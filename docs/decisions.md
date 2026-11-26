# Decisions

The apps trialling Levee are A, B and C. Each entry is something one of them asked for, or a fork in the design, with what Levee does and why. A request Levee
declines stays here so the next app that asks reads the reason instead of rediscovering it. Levee changes for a bug, a
hole in a rule, or a capability two apps need; a consumer whose convention is looser than Levee's changes its side.

## Held: the consumer changes

| Asked for | From | Levee's answer | Why |
|---|---|---|---|
| Declare a shared refusal (`because.*`) once, not on every operation | A | Declare it on each operation that can return it; share the code as a constant | An operation's error codes are its contract, read by every client from the catalog. One the operation can return but doesn't list is one no client expects. |
| Follow a handler's helpers in LEVEE110 | A, B | No: `RefuseUndeclaredErrors` in tests instead | A helper's codes aren't all reachable from every caller: following them would demand codes an operation never returns. Tests exercise the paths that are. |
| Skip Oxbow's unknown-kind check where a process enqueues kinds it doesn't work | A | Register every worker in every process; choose queues per process | The kind check catches typos at enqueue. Registration is the app's shared composition; which queues a process works is its deployment. |
| A hand-built HTTP envelope for the app's own clients | A | `MapLeveeOperations`, or name what it can't do | Projections are generated, with no logic of their own; a second wire shape is a second set of rules. An app's own typed routes reuse `LeveeProblems`. |
| Write a paid vendor call's record even when its operation rolls back | B | Dispatch an internal operation for the record, as the system | That record is its own unit of work, so it's its own operation: authorized, scoped, audited. The audit rows are correct, not noise. |
| Hand a secret to a vendor before the operation commits | B | Enqueue the handover as a job | Jobs commit with their operation (Oxbow in the unit of work): a rolled-back operation hands nothing over, with no compensation to write. |
| Read tables before there's a caller (a token endpoint, a status check) | B | Internal operations | The platform's own operations go through the same pipeline; `Internal` keeps them off every projection. |
| `IOptions` and `MemoryCache` in Application | C | Settings as a record Composition binds; a cache as a port | The core holds no framework. Configuration is wiring; caching is infrastructure behind what the application needs. |
| Composition exposing helpers and constants | B | Only registration is public | Anything else public is a way into the application around the dispatcher (LEVEE014). |
| Error codes built at run time (`{field}.required`, `externalId.tooLong`) | A | One stable code per failure (`notes.invalid`) with what's wrong per field in `Error.Fields` | A code is what a client branches on, so it can't vary by field; the field is detail. A code per field is a contract that grows with every property. |
| Sign-in endpoints that run internal operations as a principal taken from the request | A | Keep them in the Host | Turning credentials into a principal is authentication, the Host's job. They dispatch internal operations, which `/ops` rightly refuses. |
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
| A job honours its failure's `RetryAfter` | Levee | A vendor's rate limit says when to come back; Oxbow's backoff would guess. Built in Oxbow first (`JobRetryException`, 0.2.0-alpha.3), since a worker choosing its delay is Oxbow's to offer. |
| MCP: inputs the server binds, rendered results, a server with no dispatcher | A | `LeveeMcpOptions` | What kept A's own MCP server: a turn's id filled by the server, a page shown as an image, and a stdio server beside the client. Without them, the rule "no hand-written projections" couldn't hold. |
| `ProblemView` in `Levee` | A | A client (an SDK) reads failures with Levee's own type, not a copy that drifts. |
| SSE keep-alive and recheck | A | A stream outlives the authorization it opened with: a revoked key or a lost membership ends it, and idle proxies don't. |
| LEVEE016 exempts operations | A | A shared marker on a message (a tenant id) is data, not a way into the application. |
| Levee's SQL qualified by schema | B | Policies and grants that resolve through `search_path` hold only for the roles whose path happens to match. |
| Generated code documented | B | A strict build (`GenerateDocumentationFile`, warnings as errors) refused Levee's own output. |

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

## Open

| Question | From | Where it stands |
|---|---|---|
| A transaction opened lazily, for operations that never touch the database | B | Deferred: real work, and it matters under load. |
| The current operation inside `HttpClient` handlers | B | Deferred: B's admission-based workaround holds. |
| An operation's result in `IOperationOutcome` | B | Deferred: results can carry secrets, so it needs a redaction story first. |
