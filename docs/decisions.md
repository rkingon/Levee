# Decisions

The apps trialling Levee are A, B and C. Each entry is something one of them asked for, or a fork in the design, with what Levee does and why. A request Levee
declines stays here so the next app that asks reads the reason instead of rediscovering it. Levee changes for a bug, a
hole in a rule, or a capability two apps need; a consumer whose convention is looser than Levee's changes its side.

## Held: the consumer changes

| Asked for | From | Levee's answer | Why |
|---|---|---|---|
| Declare a shared refusal (`because.*`) once, not on every operation | A | Declare it on each operation that can return it; share the code as a constant | An operation's error codes are its contract, read by every client from the catalog. One the operation can return but doesn't list is one no client expects. |
| Follow a handler's helpers in LEVEE110 | A, B | No: `RefuseUndeclaredErrors` in tests instead | A helper's codes aren't all reachable from every caller: following them would demand codes an operation never returns. Tests exercise the paths that are. |
| Skip Oxbow's unknown-kind check where a process works some queues and enqueues kinds others work | A | Register every worker in every process; choose queues per process | The kind check catches typos at enqueue. Registration is the app's shared composition; which queues a process works is its deployment. (A process that works no queue at all skips the check: built, since it registers no worker to check against.) |
| A hand-built HTTP envelope for the app's own clients | A | `MapLeveeOperations`, or name what it can't do | Projections are generated, with no logic of their own; a second wire shape is a second set of rules. An app's own typed routes reuse `LeveeProblems`. |
| Write a paid vendor call's record even when its operation rolls back | B | Dispatch an internal operation for the record, as the system | That record is its own unit of work, so it's its own operation: authorized, scoped, audited. The audit rows are correct, not noise. |
| Hand a secret to a vendor before the operation commits | B | Enqueue the handover as a job | Jobs commit with their operation (Oxbow in the unit of work): a rolled-back operation hands nothing over, with no compensation to write. |
| Read tables before there's a caller (a token endpoint, a status check) | B | Internal operations | The platform's own operations go through the same pipeline; `Internal` keeps them off every projection. |
| `IOptions` and `MemoryCache` in Application | C | Settings as a record Composition binds; a cache as a port | The core holds no framework. Configuration is wiring; caching is infrastructure behind what the application needs. |
| Composition exposing helpers and constants | B | Only registration is public | Anything else public is a way into the application around the dispatcher (LEVEE014). |
| Error codes built at run time (`{field}.required`, `externalId.tooLong`) | A | One stable code per failure (`notes.invalid`) with what's wrong per field in `Error.Fields` | A code is what a client branches on, so it can't vary by field; the field is detail. A code per field is a contract that grows with every property. |
| Sign-in endpoints that run internal operations as a principal taken from the request | A | Keep them in the Host | Turning credentials into a principal is authentication, the Host's job. They dispatch internal operations, which `/ops` rightly refuses. |
| MCP tool names chosen per tool, for names outside clients already call | B | Those tools are a public API: a `Projection`, the app's own | `Levee.Mcp` names by a fixed rule because it's internal; a public tool name is a product's contract. |
| Named SSE events per item type | A | The client reads `message` events | One event name for items keeps every subscription's wire the same; the item says what it is. |

## Built

| What | From | Why |
|---|---|---|
| Failures: `internal` for a bug, declared codes, problem+json, transient retries | all three | A failure contract every projection and job reads the same way, with the bug's detail in the log, not the response. |
| `RefuseUndeclaredErrors` | A | A built a log guard to find 21 undeclared codes; every app would. |
| A Contracts project's `LeveeCatalog` | A | A CLI, an MCP server and an app's access checks read the contract offline, without the application. A reflected over Levee's internals to get it. |
| LEVEE016, delegates in LEVEE013/015, LEVEE004 on raw SQL, frameworks refused by the allow-list | A, B, C | Holes: each let code reach around the dispatcher, the unit of work or the core's purity, and compile. |
| Test fakes composed only when marked `[Adapter]` | A, B | A test wires its own fakes; composing them too gave every port a second, unwanted adapter. |
| Only the chosen adapter registered | B | An adapter a deployment doesn't use shouldn't need its dependencies to resolve. |
| Constraint and trigger refusals are `Conflict`, not `internal` | B | `internal` is transient: a job would retry what can never succeed. |
| COMMIT is never cancelled by the caller | B | A cancel mid-COMMIT leaves nobody knowing whether it happened. |
| `LeveeProblems` public | B | An app's own HTTP routes answer failures exactly as `MapLeveeOperations` does. |
| A job honours its failure's `RetryAfter` | the maintainer, with the failure contract | A vendor's rate limit says when to come back; Oxbow's backoff would guess. Built in Oxbow first (`JobRetryException`, 0.2.0-alpha.3), since a worker choosing its delay is Oxbow's to offer. |
| MCP: inputs the server binds, rendered results, a server with no dispatcher | A | `LeveeMcpOptions` | What kept A's own MCP server: a turn's id filled by the server, a page shown as an image, and a stdio server beside the client. Without them, the rule "no hand-written projections" couldn't hold. |
| A run-time guard against commands outside an operation; the audit stamped by the database; the analyzers-off check keyed on an environment token; untyped input members refused; the catalog the generator's alone (LEVEE020) | a second independent audit | Each claim a probe could still falsify is either held at run time now, or restated as exactly as strong as the code. |
| The guard keyed on Levee's own transactions, one context per app, `GetDbConnection` and `new` contexts a Tool's; the check token read from the environment; global configs and a Test role without a test framework refused; untyped containers and `[JsonInclude]` fields refused; dictionary values matched to their JSON by position; a recorder failing to record never fails a committed operation | a third independent audit | The same stance: each falsified claim is held now, by a test that fires and one that doesn't, or restated. |
| Marks honoured through overrides and interfaces; a context the guard can't reach, a second context by any registration, and a retrying execution strategy refused; a recorder failing to be built or disposed never fails a committed operation; design-time factories and EF's service accessor refused outside a Tool; setting names allowed only where read; untyped items of an app's own collection, and shared abstract bases, refused | a fourth independent audit | The same stance. |
| A table the context maps without a policy fails before the first operation (`Shared` for tables every scope reads); the second-context check reads implementations and runs the guard checks at start; shared bases with any override are LEVEE016; EF's accessor LEVEE018; policy, admission and input-validation bugs answered as failures; outcomes bounded in time and told even when the input can't be serialized; `[Secret]` refused in a subscription's input | a fifth independent audit | The same stance; a forgotten policy now fails closed. |
| The isolation check walks every table EF writes (split, beside a view, through the search path) and fails on one it can't find; an injected connection or data source is LEVEE018; any exception from an input's own type is `input.invalid`; a result is serialized before the commit; `store.stale` and `store.invalid_value` for refusals that aren't transient; recorders bounded however they block; indexers count for LEVEE016; bound MCP inputs in any casing; check goes on past a generator finding | a sixth independent audit | The same stance. |
| Views the context reads checked (`security_invoker`, or an owner row-level security holds); LEVEE018 judged where the container hands a connection over, not where the operation's own is held; `store.invalid_value` only for values a type refuses; an input type's other exceptions refused without their message; the recorders bounded as a whole; MCP's `Render` failing falls back to JSON | a seventh independent audit | The same stance. |
| Views judged by what they read, down to the tables (materialized views and foreign tables refused, `security_invoker` read in any spelling); endpoint handlers as method groups are LEVEE018, other lambdas aren't; `22P05` a value refused | an eighth independent audit | The same stance. |
| The isolation check holds per target (database, role, search path), not per context type; a throwing scope key, a cancelled subscription open and an SSE error under backpressure kept to the failure contract; MCP binds only inputs an operation takes; `generate` reuses a declared result; LEVEE018's endpoints are an endpoint route builder's `Map*` | a ninth independent audit | The same stance. |
| The isolation check's target names its server (address, port, start time) beside the database, role and search path; an SSE recheck ends the stream when the caller's subject or scope changed; an unserializable stream item is a failure; `Bind` runs only for tools that take a bound input; `generate` names a result from another area by its namespace; LEVEE018 skips private constructors | a tenth independent audit | The same stance. |
| The SSE recheck holds against a producer that keeps the channel full; a subscription can't be enqueued as a job | a code review | A busy stream would put off its recheck as long as it stayed busy; a subscription enqueued would be cancelled by the worker, unrun. |
| `ProblemView` in `Levee` | A | A client (an SDK) reads failures with Levee's own type, not a copy that drifts. |
| SSE keep-alive and recheck | A | A stream outlives the authorization it opened with: a revoked key or a lost membership (admissions run at open and at each recheck) ends it, and idle proxies don't. |
| The audit written only through `levee_audit_write`; side connections, scope settings and analyzers-off refused; recursive redaction | an independent audit | Each let code of the app's step around a guarantee the README claimed: forge the audit, leave the transaction, change its scope, or turn the rules off. |
| LEVEE016 exempts operations | A | A shared marker on a message (a tenant id) is data, not a way into the application. |
| Levee's SQL qualified by schema | B | Policies and grants that resolve through `search_path` hold only for the roles whose path happens to match. |
| Generated code documented | B | A strict build (`GenerateDocumentationFile`, warnings as errors) refused Levee's own output. |

## Trust model

Levee's guarantees are against mistakes: code that compiles in the wrong shape, and writes the contract didn't declare.

- **The build** refuses every way it knows for code of the app's to step around the pipeline: committing, opening a
  connection of its own, setting the scope settings, service location, I/O in the core, calling the application from a
  Host. Each is a rule with a test that proves it fires. Turning the rules off is itself refused (LEVEE006, LEVEE009): by
  property, by config severity or a global config's `build_property.Levee*`, by a generated-file header, or by a Test role
  on a project with no test framework. A project that references a test framework and calls itself a test is believed.
- **At run time,** what the build can't see is held anyway: the app's context refuses any command outside a transaction
  the unit of work began (a seeder, a resolved context, a hosted loop, EF's own `SaveChanges` transaction); a second
  context, registered by its options, as itself or behind an interface, fails the host's start, as does a context the
  guard can't reach (configured in `OnConfiguring`); a table the context maps without a row-level security policy fails
  before the first operation; an undeclared write fails in a read-only transaction; a write
  outside the operation's scope fails row-level security; and the runtime role adds to the audit only through
  `levee_audit_write`, which stamps its own time, so it can't change, remove or backdate a record.
- **Not covered:** hostile code running as the app's role. The scope is a transaction setting the unit of work sets, and
  `levee_audit_write` is callable by that role: code that sets the one or calls the other anyway (by reflection, a raw
  connection, or SQL the build can't see) moves into another tenant's rows or adds a misleading record. The build refuses
  every such spelling it knows, but no analyzer of a general-purpose language sees them all. A dependency the app trusts is
  trusted. Separating that is the database's roles, per tenant or per service, not Levee's.

## Forks

Where the apps disagreed before Levee, and what it chose.

| Fork | Chosen | Why |
|---|---|---|
| Failure model: unions, `Result`/`Error`, or exceptions | `Result<T>` with a closed `ErrorKind`; domain outcomes in the result type; exceptions only for bugs, answered `internal` | Every projection and job reads one shape; a bug's detail stays in the log. |
| The surface generated at compile time or run time | Compile time: a source generator catalogs operations, handlers and adapters, and binds ports | Wrong wiring fails the build, not the first request. |
| Isolation: RLS, query filters, or membership checks | Row-level security keyed on the operation's scope, with membership as an `IAdmission` | A query filter forgotten in one query leaks; a table or view the app maps that row-level security doesn't hold fails before the first operation. |
| What Valkey may hold | Not Levee's: an app's store is a port and its adapter | No rule here would stop wrong code compiling either way. |
| Auth shape | The host establishes a `Principal` (a projection's principal hook, `IJobPrincipals`); `IAccessPolicy` decides | Authentication is the host's; authorization is one decision, in one place. |
| Live updates | Subscriptions (`ISubscription<TItem>`), authorized, scoped and admitted as they open and rechecked while they stream, projected as SSE | One pipeline for one-shot and streaming calls. |
| The dev loop | `dotnet levee`: new, generate, check, explain, db, catalog | Pinned per app, so every agent runs the same rules. |

## Decided

| Question | Answer | Why |
|---|---|---|
| Who owns a public, customer-facing API? | The app, in a `Projection` project | Routes, resources, versions and deprecation are a product's contract, not a framework's. Levee's `/ops` is internal: the app's own clients and agents. A Projection sees only Kernel and Contracts, so it can only map HTTP onto operations and send them through the dispatcher; `LeveeProblems` makes its failures the same as `/ops`'. |
| Scope from the caller's credentials | `CallerScoped = true`, beside `IScopedOperation` and `Platform` | When a key is issued to one tenant, an input naming the tenant is a field every client repeats and the access policy must re-check. Input scope stays for a caller who acts across scopes. |
| Code several adapters share (a vendor transport) | The `Infrastructure` role: Kernel only, referenced by adapters and roots | Adapters still never reference each other; what they share sees nothing of the application. With it, every project an app references must use Levee with a role: a project outside was a side door. |
| Code run as the database's owner before the app exists (migrations, role bootstrap) | The `Tool` role: an executable nothing references, owning its transaction | It must own its transaction, so it can't be an adapter the dispatcher drives. Narrow on purpose: every other rule holds, so it doesn't become where rule-breaking code goes. With it, adapters implement ports only. |
| Access-policy and admission codes in the catalog | `[Refuses]`, listed once as `Refusals`, LEVEE111 | They can come back from any operation, so they're declared once, where they're decided. Two apps asked. |
| Personal data in the audit and logs | `[Personal]`, redacted in the audit and outcomes; denial logs drop the subject | The audit is append-only, so personal data written there could never be erased. The audit still names who acted; logs, kept and shipped elsewhere, don't need to. |
| An MCP projection | `Levee.Mcp`, on the official MCP SDK | A and B both wrote one. It's internal, like `/ops`, so generated: names by a fixed rule (dots to underscores, clashes fail at startup), a filter for which operations an agent sees, the principal from the request. |
| A hand-built wire for the app's own clients (A's own envelope) | `MapLeveeOperations` | Only the app's own SDK called it: that's what `/ops` is for. |

## Planned: 0.0.1-alpha.2

| Feature | Shape |
|---|---|
| A typed client for `/ops` | Send an operation, get `Result<TResult>` over HTTP: one serialization, `ProblemView` parsing, cancellation and SSE, typed by a Contracts project's `LeveeCatalog`; the app supplies authentication. |
| Idempotency keys for retryable writes | A retry after a lost response gets the first result; concurrent equal requests make one write; a key reused with other input is a conflict. |
| A conventional stale-write conflict | An expected-version input over EF's concurrency tokens, one error code across every projection; the operation chooses whether it needs one. |
| A contract diff | `dotnet levee` compares a published catalog with a candidate: new required inputs, removed error codes, renames that strand clients or queued jobs. |
| Telemetry, through .NET's own APIs | A `Meter("Levee")` (operation duration, outcomes by kind and code) beside the `ActivitySource`, spans for subscriptions, jobs, authorization and admission under OpenTelemetry's conventions, the enqueuer's trace carried into its job, and the source and meter names for an app's exporter. No OpenTelemetry dependency in the core. |
| Positioning | "The .NET framework that keeps your AI in check": wrong code fails the build with the fix in the error, and what the build can't see, Postgres refuses. The README's opening and every package's description lead with it. |
| Outbound provider calls | A typed client for third parties, in the spirit of Ky: each provider's base URL and auth configured once, so an adapter names an action and makes a typed call. Strictly typed and validated, as a schema in Ky does: the response's record is its schema (required members and nullability enforced, unknown members ignored unless the provider is strict), and an action's `Result<T>` step validates and transforms it into the app's own type, so a vendor's types never leave its adapter. A response that breaks its schema is `provider.unexpected_response`, never half a value. Cancellation, a total timeout, retries only where safe, and redacted logging by default. One event per attempt, retries included, linked to its logical call and to the Levee operation, so the warehouse answers how often each provider was called and how long it took. Absorbs the open request for the current operation inside `HttpClient` handlers. |
| Port and adapter scaffolding | `dotnet levee generate port`: the Application port, the Adapter implementing it and its selection, for the vendor's behaviour to be filled in. |

## Open

| Question | From | Where it stands |
|---|---|---|
| A process that works no queue still checks Oxbow's schema at startup, so it can't start with the database down | B | Oxbow's to offer (a client-only mode); until then, readiness waits on the database. |
| A transaction opened lazily, for operations that never touch the database | B | Deferred: [benchmarks.md](benchmarks.md) measures what an operation costs today. |
| The current operation inside `HttpClient` handlers | B | Deferred: B's admission-based workaround holds. |
| An operation's result in `IOperationOutcome` | B | Deferred: results can carry secrets, so it needs a redaction story first. |
