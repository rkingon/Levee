# Levee

A .NET framework for platforms whose rules hold by construction: wrong code fails the build, or fails at the database.
The platform is a catalog of declared operations, and every call, from every client and every job, goes through one
pipeline: authorize, scope, open the unit of work, run the handler, audit, commit. See [docs/principles.md](docs/principles.md) for why.

> **0.0.1-alpha.1.** Out for feedback from the apps migrating onto it. Everything may change.

## Packages

| Package | |
|---|---|
| `Levee` | Operations, `Result`/`Error`, `Principal`, the `Dispatcher`, the unit-of-work and job ports, the strict wire (`LeveeJson`). Ships the generator and analyzers. |
| `Levee.EntityFrameworkCore` | The unit of work on EF Core and Postgres: one transaction per operation, `read only` when declared, scoped by RLS, audited. |
| `Levee.Oxbow` | `IJobs` on Oxbow: an operation enqueued in its caller's transaction, run later through the pipeline. |
| `Levee.AspNetCore` | `GET /ops` and `POST /ops/{name}`: the catalog over HTTP, no logic of its own. |
| `Levee.Mcp` | The catalog over MCP: a tool per operation a client may call, through the dispatcher, no logic of its own. |
| `Levee.Cli` | `dotnet levee`: scaffold an app, generate operations, list the catalog, print the database's SQL. Pinned per app. |

`MapLeveeOperations` (`/ops`) is the platform's own surface, for its own clients and agents. A public API, with routes,
versions and a contract outside customers rely on, is the app's to design, in a `Projection` project.

## The CLI

```sh
dotnet levee new Acme.Billing                     # a project per role, one operation end to end, the tool pinned
dotnet levee generate operation notes.create CreateNote --access member --summary "Writes a note." \
    --scoped-by Tenant --writes --idempotency conflicts --result NoteView --errors notes.too_long
dotnet levee catalog                              # builds the Host and lists what it serves (--json)
dotnet levee check                                # every LEVEE rule the app breaks, by rule and project (--json)
dotnet levee explain LEVEE004                     # a rule's why, the shape it refuses, the shape to write
dotnet levee db schema --jobs                     # the owner's DDL: Levee's functions and audit, Oxbow's tables
dotnet levee db isolate notes tenant_id --type uuid
dotnet levee db share plans                       # a table every scope reads, only platform operations write
dotnet levee db grants app_role --jobs            # what the runtime role needs
```

`dotnet levee check` runs every rule over an app, project by project (compiled once without analyzers, then each
analyzed alone), so one project's errors never hide another's, except that a project failing the generator's rules
(LEVEE101-111) stops those that reference it from compiling until it's fixed: how a consumer sees what adopting Levee
asks of it.

`generate` asks up front for everything the build would otherwise refuse (scope, idempotency of a write, a summary),
never overwrites, and writes a handler that answers `not_implemented` until it's written. `db` prints; migrations
stay the app's.

## Roles

Every project that references Levee declares one, or the build fails (LEVEE001):

```xml
<LeveeRole>Application</LeveeRole>
```

| Role | May reference | Holds |
|---|---|---|
| `Kernel` | Kernel | The shared kernel: typed ids and value types every layer may share. |
| `Contracts` | Kernel, Contracts | Operations and their views. What clients see. |
| `Domain` | Kernel, Domain | Aggregates and invariants. |
| `Application` | Kernel, Contracts, Domain, Application | Handlers and the ports they need. |
| `Adapter` | Kernel, Contracts, Domain, Application, Infrastructure | Ports' implementations: the DbContext, repositories, vendors, static data. Never each other. |
| `Infrastructure` | Kernel, Infrastructure | Plumbing several adapters share: a vendor transport, `DelegatingHandler`s. May do I/O; sees nothing of the application's; declares no ports or operations. |
| `Composition` | everything but Host, Tool and Test | The composition root hosts share: settings bound into records, hosted services, the generated catalog. Optional: without one, the Host is the root. |
| `Host` | everything but Host, Tool and Test | A process: it composes (or takes the Composition project's) and drives. It names nothing of Application's or Domain's. A Host that sees no Application (it calls one through an SDK) composes nothing. |
| `Client` | Kernel, Contracts, Client | Thin consumers over the wire. |
| `Projection` | Kernel, Contracts, Projection | The app's own public API: routes it designs (resources, versions), mapped onto operations and sent through the `Dispatcher`, failing through `LeveeProblems`. It can't see Application, Domain or adapters, so it holds no logic of theirs. A Host or Composition maps it. |
| `Tool` | what a Host sees but Client and Projection | A process run as the database's owner: migrations, the role bootstrap. Owns its transaction (LEVEE004 doesn't apply); otherwise held like a Host. Nothing references it (LEVEE017). |
| `Test` | everything | Also a composition root. |

Every project in the app declares a role: one without Levee that a Levee project references fails the build.

Kernel, Contracts, Domain and Application reference nothing else but the BCL, `Microsoft.Extensions.*.Abstractions` and
`Levee` (LEVEE008). Configuration isn't theirs: the Composition project binds settings into plain records, and Application
takes the record. A package that is pure (no I/O, no framework) can be allowed by name or prefix:

```xml
<LeveeAllowedReferences>NodaTime;Vogen*</LeveeAllowedReferences>
```

## Ports and adapters

Hexagonal, by construction. What the application needs from outside it (data, a vendor, a store) is a port, declared in
Application; handlers depend on ports, and nothing in Contracts, Domain or Application does I/O of its own (LEVEE012).
Adapters implement ports in Adapter projects and register themselves: the Host's generated composition binds every
port, or the build fails (LEVEE107–108).

```csharp
// Application: what's needed, not where it comes from
[Port(SelectedBy = "Providers:HomePrices")]
public interface IHomePrices { Task<Result<HomePrice>> FindAsync(Zip3 area, CancellationToken ct); }

// Adapters: where it comes from
[Adapter("s3")] internal sealed class S3HomePrices(...) : IHomePrices { ... }
[Adapter("embedded")] internal sealed class EmbeddedHomePrices : IHomePrices { ... }
```

`Providers:HomePrices` names the adapter, checked at startup (`AddLeveeOperations(configuration)`): always set, never
inferred. A port without `SelectedBy` has exactly one adapter; a repository (`IRepository<T>`) is a port without the
attribute, and only an aggregate root has one. `[Port(Many = true)]` is a set every adapter serves (integrations,
channels), taken as `IEnumerable<TPort>`; name its adapters (`[Adapter("ecs")]`) and it's routed too: `IAdapters<TPort>`
gives one by name, made only when asked for (a launcher per workbench). An adapter serving several ports is one instance. An adapter is scoped to the operation unless it says
`[Adapter(Lifetime = AdapterLifetime.Singleton)]` (a client a vendor's SDK wants kept). A connection of its own, even
one the container hands it (to a constructor, a minimal API handler's or an action's parameter, an injected property, `GetService`),
is outside every operation (LEVEE018): a listener that truly needs one suppresses at that
line, with why. The Host composes and
drives: it names nothing of Application's or Domain's, and reaches them only through the dispatcher (LEVEE013).

## An operation

```csharp
// Contracts
[Operation("notes.create", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record CreateNote(Guid Id, string Tenant, string Text) : IOperation<NoteView>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

[Operation("tenants.list", "platform-admin", Platform = true)]
public sealed record ListTenants : IOperation<IReadOnlyList<TenantView>>;

// Application
internal sealed class CreateNoteHandler(INotes notes) : IOperationHandler<CreateNote, NoteView>
{
    public Task<Result<NoteView>> HandleAsync(CreateNote op, OperationContext context, CancellationToken ct) { ... }
}

// Host
services.AddLeveeOperations(configuration);     // generated: the catalog, the dispatcher, every handler and adapter, ports bound
services.AddLeveePostgres<AppDbContext>();      // the unit of work
services.AddLeveeOxbow().AddLeveeJobs();       // Oxbow in the operation's transaction; IJobs on top
services.AddSingleton<IAccessPolicy, MyAccess>(); // required: the app's authorization
app.MapLeveeOperations(http => MyAuth.PrincipalAsync(http)); // a lookup if need be; both endpoints need a caller
```

- `Access` is the app's vocabulary; its `IAccessPolicy` decides, once, before the unit of work opens. Checks that need
  the transaction (a membership row, a lease) are an `IAdmission`.
- An operation declares only reads unless it says otherwise, and runs in a `read only` transaction: an undeclared write
  is `effects.undeclared`, refused by Postgres.
- Every operation declares one scope (LEVEE106): its input's (`IScopedOperation`), its caller's (`CallerScoped = true`, from
  a principal that is an `IScopedPrincipal`: an API key issued to one tenant), or every scope (`Platform = true`). The
  dispatcher refuses a missing or blank scope key. A job runs in the scope its enqueuer carried. `ScopeKey` sets `levee.scope` for the
  transaction, and `LeveeSchema.Isolation(table, column)` is the RLS policy that holds a table to it (`Shared(table)` for one every scope
  reads); a table the app's context maps without a policy, or a view it maps that reads one, fails before the first operation. Platform scope
  (`levee.platform`) sees every row, so the access policy decides who may run a platform operation
  (`AccessRequest.Operation.Platform`).
- Every write is audited in the operation's own transaction (`levee_audit`, append-only, `[Secret]` and `[Personal]`
  inputs redacted, a mark on an abstract or interface member holding for the property that implements it (an explicit
  implementation returning another property doesn't carry it: mark that one): an append-only record can never forget personal data, so it never holds it). Denial logs name the
  operation and the reason, not the caller.
  `[Secret]` is property-only: on a positional record it's `[property: Secret]`, and a bare one doesn't compile.
- A job is an operation: `jobs.EnqueueAsync(new CreateNote(...))` is authorized as the caller, commits or rolls back
  with the caller's transaction, and runs through the dispatcher as a `JobPrincipal` for the caller (the audit names
  them; the access policy decides again). Jobs run at least once, so an operation declaring `Idempotency.None` can't be
  enqueued, nor one with a `[Secret]` anywhere in its input (job inputs are stored) unless jobs seal them
  ([docs/jobs.md](docs/jobs.md)).
- Oxbow is enlisted in the unit of work (`AddLeveeOxbow()`): the app's own job kinds, through the scoped `IOxbowClient`,
  commit or roll back with the operation too. `.AddLeveeJobs()` adds operations as jobs on top.
- `Internal = true`: the platform's own operations (sign-in, bootstrap), through the same pipeline, never projected. The
  dispatcher's `FromClient` methods, which every projection uses, refuse them; in-process calls and jobs reach them.
- A subscription (`ISubscription<TItem>`, `ISubscriptionHandler`) is declared like an operation, scoped, authorized and
  admitted as it opens and, by the HTTP projection, rechecked while it streams, holds no transaction, and is projected as server-sent events
  ([docs/subscriptions.md](docs/subscriptions.md)).
- Jobs with `[Secret]` inputs are sealed at rest with Oxbow's args encryption: `AddLeveeJobs(sealSecrets: true)` and
  `AddArgsEncryption()`. Without it they're refused.
- `OperationContext.Now` is the handler's one time, at Postgres's microseconds. The audit's own time is the database's
  `now()`, so a caller can't backdate it.
- Only the dispatcher commits, but an adapter may flush: `IFlush` sends pending writes inside the open transaction, so a
  later query or set-based statement sees them, and refuses outside an operation. `SaveChanges` stays banned (LEVEE004).
- Jobs run for whoever enqueued them: register an `IJobPrincipals` to turn that subject back into the app's own principal.
  A process that works no queue only enqueues, and may enqueue kinds other processes work.
- A test over an app with a Composition project adds its own operations on top: `AddLeveeOperations(...)` then the
  generated `AddLeveeTestOperations()`. Only generated code builds the catalog (LEVEE020).
- An app's own attributes marked `[OperationMetadata]` are captured into the catalog (`descriptor.Get<T>()`) and shown by
  `GET /ops` beside each operation's input and result schemas.
- A Contracts project carries its own catalog for clients: `<AssemblyName>.LeveeCatalog.Catalog`, every operation it
  declares but the internal ones, with their metadata and errors. A CLI, an MCP server or an app's access check reads the
  contract from it, offline, without the application.
- `IOperationOutcome` is told how every operation ended, after its unit of work committed or rolled back, with the input
  as the audit keeps it.

## Failures

A handler returns a failure, never throws one: `Result<T>` holds a value or an `Error` (a closed `ErrorKind`, a dotted
`Code` a client branches on, a `Message` for a person). An outcome the domain expects that isn't a failure (a refusal that
must be recorded, a declined application) is part of the result type.

- **Declared.** `[Operation(..., Errors = ["notes.too_long"])]` lists what the handler fails with: the catalog shows it
  beside the input and result, and a literal code the handler doesn't declare fails the build (LEVEE110). Levee's own
  codes (`LeveeErrors`) come on top of every operation's. A code the build can't see (from a domain method, a helper)
  is logged when returned undeclared; tests set `RefuseUndeclaredErrors` to fail on it instead.
- **Refusals.** The access policy and admissions declare what they refuse with (`[Refuses(...)]`, LEVEE111); the
  catalog lists those once, for every operation (`OperationCatalog.Refusals`, `refusals` in `GET /ops`).
- **Bugs.** A handler that throws is `internal`: the dispatcher rolls back, logs the exception once under the trace,
  tells `IOperationOutcome`, and answers with nothing but the trace to quote. A subscription that throws mid-stream ends
  with `OperationFailedException` (over HTTP, a last `error` event).
- **Retries.** `Error.Transient` (`Unavailable` and `internal`) means the same call may succeed later: jobs retry it with
  Oxbow's backoff, or after the error's own `RetryAfter` when it has one, and cancel on anything else. The store's contention (serialization failures, deadlocks) and an
  unreachable database are `Unavailable`. `Error.Unavailable(code, message, retryAfter)` carries a vendor's wait.
- **Fields.** `Error.Validation(code, message, fields)` says what's wrong with each field by its path; malformed input
  names the field it broke on.
- **Over HTTP**, every failure is `application/problem+json` (RFC 9457), a bug in a policy, an admission or an input's own
  validation included: `title`, `status`, `detail`, `code`, `kind`,
  `transient`, `trace` and, for validation, `errors` by field. `Unavailable` with a wait sets `Retry-After`.

## MCP

[docs/mcp.md](docs/mcp.md) has the options.

```csharp
services.AddMcpServer()
    .WithHttpTransport()                                  // or stdio, in-process for an agent's turn
    .WithLeveeOperations(request => Principals.Of(request.User), include: d => d.Get<AgentTool>() is not null);
```

`WithLeveeOperations(o => ...)` takes the rest: `BoundInputs` and `Bind` for inputs the server supplies rather than the
model (a turn's id: out of the schema, filled on every call), `Render` for a result that's better as other content (an
image), and `Send` with `Catalog` for an MCP server with no dispatcher of its own, sending to the app's `/ops`.

One tool per operation a client may call (not internal, answering once), named by a fixed rule: `notes.create` is
`notes_create`, and two operations that would share a name fail at startup. Each tool carries its input schema and
hints (read-only, idempotent, destructive, open-world) from the contract. A call goes through the dispatcher's client
path as the principal `WithLeveeOperations` resolves; a failure answers as an error with the same problem body
(`ProblemView`) HTTP sends. Like `/ops`, it's the platform's own surface, for its agents.

Projections an app writes itself (typed REST routes, a webhook) answer failures through `LeveeProblems`, exactly as
`MapLeveeOperations` does.

## Postgres

The owner runs `LeveeSchema.Sql` (or `migrationBuilder.AddLeveeSchema()`) and each table's `Isolation` or `Shared`; the app connects
as a role that is not a superuser, owns nothing and doesn't bypass RLS, with what `LeveeSchema.Grants(role)` gives it. That
role adds to the audit only through `levee_audit_write`, which stamps its own time, so it can't change, remove or backdate a
record. The app's context refuses any command outside an operation, and a table or view it maps that row-level security
doesn't hold. [docs/postgres.md](docs/postgres.md) has the setup,
and [the trust model](docs/decisions.md#trust-model) where the guarantees stop.

## Rules

Every message says what's wrong, then `Fix:` and the conventional shape, then `(dotnet levee explain LEVEEnnn)`: the
build output alone is enough to write it right. [docs/rules.md](docs/rules.md) is the long form `explain` prints.

| ID | Rule |
|---|---|
| LEVEE001 | A project that references Levee declares its `LeveeRole`. |
| LEVEE002 | A role references only the roles it may (table above). |
| LEVEE003 | No `DateTime.Now`/`UtcNow`/`Today` or `DateTimeOffset.Now`/`UtcNow` outside tests; `Guid.CreateVersion7()` with no time; `TimeProvider.System` only where the app composes (Composition, Host, Tool). |
| LEVEE004 | No `SaveChanges`, transaction control, `Commit`/`Rollback`, or opening or committing Levee's unit of work outside tests: the dispatcher commits. |
| LEVEE005 | No service location (`GetService`, keyed services, `ActivatorUtilities`) outside where the app composes (Composition, Host, Tool) or tests. |
| LEVEE006 | A Levee rule is suppressed only beside the code with a reason: `Justification`, or a `// reason` on the pragma. Never project-wide: an assembly attribute, `NoWarn`, a ruleset, or an `.editorconfig` or global config severity fails the build. Not configurable itself. |
| LEVEE007 | In a Domain project, an aggregate root's state, collections included, is changeable only from inside it. |
| LEVEE008 | Kernel, Contracts, Domain and Application reference only the BCL, abstractions, Levee and `LeveeAllowedReferences`; never hosting. |
| LEVEE009 | Nothing referenced uses Levee without having been built with its analyzers (`ExcludeAssets="analyzers"`). |
| LEVEE010 | Ports (`[Port]`, repositories) are declared in Application. |
| LEVEE011 | A port is implemented only in an Adapter project, and an adapter implements nothing of Application's but ports. |
| LEVEE012 | Kernel, Contracts, Domain and Application do no I/O: no HTTP, sockets, files, databases, processes, console or environment. Naming an exception or an enum isn't I/O. |
| LEVEE013 | A Host names or holds nothing of Application's or Domain's (`var` included): it reaches them through the dispatcher. |
| LEVEE014 | A Composition project's public surface is extension methods that register and map, taking and returning none of Application's, Domain's or an adapter's types. |
| LEVEE015 | Composition wires the application (constructs, binds, registers) and never calls into it, delegates included: what it maps goes through the dispatcher. |
| LEVEE016 | An Application or Domain type implements no Kernel, Contracts or package interface (Levee's and its own ports' aside), and derives from no shared abstract class: it's reached only through operations. |
| LEVEE017 | A Tool is an executable nothing references. |
| LEVEE018 | No connection of its own (`new NpgsqlConnection`, a data source, a context factory) outside a Tool: data is reached through the operation's unit of work. |
| LEVEE019 | Aggregate roots live in Domain. |
| LEVEE020 | Only generated code builds the catalog: no descriptor, catalog or catalog part by hand outside tests. |
| LEVEE101–103 | Every operation has exactly one handler, and names are unique (checked where the catalog is built). |
| LEVEE104 | An operation is a public top-level class with `[Operation(name, access)]` and `IOperation<T>`; names are lowercase and dotted. |
| LEVEE105 | An operation with effects declares its idempotency. |
| LEVEE106 | An operation is scoped or declared platform-wide, never neither or both. |
| LEVEE107 | Every port the Host sees has an adapter. |
| LEVEE108 | A port is bound one way: one unnamed adapter, or named adapters chosen by its `SelectedBy` setting. |
| LEVEE109 | An `[Adapter]` adapts a port. |
| LEVEE110 | A handler fails only with the codes its operation declares (or Levee's own). |
| LEVEE111 | An access policy or admission refuses only with the codes its `[Refuses]` declares; the catalog lists them once. |

## Decisions

[docs/decisions.md](docs/decisions.md): what consumers asked for, what Levee did, and why, including what it declined.

## Build

```sh
dotnet build Levee.slnx
dotnet test Levee.slnx      # the integration tests need Docker
dotnet pack Levee.slnx -c Release -o artifacts
```
