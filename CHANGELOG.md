# Changelog

## 0.0.1-alpha.1 (unreleased)

The first release: the keystone, for feedback from the apps migrating onto it. Everything may change.

### Levee
- **Operations.** `[Operation(name, access)]` with effects, idempotency, declared errors (`Errors`), metadata
  (`[OperationMetadata]`) and one scope: the input's (`IScopedOperation`), the caller's (`CallerScoped`, `IScopedPrincipal`)
  or every scope (`Platform`). `Internal` operations never reach a projection. Subscriptions (`ISubscription<TItem>`).
- **The dispatcher.** Scope, authorize (`IAccessPolicy`), unit of work, admit (`IAdmission`), handler, audit, commit, for
  every call, job and subscription. `IOperationOutcome` is told how each ended, cancellations included.
  `OperationContext.Now` at the store's precision.
- **Failures.** `Result<T>` and `Error`, made only by its factories, with a closed `ErrorKind`: `internal` is the
  dispatcher's alone, for a bug, logged once under its trace. `Fields`, `RetryAfter`, `Transient`. `LeveeErrors` for
  Levee's own codes. `ProblemView`, the one wire shape for a failure. `RefuseUndeclaredErrors` for tests.
- **Ports and adapters.** `[Port]` (one adapter, a `SelectedBy` setting, or a `Many` set), `[Adapter]` with lifetimes,
  `IAdapters<TPort>`, repositories as ports; only the chosen adapter is registered. `IFlush`, `IAmbientTransaction`.
- **Personal data and secrets.** `[Secret]` (write-only, never stored unsealed) and `[Personal]` (redacted in the audit
  and outcomes), at any depth of an input: nested records, collections, dictionary values under any key, derived types,
  and a mark on an abstract or interface member holding for the property that implements it.
  Inputs are typed all the way down (LEVEE104 refuses `object`, raw JSON, `dynamic`, non-generic collections, the app's
  own included), so no member hides from either. A subscription's input, which travels in its URL, holds no `[Secret]`.
- **The strict wire.** `LeveeJson`: unknown members and missing required members refused; schemas for every contract.
- **Jobs.** `IJobs`: an operation enqueued in its caller's transaction, authorized as the caller, run as a `JobPrincipal`
  in the caller's scope; `IJobPrincipals` restores the app's own principal.

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
  `Shared`, or the app's own), checked before the first operation: a forgotten policy fails closed. Refusals classified
  as `store.stale` (no row to change) and `store.invalid_value` besides the others.
- The Postgres unit of work: one transaction per operation, `read only` when declared, `levee.scope` for row-level
  security, COMMIT never cancelled by the caller, store failures classified (undeclared writes, scope violations,
  conflicts, constraints, contention, an unreachable store).
- `LeveeSchema`: the scope functions, the append-only audit added to only through `levee_audit_write` (stamped by the
  database), isolation policies and the runtime role's grants, qualified by schema.
- The app's context refuses commands outside a transaction its unit of work began (EF's connection probe aside); a second
  context, a context the guard can't reach and a retrying execution strategy are refused before any operation runs;
  `LeveeOwner.Run()` for a Tool's owner work.

### Levee.Oxbow
- Oxbow (0.2.0-alpha.3) enlisted in the unit of work; operations as jobs, retried when transient (after the failure's own
  `RetryAfter` when it has one), sealed when they carry secrets.

### Levee.AspNetCore
- `MapLeveeOperations`: the catalog (`GET /ops`, with schemas, errors and refusals), calls (`POST /ops/{name}`) and
  subscriptions over SSE (keep-alive, the caller rechecked, an `error` event to end). Failures as
  `application/problem+json`; `LeveeProblems` for an app's own routes.

### Levee.Mcp
- `WithLeveeOperations`: the catalog as MCP tools, named by a fixed rule, through the dispatcher; bound inputs, rendered
  results, and a remote mode for a server with no dispatcher of its own.

### Levee.Cli
- `dotnet levee`: `new`, `generate operation`, `catalog`, `check`, `explain`, `db schema|isolate|grants`.
