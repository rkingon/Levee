# Changelog

## Unreleased

### Levee
- **Breaking:** the unit of work is Levee's alone. `IUnitOfWorkFactory`, `IUnitOfWork` and `AuditEntry` are internal: an
  app's own would keep the transaction and lose what Levee's Postgres unit of work holds (row-level security checked at
  startup, the guard on the context, the audit, classified failures). `AddLeveePostgres<TContext>()` is the unit of work.
- An input built in-process with null where its type forbids it is refused as `input.invalid` before any transaction
  opens, as the wire refuses it, not failed as `internal` by the audit's redaction. Each call's input is redacted once,
  for the audit and the outcome both.
- **Breaking:** internal by default, public only what an app writes against. Internal now: the dispatcher's by-name and
  JSON entry points (`SendJsonAsync`, `SendFromClientAsync`, `SubscribeJsonAsync`, `SubscribeFromClientAsync`,
  `AuthorizeAsync`, `AuthorizeFromClientAsync`), which Levee's projections use for an app; `CurrentOperation`; and
  `LeveeJson.Redact`, `FindSecret`, `FindSecretIn` and `Schema`, which the audit, outcomes, jobs and catalog apply
  themselves. In `Levee.AspNetCore`, `OperationView` (the catalog's wire shape); in `Levee.Oxbow`,
  `OperationUnavailableException`.

### Levee.Testing
- New: `LeveePostgres.StartAsync`, Postgres for pipeline tests as production has it (Testcontainers): the owner runs
  Levee's schema, the app's migrations and its SQL, and the app connects as a role that owns nothing and doesn't bypass
  row-level security. Referenced from Test and TestSupport projects only.

### Levee.EntityFrameworkCore
- `LeveeSchema.Grants` leaves EF's migration history to the owner; the docs say to run the grants after migrations and
  before the app's own narrowing, which a blanket grant would undo.

### Generator and analyzers
- LEVEE006 leaves .NET's own source generators' output alone (`[GeneratedRegex]` emits a bare `#pragma warning disable`),
  and holds every other generator's to the rule.
- LEVEE004 leaves an EF migration's SQL alone: it's the owner's, run at migrate, so it may backfill the audit.

### Levee.Cli
- `dotnet levee db isolate` and `share` take a table in a schema of its own (`billing.invoices`), as the docs say.
- `dotnet levee check` lists every suppressed LEVEE rule with its reason and fails until none is left: a suppression lets
  a migration build, never makes it green.
- `dotnet levee check` takes stock partway through a migration: projects that don't compile are named, and the rest are
  still checked.

### Generator and analyzers
- `[Port(SelectedBy = "...", Default = "none")]`: an optional port's null object serves when its setting isn't set, so
  only a deployment that uses the port configures it (LEVEE108 holds the default to an adapter that exists).
- LEVEE015 lets Composition call an adapter's own registration, one `IServiceCollection` extension in its Adapter
  project for what it needs beyond its ports; nothing else of an adapter's.
- A Tool binds the ports it references, not every port the app declares (LEVEE107 holds a Host and Composition to all).
- LEVEE020 refuses calling the composition's plumbing by hand (a generated `LeveeRegistration_*`, `PortSelection`),
  which is public only because the root's generated code calls it; the generated registration is hidden from IntelliSense.
- A `TestSupport` role for what several test projects share (fixtures, fakes, a harness): held to no code rule, like a
  test, and referenced by tests alone.
- Every rule's help link opens its page on the site (`https://rkingon.github.io/Levee/rules/levee0nn/`).

### Documentation
- The Starlight site includes a NuGet quick start, the guides, and a stable reference page for each build rule. Pages preload their self-hosted fonts to keep text stable on first load.

## 0.0.1-alpha.1 (2026-11-26)

The first release: the keystone, for feedback from the apps migrating onto it. Everything may change.

### Levee
- **Operations.** `[Operation(name, access)]` with effects, idempotency, declared errors (`Errors`), metadata
  (`[OperationMetadata]`) and one scope: the input's (`IScopedOperation`), the caller's (`CallerScoped`, `IScopedPrincipal`)
  or every scope (`Platform`). `Internal` operations never reach a projection. Subscriptions (`ISubscription<TItem>`).
- **The dispatcher.** Scope, authorize (`IAccessPolicy`), unit of work, admit (`IAdmission`), handler, audit, commit, for
  every call, job and subscription. `IOperationOutcome` is told how each call ended, cancellations included, and how each subscription opened,
  within `LeveeOptions.OutcomeTimeout`, never failing the operation. A principal's scope is read once per call.
  `OperationContext.Now` at the store's precision.
- **Failures.** `Result<T>` and `Error`, made only by its factories, with a closed `ErrorKind`: `internal` is the
  dispatcher's alone, for a bug, logged once under its trace. `Fields`, `RetryAfter`, `Transient`. `LeveeErrors` for
  Levee's own codes (among them `store.stale`, `store.invalid_value`, `caller.unconfirmed`). Input its own type refuses is
  `input.invalid`; a result is serialized before the commit. `ProblemView`, the one wire shape for a failure. `RefuseUndeclaredErrors` for tests.
- **Ports and adapters.** `[Port]` (one adapter, a `SelectedBy` setting, or a `Many` set), `[Adapter]` with lifetimes,
  `IAdapters<TPort>`, repositories as ports; only the chosen adapter is registered. `IFlush`, `IAmbientTransaction`.
- **Personal data and secrets.** `[Secret]` (write-only, never stored unsealed) and `[Personal]` (redacted in the audit
  and outcomes). Redaction is the serializer's own pass: each marked member is written as its marker wherever the
  serializer writes it (nested records, collections, nullable values, interfaces, derived and fallback types), a mark on
  an abstract or interface member holding for the property that implements it, and a value its own converter writes whole
  marked whole when its type carries a mark. `LeveeJson.FindSecretIn` finds a secret in a value the same way.
  Inputs are typed all the way down (LEVEE104 refuses `object`, raw JSON, `dynamic`, non-generic collections, the app's
  own included), so no member hides from either. A subscription's input, which travels in its URL, holds no `[Secret]`.
- **The strict wire.** `LeveeJson`: unknown members and missing required members refused; schemas for every contract.
- **Jobs.** `IJobs`: an operation enqueued in its caller's transaction, authorized as the caller, run as a `JobPrincipal`
  in the scope it was authorized in; `IJobPrincipals` restores the app's own principal, and a run restored as another
  subject or scope is cancelled.

### Generator and analyzers (shipped in `Levee`)
- Roles: Kernel, Contracts, Domain, Application, Adapter, Infrastructure, Composition, Host, Client, Projection, Tool,
  Test; every project a Levee project references declares one.
- The catalog, handler and adapter registration, and port binding, generated across the app's assemblies; a Contracts
  project's own `LeveeCatalog` for clients.
- LEVEE001–020 and LEVEE101–111, each with `Fix:` in its message and a long form in `dotnet levee explain`. Turning them
  off, by property, config severity, a global config, a generated-file header or a Test role on a project with no test
  framework, is itself refused (an IDE's design-time pass excepted).

### Levee.EntityFrameworkCore
- Every table the app's context writes to, however it's mapped, has forced row-level security and a policy (`Isolation`,
  `Shared`, or the app's own), and every view it maps is judged by what it reads, down to the tables,
  checked before the first operation: a forgotten policy fails closed. Refusals classified
  as `store.stale` (no row to change) and `store.invalid_value` besides the others.
- The Postgres unit of work: one transaction per operation, `read only` when declared, `levee.scope` for row-level
  security, COMMIT never cancelled by the caller, store failures classified (undeclared writes, scope violations,
  conflicts, constraints, contention, an unreachable store).
- `LeveeSchema`: the scope functions, the append-only audit added to only through `levee_audit_write` (stamped by the
  database), isolation policies (`Isolation`, and `Shared` for tables every scope reads) and the runtime role's grants,
  qualified by schema.
- The app's context refuses commands outside a transaction its unit of work began (EF's connection probe aside); a second
  context, a context the guard can't reach and a retrying execution strategy are refused before any operation runs;
  `LeveeOwner.Run()` for a Tool's owner work.

### Levee.Oxbow
- Oxbow (0.2.0-alpha.3) enlisted in the unit of work; operations as jobs, retried when transient (after the failure's own
  `RetryAfter` when it has one), sealed when they carry secrets.

### Levee.AspNetCore
- `MapLeveeOperations`: the catalog (`GET /ops`, with schemas, errors and refusals), calls (`POST /ops/{name}`) and
  subscriptions over SSE (keep-alive, the caller rechecked in the subject and scope it opened with, an `error` event to
  end). Failures as
  `application/problem+json`; `LeveeProblems` for an app's own routes.

### Levee.Mcp
- `WithLeveeOperations`: the catalog as MCP tools, named by a fixed rule, through the dispatcher; bound inputs, rendered
  results, and a remote mode for a server with no dispatcher of its own.

### Levee.Cli
- `dotnet levee`: `new`, `generate operation`, `catalog`, `check`, `explain`, `db schema|isolate|share|grants`.
