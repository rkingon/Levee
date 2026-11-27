# Repair Shop: what we look for

For whoever runs the trial, never the agent: [repair-shop.md](repair-shop.md) is all an agent gets, with one line
naming Levee and where its packages are. It names no Levee feature on purpose. What it shows is which shapes an agent
finds by itself, from the CLI, the templates and the build's `Fix:` texts, and which it rebuilds by hand.

## Run it

- The same spec to at least two agents (Opus and Astra), each in an empty directory: "Build this with Levee
  (`dotnet tool install Levee.Cli --prerelease`). Report what you built and anything you couldn't do."
- Nothing else: no rule list, no hints, no AGENTS.md beyond what `dotnet levee new` writes.

## Each requirement, and the shape we expect

| Spec | Levee's shape | A miss looks like |
|---|---|---|
| Shops isolated | Scoped operations (`IScopedOperation`, `CallerScoped`), `LeveeSchema.Isolation` per table, `AddLeveePostgres` | Query filters, a tenant id checked in handlers, a context of its own |
| Roles | One `IAccessPolicy` with the app's own access words; `[Refuses]` | Checks in handlers, attributes of its own |
| API keys shown once | `[Secret]` on the issued key's input or result as appropriate; a caller-scoped principal (`IScopedPrincipal`) | The key in the audit or logs |
| Personal data | `[Personal]` on name, phone, email | Personal values in the audit or outcomes |
| VIN | A value object with `[ValueObject(typeof(string), Pattern = ...)]` and its converter | A bare string, or a converter whose schema says nothing |
| Work order lifecycle | An aggregate root in Domain changing only through its methods (LEVEE007); declared errors per transition | Public setters, status strings set from handlers |
| Overwrite case | An expected version on the input, EF's concurrency token, a conflict error | Last write wins |
| Who changed what | Levee's audit, nothing of its own | An audit table of the app's |
| PartsHub | A `[Port(SelectedBy = ..., )]` with a simulator and a live adapter, named | A flag in the adapter, an HttpClient in a handler |
| Lookup recorded on failure | An internal operation sent as the system, its own transaction | A side connection, a suppression |
| Optional texting | An optional port with `Default = "none"` and a null adapter | A null check in handlers, a required setting |
| Ready text | Sent from the operation that marks it ready, or a job enqueued with it | Sent before commit with no thought, or from a hosted loop |
| Live board | A subscription following `IChangeSignals`: watch, read, follow | Polling, a LISTEN connection of its own, a WebSocket hub around the dispatcher |
| Nightly invoicing | An `IProcess<TInput>` started by an operation (or a scheduled job), each batch an operation, idempotent | One long operation, a hosted loop with its own transactions |
| Never twice | `Idempotency = Idempotent` or `Conflicts` on the invoicing step; state in the aggregate | A flag checked outside a transaction |
| HTTP API | `MapLeveeOperations` | Routes of its own sending by name |
| MCP | `WithLeveeOperations` | An MCP server of its own |
| Public status page | A Projection: one typed route, its own principal for the token, sent through the Dispatcher | `/ops` opened to anonymous callers, a route in a Host that queries the database |
| Operator CLI | A Tool with `LeveeOwner.Run()` for migrations and bootstrap | Owner work in an adapter |
| Tests | Handlers with fakes of ports; aggregates in memory; the pipeline on Postgres (`Levee.Testing`) | An in-memory database for pipeline tests |

## What to measure

- **Green without being told:** `dotnet levee check` at the end, and any suppression with its reason.
- **Convergence:** diff the agents' results. Project layout and roles, operation names, error codes, where each concern
  lives. Where they differ, which one Levee's guidance points to, and whether anything pointed at all.
- **Self-correction:** build iterations per rule; a rule an agent needed several tries to satisfy gets a better `Fix:`.
- **Rebuilt by hand:** every row above that an agent built its own way, and whether a rule could have refused it.
- **Walls:** what it couldn't do, and whether Levee lacks the shape or the agent missed it.
