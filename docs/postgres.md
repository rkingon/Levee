# Postgres

Levee's unit of work is a Postgres transaction per operation, held by row-level security to the operation's scope.

## Setup, as the owner

The owner (the role that owns the schema; never the app's) runs, once:

```sh
dotnet levee db schema [--jobs] [--schema S]   # Levee's functions and audit, and Oxbow's tables with --jobs
dotnet levee db isolate notes tenant_id --type uuid
dotnet levee db share plans                    # a table every scope reads, only platform operations write
dotnet levee db grants app_role [--jobs]       # what the runtime role needs
```

or the same from a migration: `migrationBuilder.AddLeveeSchema()`, `EnableScopeIsolation("notes", "tenant_id", "uuid")`,
`ShareAcrossScopes("plans")`.
`LeveeSchema.Create(schema)` and the `schema` arguments put Levee's objects and the app's tables in a schema other than
`public` (Oxbow's tables stay in `public`); every object is
qualified by it, so policies and grants hold whatever a role's `search_path` is.

`tests/Levee.IntegrationTests/PostgresFixture.cs` is the whole setup, run against Postgres 16 and 17 in CI.

## The runtime role

The app connects as a role that is not a superuser, owns nothing and doesn't bypass row-level security, granted only
what `LeveeSchema.Grants(role)` prints:

- every table's rows, now and as migrations add tables; row-level security still holds each to the operation's scope.
  Everything the app's context maps, however it's mapped (split across tables, as a view, found through the search path),
  is judged by what it is in the database, and every view among it by what it reads, down to the tables:
  - a table must exist and have forced row-level security and a policy (`Isolation` or `Shared`, or one of the app's
    own);
  - a view must be `security_invoker` or owned by a role that is neither a superuser nor bypasses row-level security,
    since a view reads with its owner's rights;
  - a materialized view or a foreign table is refused: row-level security can't hold either.

  What fails is found before the first operation, which fails with the fix, so a forgotten policy fails closed rather
  than opening the table to every tenant. Not followed: a table the context doesn't map at all, and what a function or a
  mapped SQL query reads;
- `levee_scope()` and `levee_platform()`, which the policies call;
- the audit: `select`, and adding a record only through `levee_audit_write`, which runs with the owner's rights and stamps
  the transaction's own time. No `insert`, `update` or `delete` of its own: the role can't change, remove or backdate a
  record.

## Per operation

`AddLeveePostgres<TContext>()` makes each operation's unit of work the scoped context and one transaction on it:

1. `begin`; `set transaction read only` when the operation declares only reads.
2. `levee.scope` (the operation's scope key) and `levee.platform` (`on` for a platform operation), local to the transaction.
3. The handler, with `IFlush` to make pending writes visible to a later query in the same transaction.
4. The audit record, for a write, through `levee_audit_write`.
5. `commit`, never cancelled by the caller once sent; or a rollback, on any failure.

Postgres's refusals come back classified: a write in a read-only transaction is `effects.undeclared`, a row inserted or
moved outside the scope is `scope.violation`, an update or delete that finds no row to change (another write changed it
first, or row-level security hides it from this scope) is `store.stale`, a duplicate is `conflict`, any other constraint
or trigger refusal is `store.constraint`, a value its column's type refuses (a scope key that isn't the policy's uuid) is
`store.invalid_value`, and contention or an unreachable database is `Unavailable` (so jobs retry it).

## Outside an operation

The app's context refuses every command that isn't in a transaction the unit of work began: a seeder, a context a Host
resolved, a hosted loop, `SaveChanges` in EF's own transaction. None of those is authorized, scoped or audited, so none
runs. EF's connection probe (`SELECT 1`, from `CanConnect` and the health check on the context) is let through: it reads
and changes nothing. A Tool's owner work (migrations, the role bootstrap) runs inside `LeveeOwner.Run()`, which the build
allows only in a Tool (LEVEE018); runs nest.

The guard is on the context `AddLeveePostgres` names, so another context registered beside it (`AddDbContext`,
`AddDbContextFactory`, or the context itself, by its type or behind an interface) fails the host's start: map its entities
in the one context. The guard reaches the context through `DbContextOptions<TContext>`, so a context that configures
itself in `OnConfiguring` instead fails the host's start too (outside a host, its first transaction), with the fix.

A retrying execution strategy (`EnableRetryOnFailure`) can't run a transaction its caller began, so it's refused the same
way: a transient failure answers `Unavailable`, which a client may repeat and a job retries.

The build refuses the other ways to a connection of the app's own outside a Tool: `new NpgsqlConnection`, a data
source's connections, a context factory or a design-time factory's `CreateDbContext`, `new` on a context, the context's
own connection (`GetDbConnection`; `IAmbientTransaction` has the operation's) or its internal services (`GetService<T>`),
SQL that sets the scope settings or writes the audit by hand, and transaction control (LEVEE004, LEVEE005, LEVEE018).
See the [trust model](decisions.md#trust-model) for where these guarantees stop.
