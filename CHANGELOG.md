# Changelog

## Unreleased

### Levee
- `[Operation(Unscoped = true)]`: an operation in no one's scope, for what touches no tenant's rows (a health check, a
  lookup in a `Shared` table). It sees no tenant's rows, where `Platform = true` sees every one; `WorkScope.Unscoped`
  tells an access policy which it is.
- `AnonymousPrincipal`: a caller with no credentials, for what anyone may do. An operation in its caller's own scope,
  or across every scope (platform), answers it `Unauthenticated` (401), whatever the access policy grants: what anyone
  may do is `Unscoped`. A policy from alpha.1's scaffold (`"anyone" when Scope.Key is null`) grants unscoped operations now.
- `OperationContext.NewId()`: a UUIDv7 at the operation's time, later than every id the operation made before it (a
  42-bit counter in the version's sequence bits, RFC 9562's method 1, seeded from random bits of its own, so every id
  keeps the operation's own time and operations in one millisecond don't collide), where
  `Guid.CreateVersion7(context.Now)` would order ids minted in one operation at random.
- Processes: `IProcess<TInput>`, long-running work an operation starts with `IJobs.StartAsync` (inserted in its
  transaction, run only if it commits) and `Levee.Oxbow` runs as a job, outside any transaction. Each step is an operation
  it sends through `ProcessContext`, as its starter, in their scope; `Snooze` runs it again later, from the top, reading
  where it was. The generated registration finds a process in Application. A process started in a scope (or in none) stays in it, step
  by step, its principal (each run's own copy) held there by whatever path it reaches the dispatcher; one the platform started may act in any. A stored process resolves only to a registered process's input, and
  one that snoozes before its first await isn't counted as failed.
- `IChangeSignals`: change notifications, the store's own. Raised in an operation and delivered when it commits, never if it
  rolls back; watched from a subscription's handler in the scope the dispatcher authorized (every scope's for a platform
  one). A platform operation names the scope it changed; one with no scope is an unscoped
  operation's, heard only by unscoped watchers. `WatchAsync` returns once Levee listens, so watching, reading, then following misses nothing, and a signal with no
  key after the listening connection comes back says to read again. A signal is a kind and a key, a hint to read again.
  `Levee.EntityFrameworkCore` serves it over one Postgres `LISTEN` connection per process, Levee's own.
- `[ValueObject(typeof(string), Format = ..., Pattern = ...)]`: a value object written by its own converter says what
  it writes, and every schema (the catalog's, each MCP tool's) shows that, nullable where the member is; a member's
  `[Description]` is its description in every schema, a nullable member with a default included; a nullable reference
  value object allows null. LEVEE104 refuses a contract type written by its own converter
  without one, and a value object's converter is handed only the JSON its `[ValueObject]` says it writes. A value object is
  one JSON scalar; LEVEE104 refuses any other wire, and so does the contract wire.
- **Breaking:** the contract wire is Levee's, one and unconfigurable. `LeveeJson.Create` and `LeveeOptions.Json` are gone:
  every schema Levee shows is `LeveeJson.Options`'s, so a setting changed would make each a promise the wire breaks.
  It's strict (unknown members refused, nullability and required parameters respected, every member written) and numbers
  are numbers both ways (Web's reading of one from a string is off). A value's JSON is its value object's own: a
  converter goes on a `[ValueObject]` type, never a member, and a `[ValueObject]` has one. A contract can't loosen the wire
  (`[JsonNumberHandling]`, `[JsonUnmappedMemberHandling(Skip)]`, `[JsonExtensionData]`, any `[JsonIgnore]`: a member off
  the wire is skipped when sent, not refused, so a contract carries what's on its wire and nothing else). Its resolver is Levee's own and sealed, with nothing to add to. Contracts are written in a small, closed language, so a type's schema is its parameter list and the wire can't
  disagree with it: values (scalars, enums, value objects), lists and dictionaries of them, plain positional records whose
  members are their parameters, and a choice of them (an abstract record with no members, its `[JsonDerivedType]`
  subtypes plain positional records). Anything else (another property or field, a constructor, a base, a struct, an
  interface, a JSON attribute but `[JsonPropertyName]`) is refused by LEVEE104 and by the catalog where it's made (a
  host's start, the generated catalog test), naming the operation. A referenced assembly's operation is judged where it's
  declared and carried as declared where it's composed, never left out of the catalog.
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
- `LeveePostgresOptions.LeveeSchema = false` for an app whose own migrations add Levee's schema, and `Schema` for one
  that keeps Levee's objects in a schema of their own, as production does.
- New: `LeveePostgres.StartAsync`, Postgres for pipeline tests as production has it (Testcontainers): the owner runs
  Levee's schema, the app's migrations and its SQL, and the app connects as a role that owns nothing and doesn't bypass
  row-level security. Referenced from Test and TestSupport projects only.

### Levee.EntityFrameworkCore
- **Breaking:** the runtime role's grants are an allow-list. `LeveeSchema.Grants(role)` grants exactly the tables a Levee
  policy holds (`Isolation`, `Shared`) and resets the role to them; a table isolated later is granted as its policy is
  made. A table without one is unreachable, whatever names it: no blanket grant, no default privileges. Run Levee's
  grants first, then the app's own.
- Before the first operation, what the role can reach beyond Levee's grants (by table or column, in any schema it can use) is
  checked as what the context maps is, and so is the role itself: a superuser, a role that bypasses row-level security,
  or one that owns a table row-level security holds fails before the first operation. The tables Levee.Oxbow installs are left to it by name, not by prefix.
- `LeveeSchema.Grants` resets the role to its allow-list: it revokes exactly what the role holds, wherever it is (by
  table or column) and the default privileges (per schema and database-wide) that would grant it more, then fails naming
  anything another role granted that it couldn't take back, and anything it still reaches in effect (through a role it
  belongs to, or PUBLIC) beyond what this installation allows, read from the privileges themselves as an allow-list (so one a
  later Postgres adds, as 17 added `maintain`, is refused, not missed): another's table nothing, its audit reading
  only, its own tables what row-level security holds (`truncate` would empty every scope's rows). An extension's own
  relations (PostGIS's `spatial_ref_sys`) are the extension's, here and in the first operation's check. The runtime
  role belongs to no role: a group, or a predefined one such as `pg_read_all_data`, lends it what no ACL shows, so
  `Grants` fails naming it.
  `Grants` is one statement: refused, it changes nothing (the registered role included), whatever the client's autocommit;
  `dotnet levee db grants --jobs` runs Oxbow's grants inside that same statement, so both hold or neither does, standalone
  or inside a caller's transaction, which it never commits. It grants only this installation's tables: a second Levee
  schema's tables and audit stay its own role's. A schema's owner provisions it without rights over anyone else's.
- **Breaking (schema):** the audit's policies are a read (`levee_scope`) and an insert (`levee_audit_insert`) that takes an
  unscoped operation's record, so an unscoped write commits when the audit's owner is an ordinary role. A database
  made by an earlier alpha runs, as the audit's owner: `drop policy levee_scope on levee_audit;` then the two
  `create policy` statements from `LeveeSchema.Create`.
- A context registered more than once (the separate store beside its `AddLeveeSeparateContext`) fails the host's start.
- A watch waiting for, or reading, the listening connection ends when the host does.
- `[SeparateContext("OpenIddict")]` and `AddLeveeSeparateContext<T>`: a separate bounded context's store (identity:
  OpenIddict's) beside the app's, used by the library it names outside every operation, kept apart by the database: it
  connects as a role of its own, and the app's role reaches only the tables Levee's policies hold. The host's start
  refuses it registered any other way, connecting as the app's role, or mapping an entity that isn't the library's (an
  assembly built with Levee is the app's); LEVEE018 refuses the app's own code holding it.
- LEVEE021 (new): a principal is made only where the app composes (Composition, a Host, a Tool), and a process sends
  through its `ProcessContext`, never a `Dispatcher` it holds.
- LEVEE112 (new): one process per input across the composed app, or a queued run would go to whichever DI resolves.
- LEVEE113 (new): an access policy reads the scope's `Key`; one that grants by the access word alone, or reads only
  `Scope.Platform`, lets any caller into any scope. A policy that refuses everything is exempt. `dotnet levee new`'s
  policy grants `anyone` only at platform scope.
- LEVEE022 (new): change signals go only through `IChangeSignals`; SQL naming `levee_signals` is refused.
- LEVEE021 also refuses holding a principal (a constructor parameter, field or property) in Kernel, Contracts, Domain,
  Application or an Adapter: those act as the caller their context names. Acting as the system is a Host's or
  Composition's own code, as LEVEE016, LEVEE018 and LEVEE008's messages now say.
- LEVEE003 refuses a clock (`TimeProvider`, NodaTime's `IClock`) held by an operation's or a subscription's handler: a
  handler's time is `OperationContext.Now`, its ids `OperationContext.NewId()`.
- LEVEE020 refuses `OperationAttribute` named as a type anywhere outside an attribute.
- LEVEE020 follows reflection into Levee's types through `Type` locals and fields, `Type.GetType`, `GetTypeInfo`, the
  reflection extensions and activators, and judges an `[UnsafeAccessor]` by its target; it refuses a `dynamic` call on the
  `Dispatcher`.
- LEVEE005 refuses reflecting on the container and the DI extensions to locate services, outside where the app composes.
- LEVEE004's migration exemption covers only SQL handed to the `MigrationBuilder`.
- LEVEE003 refuses Visual Basic's `DateAndTime` clock, and NodaTime's `SystemClock.Instance` outside where the app composes.
- LEVEE018 refuses holding a separate context in every role; Composition and a Host name it only as a registration's type
  argument.
- LEVEE104 checks the members System.Text.Json writes: `[JsonIgnore]` left out, `[JsonInclude]` fields and non-public
  properties in. LEVEE108 refuses a `Default` on a set (`Many`) port.
- LEVEE015 allows an adapter's extension only in the registration shape, called to register. LEVEE006 exempts .NET's
  generators only by their output's path shape, and refuses a TestSupport project that's a test project or an executable.
- The generated registration adds a closed generic helper as constructed.
- LEVEE104 leaves System.Text.Json's own converters alone (`JsonStringEnumConverter` on an enum): the schema shows what
  they write.
- The generated registration finds Application's own classes its handlers and processes take, at any depth (a store, a
  calculation over ports), and registers them scoped; a class that takes a value is configuration, Composition's.
- **Breaking:** a LEVEE suppression builds only while the app declares it's migrating,
  `<LeveeAllowSuppressions>true</LeveeAllowSuppressions>`; elsewhere LEVEE006 refuses it. `check` stays red either way.
- LEVEE006 leaves .NET's own source generators' output alone (`[GeneratedRegex]` emits a bare `#pragma warning disable`),
  and holds every other generator's to the rule.
- LEVEE004 leaves an EF migration's SQL alone: it's the owner's, run at migrate, so it may backfill the audit.

### Levee.Cli
- `generate operation` takes `--unscoped` and `--caller-scoped` beside `--scoped-by` and `--platform`; `catalog` shows
  each operation's scope (`input`, `caller`, `unscoped`, `platform`), as `scope` in its JSON, and a generated test
  asserts all three scope flags.
- The scaffold's anonymous caller is Levee's `AnonymousPrincipal`, so the dispatcher's 401s hold for it.
- `dotnet levee check` no longer passes a failed analyzed build: a project whose analyzed build fails on anything but a
  LEVEE rule is named with its errors and its analysis is incomplete, in the verdict and in `--json` (an entry per project,
  `"incomplete": true` and no rule; each finding gains `"incomplete": false`). It reads a multi-target project's findings
  (`[App.csproj::TargetFramework=…]`) once each, and builds the app at its real path, so an `.editorconfig` applies as it
  does to `dotnet build` in the directory. `--json` lists projects that don't compile.
- `dotnet levee check` ends with its verdict: not on Levee yet while any rule is broken or suppressed, or a project
  doesn't compile.
- `dotnet levee db isolate` and `share` take a table in a schema of its own (`billing.invoices`), as the docs say.
- `dotnet levee check` lists every suppressed LEVEE rule with its reason and fails until none is left: a suppression lets
  a migration build, never makes it green.
- `dotnet levee check` takes stock partway through a migration: projects that don't compile are named, and the rest are
  still checked.

### Generator and analyzers
- LEVEE104: a converter goes on the value's type, never a member; a library's attribute deriving `JsonConverterAttribute`
  is one too; a `[ValueObject]` has its converter; `[JsonNumberHandling]` other than strict, `[JsonUnmappedMemberHandling(Skip)]` and
  `[JsonExtensionData]` and `[JsonIgnore]` are refused on a contract, through the interfaces a result extends and the subtypes a
  `[JsonDerivedType]` declares too; so is a contract type with more than one public
  constructor and none marked `[JsonConstructor]`, which the wire writes but can't read back.
- LEVEE113 takes a policy returning a `static readonly Error` as refusing everything; LEVEE021 doesn't count a delegate
  that takes a principal (`Action<Principal>`) as holding one.
- LEVEE003: a handler holding a port that tells the time (a `[Port]` or delegate answering a date or time at once without
  being handed one) holds a clock, as one holding `TimeProvider` does. In Application only a process holds a clock: a
  helper class holding one would hand the wall clock to every handler that takes it.
- `[Port(SelectedBy = "...", Default = "none")]`: an optional port's null object serves when its setting isn't set, so
  only a deployment that uses the port configures it (LEVEE108 holds the default to an adapter that exists).
- LEVEE015 lets Composition call an adapter's own registration, one `IServiceCollection` extension in its Adapter
  project for what it needs beyond its ports; nothing else of an adapter's.
- A Tool binds the ports it references, not every port the app declares (LEVEE107 holds a Host and Composition to all).
- LEVEE020 refuses reflection into Levee's own types (`typeof(Dispatcher).GetMethod(...)`): what Levee keeps internal
  is its own, and what it makes public is called.
- LEVEE020 refuses a by-name surface of the app's own (reading an operation's `InputType` or `ResultType` from its
  descriptor) and a contract that sends itself (the `Dispatcher` from Kernel, Contracts or Domain): sending by name is
  `MapLeveeOperations` and `WithLeveeOperations` alone.
- LEVEE020 refuses calling the composition's plumbing by hand (a generated `LeveeRegistration_*`, `PortSelection`),
  which is public only because the root's generated code calls it; the generated registration is hidden from IntelliSense.
- A `TestSupport` role for what several test projects share (fixtures, fakes, a harness): held to no code rule, like a
  test, and referenced by tests alone.
- Every rule's help link opens its page on the site (`https://rkingon.github.io/Levee/rules/levee0nn/`).

### Documentation
- The packed README links each guide to its page on the site, not the repository's `docs/` paths nuget.org can't follow;
  it names a subscription's route, `GET /ops/{name}/events`.
- The access policy lives in Composition, where the catalog is composed, so its refusals reach `GET /ops`.
- Recorded the open proposal for `ProcessContext.StepAsync` in the [decision log](docs/decisions.md#open).
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
