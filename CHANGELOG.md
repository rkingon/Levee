# Changelog

## 0.0.1-alpha.1

The keystone, for feedback from the apps migrating onto it.

- `Levee`: `[Operation(name, access)]` with effects, idempotency and declared platform scope, `Result<T>` and a closed
  `ErrorKind`, `Principal`, `JobPrincipal`, the `Dispatcher` (scope, authorize, unit of work, admission, handler, audit,
  commit), the strict wire, `IJobs`, `IAggregateRoot`. `[Secret]` is property-only.
- Generator: the catalog and handler registration across role-tagged assemblies; LEVEE101–106.
- Analyzers: roles (LEVEE001–002), wall clock (003), commits (004), service location (005), suppression reasons,
  including NoWarn and config severities (006), aggregate state (007), packages of the inner roles (008), assemblies
  built without the analyzers (009).
- Ports and adapters: `[Port]` (with `SelectedBy` to choose between named `[Adapter]`s by setting), repositories as ports,
  adapters registered and ports bound by the generated composition; LEVEE010–013 and LEVEE107–109. Adapters no longer
  reference each other. `AddLeveeOperations(configuration)`.
- Roles: `Kernel` (the shared kernel) and `Composition` (the root hosts share, where the catalog is generated).
- Internal operations, app metadata on operations (`[OperationMetadata]`), `IOperationOutcome`, an async principal for
  the HTTP projection, input and result schemas in `GET /ops`.
- Ports: `Many` sets, adapter lifetimes. Oxbow enlisted in the unit of work for any job (`AddLeveeOxbow`), jobs as
  operations on top (`AddLeveeJobs`). `IAmbientTransaction`.
- Fixed: `LeveeAllowedReferences` keeps every `;`-separated entry; LEVEE012 no longer flags exceptions and enums.
- Subscriptions (`ISubscription<TItem>`), streamed over SSE. Routed sets (`IAdapters<TPort>`). Sealed jobs for secret inputs.
  The dispatcher's client paths (`SendFromClientAsync`, `SubscribeFromClientAsync`) refuse internal operations.
- LEVEE014; LEVEE004 covers Levee's unit of work; LEVEE008 refuses hosting in inner roles.
- Fixed: nullable results; an adapter serving several ports is one instance; a Host that sees no Application composes
  nothing; `OperationContext.Now` at microseconds; `IOperationOutcome` gets the redacted input.
- `IFlush` (an adapter reads its own writes; only the dispatcher commits), `IJobPrincipals`, catalog parts and
  `AddLeveeTestOperations`, enqueue-only processes skip Oxbow's unknown-kind check, a routed set of singletons is a singleton.
- LEVEE015; LEVEE014 covers extension signatures; LEVEE013 covers what a Host holds, not only what it names.
- `Levee.EntityFrameworkCore`: the Postgres unit of work (`read only` when declared, `levee.scope` for RLS), the append-only,
  scope-isolated audit, `LeveeSchema` and migration helpers.
- `Levee.Oxbow`: jobs as operations, authorized as and run for their caller, enqueued in the caller's transaction;
  operations not safe to repeat or carrying secrets are refused.
- `Levee.AspNetCore`: `GET /ops`, `POST /ops/{name}`, both for a caller.
- `Levee.Cli` (`dotnet levee`): `new`, `generate operation`, `catalog`, `check`, `db schema|isolate|grants`.
  `LeveeSchema.Grants(role)` for the runtime role.
- Every diagnostic states its fix and points to `dotnet levee explain <id>`, which prints the rule's long form
  (docs/rules.md, shipped inside the tool). `check` gives the pointer once per rule.
- Failures: a handler that throws is `ErrorKind.Internal`, rolled back, logged once under its trace, told to
  `IOperationOutcome`, answered with the trace alone; a subscription that throws ends with `OperationFailedException`.
  Operations declare their error codes (`Errors`), shown in the catalog and checked by LEVEE110; `LeveeErrors` holds
  Levee's own (`StoreErrors` folded in). `Error` carries `Fields`, `RetryAfter`, `Trace` and `Transient`. HTTP failures
  are `application/problem+json` with `Retry-After`; SSE ends with an `error` event. Postgres contention and an
  unreachable store are `Unavailable`; jobs retry transient failures and cancel the rest. `Classify` moved from
  `IUnitOfWork` to `IUnitOfWorkFactory`, so a failure to begin is classified too. `generate operation --errors`.
- `LeveeOptions.RefuseUndeclaredErrors`, for tests: an undeclared code answers `internal` instead of only being logged.
- LEVEE016: an Application class implements no Kernel or Contracts interface. LEVEE013 and LEVEE015 see delegates to the
  application's methods. Fixed: a test composes only the fakes it marks `[Adapter]`, not every port implementation.
- Fixed: LEVEE012 no longer flags a doc comment's `cref`; `check` takes the solution's projects, not every project on
  disk; a constraint or trigger refusal is `Conflict` (`store.constraint`), not a transient `internal`; COMMIT isn't
  cancelled by the caller; generated public types are documented; `Microsoft.Extensions.ObjectPool` is allowed.
- LEVEE004 refuses transaction control in raw SQL. LEVEE008 refuses frameworks in `LeveeAllowedReferences`.
- An adapter its port's setting didn't choose isn't registered. `LeveeProblems` maps an `Error` to HTTP for an app's
  own routes. `LeveeSchema` takes the schema Levee's objects live in (`db --schema`), and qualifies everything by it.
- Public API tracked per package (`PublicAPI.Shipped.txt`/`Unshipped.txt`).
- Caller-scoped operations (`CallerScoped = true`, `IScopedPrincipal`): the scope comes from the caller's credentials, not
  its input. LEVEE106 requires exactly one of input, caller or platform. Jobs carry the enqueuer's scope.
- The `Infrastructure` role (plumbing adapters share) and the `Tool` role (a process run as the owner, LEVEE017; LEVEE004
  doesn't hold it). Every project a Levee project references uses Levee with a role (an MSBuild LEVEE001), tests aside.
  LEVEE011: an adapter implements no interface of Application's, Domain's, Kernel's or Contracts' but ports. Operations
  are declared in Contracts, or Application for the platform's own (LEVEE104).
- `[Refuses]` on an access policy or admission: its codes, listed once in the catalog (`Refusals`, `refusals` in
  `GET /ops`), checked by LEVEE111.
- `[Personal]`: personal input reaches the handler and the result, but the audit and `IOperationOutcome` keep
  `[personal]`. Denial logs no longer name the subject.
- Oxbow 0.2.0-alpha.3: a job whose operation fails `Unavailable` with a `RetryAfter` retries after exactly that wait
  (`JobRetryException`), not Oxbow's backoff.
- `Levee.Mcp` options: bound inputs, rendered results, and a remote mode (`Send`, `Catalog`) for a server beside the client.
  Fixed: an SSE stream sends its status as it opens, not with its first item.
- `Levee.Mcp`: `WithLeveeOperations` projects the catalog as MCP tools, named by a fixed rule, with schemas and hints from
  the contract, calling through the dispatcher's client path. `ProblemView.From` and `StatusOf` are core, shared by
  HTTP and MCP.
- The `Projection` role: an app's own public API, seeing only Kernel and Contracts. `/ops` is the platform's internal surface.
- Fixed: LEVEE016 no longer flags an operation carrying a shared marker. Under `RefuseUndeclaredErrors` the answer names the
  undeclared code. `ProblemView` moved to `Levee`, so a client reads failures without ASP.NET. SSE streams send a
  `keepalive` event and recheck their caller (`SubscriptionKeepAlive`, `SubscriptionRecheck`), ending with an `error` event
  when it no longer holds. `Dispatcher.AuthorizeFromClientAsync`.
- docs/decisions.md: every consumer request, built, held or open, with why.
- A Contracts project generates `<AssemblyName>.LeveeCatalog`: its public operations' descriptors, for clients that
  see only Contracts.
