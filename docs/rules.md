# Levee's rules

Every rule is an error. Each section is what `dotnet levee explain <id>` prints: why the rule exists, the shape it
refuses, and the conventional shape to write instead.

## LEVEE001: Every Levee project declares its role

The role decides what a project may reference and which rules apply to its code. Without one, none of them can.

```xml
<!-- Right: the layer the project is -->
<PropertyGroup>
  <LeveeRole>Application</LeveeRole>
</PropertyGroup>
```

Kernel holds ids and values every layer shares; Contracts, the operations and views clients see; Domain, aggregates;
Application, handlers and ports; Adapter, ports' implementations; Infrastructure, plumbing adapters share; Composition, the wiring hosts share; Host, a process;
Client, a consumer over the wire; Tool, a process run as the database's owner (migrations, role bootstrap); Projection, the app's own public API (typed routes, a webhook), which maps HTTP onto
operations and sends them through the `Dispatcher`; Test, tests.

## LEVEE002: A role references only what it may

Dependencies point inward: Domain knows nothing of Application, Application nothing of its adapters, a Host nothing it
doesn't compose. A reference outward lets the core depend on a detail.

| Role | May reference |
|---|---|
| Kernel | Kernel |
| Contracts | Kernel, Contracts |
| Domain | Kernel, Domain |
| Application | Kernel, Contracts, Domain, Application |
| Adapter | Kernel, Contracts, Domain, Application, Infrastructure (never another Adapter) |
| Infrastructure | Kernel, Infrastructure |
| Composition | all but Host, Tool and Test |
| Host | all but Host, Tool and Test |
| Client | Kernel, Contracts, Client |
| Projection | Kernel, Contracts, Projection |
| Tool | what a Host sees, but Client and Projection; nothing references a Tool |
| Test | all |

Every project an app builds declares a role, so none sits outside these rules: a project without Levee that another
references fails the build. Code several adapters share (a vendor transport, `DelegatingHandler`s) is Infrastructure.

Wrong: Application references the Postgres adapter to use its `DbContext`. Right: Application declares a port
(`INotes`), the adapter implements it, and composition binds it. Wrong: two adapters share code by referencing each other.
Right: what they share is Kernel (values) or a port both serve.

## LEVEE003: Time comes from TimeProvider

Reading the wall clock makes code untestable and gives one operation several "nows".

```csharp
// Wrong
var due = DateTimeOffset.UtcNow.AddDays(30);

// Right, in a handler: the operation's one time, at Postgres's precision
var due = context.Now.AddDays(30);

// Right, elsewhere
internal sealed class Lease(TimeProvider time) { DateTimeOffset Now => time.GetUtcNow(); }
```

`Guid.CreateVersion7()` reads the clock too. In a handler, `context.NewId()` is the id: a UUIDv7 at the operation's time,
and later than every id the operation made before it, which `Guid.CreateVersion7(context.Now)` isn't (every id in one
operation shares its one time, so their order would be random). Elsewhere, pass the time.

Only Composition, a Host or a Tool uses `TimeProvider.System`; tests register a `FakeTimeProvider`.

## LEVEE004: Only the dispatcher saves and commits

One operation is one transaction, committed once, after the handler and the audit. A `SaveChanges` or a transaction
of your own commits half an operation, or commits what the audit never saw.

```csharp
// Wrong
db.Notes.Add(note);
await db.SaveChangesAsync(ct);

// Right: change state; the dispatcher commits, or rolls back on an error
db.Notes.Add(note);
return view;

// Right: a later query in the same operation must see the insert
internal sealed class EfNotes(AppDbContext db, IFlush flush) : INotes
{
    public async Task AddAsync(Note note, CancellationToken ct)
    {
        db.Notes.Add(note);
        await flush.FlushAsync(ct); // sent inside the open transaction, not committed
    }
}
```

Raw SQL is held to the same rule: a constant or interpolated statement that begins, commits, rolls back or shapes a
transaction (`ExecuteSqlRaw("commit; begin")`, a command's `CommandText`) is refused, and so is `TransactionScope` and a
savepoint. So is SQL that sets `levee.scope` or `levee.platform`, the settings row-level security trusts, however it's
spelled (`set_config`, `SET`, an update of `pg_settings`): set by code of the app's, they'd move the operation into another
tenant's rows. Only the unit of work sets them. The analyzer sees the name as a literal or a constant; a name put
together at run time from pieces isn't seen, and is the trust model's to answer. A setting's bare name is allowed only where it's read: passed with SQL
that reads it (`current_setting`) and sets nothing, or held in a `const` or a local used only that way. Anywhere else (a
`static readonly` field, a parameter's value, a format string's argument) it's refused. An EF migration's SQL is the
owner's, run at migrate and never inside an operation, so it may write the audit (a backfill from an old one).

To undo, return an error: the dispatcher rolls back. A refusal that must be recorded is a domain outcome in the
result, not an error.

## LEVEE005: No service location outside hosts

`GetService` hides a dependency from the constructor, from tests and from the generated composition's check that
every port is bound.

```csharp
// Wrong
var gateway = provider.GetRequiredService<IPayments>();

// Right
internal sealed class PayHandler(IPayments payments) : IOperationHandler<Pay, Receipt> { ... }
```

EF's accessor (`db.GetService<T>()`) is the same, and more: it reaches the context's internal services, its connection
among them, so outside a Tool it's LEVEE018 too.

`IKeyedServiceProvider` and `ActivatorUtilities` are service location by another name. To choose an adapter by
configuration, declare `[Port(SelectedBy = "Providers:Payments")]`. To choose one at run
time by name (per tenant, per request), declare `[Port(Many = true)]`, name the adapters, and take `IAdapters<TPort>`.

## LEVEE006: Levee rules are suppressed only with a reason

A rule weakened for a whole project is a rule nobody knows is off. The fix is the code. A suppression is migration debt,
and builds only where the app says it's migrating (`<LeveeAllowSuppressions>true</LeveeAllowSuppressions>`, in the
project or `Directory.Build.props`, where it shows in review): at that line, with the reason beside it, it lets the app
build while it moves onto Levee a piece at a time,

```csharp
#pragma warning disable LEVEE012 // the generated client's only I/O is this stream, owned by the SDK
```

but `dotnet levee check` lists every one, with its reason, and fails until none is left. Code that can't follow a rule
because Levee has no way to say what it needs is a gap in Levee: report it, and keep the suppression only until Levee
closes it.

`NoWarn`, rulesets, `.editorconfig` or global config severities (per rule, for Levee's category, or for every analyzer at
once), assembly-wide `[SuppressMessage]`, a bare `#pragma warning disable`, turning analyzers off (`RunAnalyzers`,
`RunAnalyzersDuringBuild`), a global config setting what the analyzers read from MSBuild (`build_property.LeveeRole`), and
a Test role on a project that doesn't set `IsTestProject` and reference a test framework are refused. `dotnet levee
check` compiles once without analyzers, recognised by a token it puts in its own environment. A file marked `// <auto-generated/>` is held to the rules like any other. LEVEE006
itself is never suppressed.

## LEVEE007: Aggregates change only through their own methods

An aggregate root guards its invariants. A public setter or a mutable collection lets anyone skip them, whether it's
declared on the root or on a class the root derives from; a type deriving `List<T>` or implementing a writable collection
interface is a mutable collection.

```csharp
// Wrong
public sealed class Order : IAggregateRoot
{
    public OrderStatus Status { get; set; }
    public List<Line> Lines { get; } = [];
}

// Right
public sealed class Order : IAggregateRoot
{
    private readonly List<Line> _lines = [];
    public OrderStatus Status { get; private set; }
    public IReadOnlyList<Line> Lines => _lines;

    public Result<Order> Add(Line line)
    {
        if (Status != OrderStatus.Open) return Error.Conflict("order.closed", "A closed order takes no lines.");
        _lines.Add(line);
        return this;
    }
}
```

## LEVEE008: Inner roles reference only the BCL and abstractions

Kernel, Contracts, Domain and Application are the core: what the app is, not how it runs. A package there drags I/O,
a vendor or a framework into it.

Wrong: Application references `Microsoft.Extensions.Caching.Memory`, an HTTP client SDK, or `Microsoft.Extensions.Hosting`.
Right: Application declares a port for what it needs (`IPlanCache`, `IPayments`), an Adapter implements it with the
package, and a hosted service or loop lives in Composition or an Adapter, sending operations through the `Dispatcher`.

Configuration isn't the core's either: Composition binds settings into a plain record, and Application takes the record.

Only a pure library of values and functions (no I/O, caching, clients or framework) is allowed by name. Frameworks
can't be: an entry naming `Microsoft.Extensions.*` (the abstractions are already allowed), ASP.NET Core, EF Core,
`Microsoft.Data.*`, Npgsql or Levee's own packages, or a wildcard wide enough to cover one, fails the build.

```xml
<LeveeAllowedReferences>NodaTime;Vogen*</LeveeAllowedReferences>
```

## LEVEE009: Everything that uses Levee is built with its analyzers

A project with a role whose build has no Levee analyzers fails there; one referencing an assembly built without them fails
too.

A project built with `ExcludeAssets="analyzers"` on Levee compiles with none of these rules and none of the generated
registration. Reference Levee plainly.

## LEVEE010: Ports are declared in Application

A port is what the application needs from outside, so it lives with the application: named for the need
(`IHomePrices`), not for the vendor or the transport (`IZillowClient`, `IS3Reader`).

```csharp
// Application
[Port(SelectedBy = "Providers:HomePrices")]
public interface IHomePrices { Task<Result<HomePrice>> FindAsync(Zip3 area, CancellationToken ct); }
```

A repository is a port too: `IRepository<T>` for an aggregate root, declared in Application.

## LEVEE011: Adapters implement ports, in Adapter projects

An adapter is the only place a port meets the outside, and the only thing an Adapter project offers the application.

```csharp
// Adapters
[Adapter("s3")]
internal sealed class S3HomePrices(IAmazonS3 s3) : IHomePrices { ... }
```

Wrong: Application implements its own port (that's not a port; drop `[Port]`). Wrong: an adapter implements an
interface of Application's, Domain's, Kernel's or Contracts' that isn't a port: whoever holds that interface drives the
adapter around the dispatcher. Mark it `[Port]` in Application if the application needs it; work run as the database's
owner (a migrator, a role bootstrap) is a Tool's, which references the adapter directly.

## LEVEE012: The core does I/O only through ports

Kernel, Contracts, Domain and Application do no I/O: no HTTP, sockets, files, databases, processes, console or
environment. I/O there can't be swapped, simulated or tested without the world.

```csharp
// Wrong, in a handler
var json = await File.ReadAllTextAsync("rates.json", ct);

// Right: Application declares IRates; an Adapter reads the file (or S3, or a vendor), chosen by setting
internal sealed class QuoteHandler(IRates rates) : IOperationHandler<Quote, QuoteView> { ... }
```

Naming an exception or an enum from an I/O namespace isn't I/O, nor is a doc comment's `cref`, nor a value like
`HttpMethod` or a header value. A reader, writer or document given a path (`new StreamReader("x")`, `XDocument.Load("x")`), a
temporary file, and a member named without its type (`using static System.IO.File`) are.

## LEVEE013: The Host reaches the application only through the dispatcher

A Host is a process: it composes and drives. Calling a handler or a domain type directly skips authorization, scope,
the unit of work and the audit.

```csharp
// Wrong
var notes = app.Services.GetRequiredService<INotes>();

// Right
var result = await dispatcher.SendAsync(new CreateNote(id, tenant, text), principal, ct);
```

Wiring that needs Application's types moves into Composition. A Host may name an adapter to register it; calling one
(a store, a migrator) drives it around the pipeline, so that's refused too: migrations are a Tool's. A delegate to an Application method
(`Func<int> f = Rules.Decide`) is a call all the same.

## LEVEE014: Composition exposes only registration

Composition's public surface is what hosts call to wire the app: extension methods on `IServiceCollection` or an
endpoint builder. Anything else public would let a Host reach the application around the dispatcher.

```csharp
// Right
public static class AppComposition
{
    public static IServiceCollection AddApp(this IServiceCollection services, IConfiguration configuration) { ... }
}
```

Their signatures take and return only framework, Kernel and Contracts types. Everything else is internal.

## LEVEE015: Composition wires the application, never drives it

Composition constructs, binds and registers. Calling into the application from there (seeding, warming, a startup
task) skips the pipeline: send an operation through the `Dispatcher` instead, as the app's own principal. A delegate to
an Application method (a method group handed to a registration, a loop or a timer) is a call all the same.

An adapter's own setup is the exception: what it needs beyond its ports (its options, an `HttpClient`, a vendor's SDK)
is one `IServiceCollection` extension in its Adapter project, and Composition calls it. Nothing else of an adapter's is
called from Composition, so its options are parsed inside that extension, not by Composition.

```csharp
// Adapter
public static class ZipCodeApi
{
    public static IServiceCollection AddZipCodeApi(this IServiceCollection services, IConfiguration configuration) =>
        services.Configure<ZipCodeApiOptions>(configuration.GetSection("ZipCodeApi"));
}

// Composition
services.AddZipCodeApi(configuration);
```

## LEVEE016: Application is reached only through operations

An Application class that implements an interface from Kernel, Contracts or a package (a vendor's `IHealthCheck`, a
library's callback) is handed to whatever holds that interface: a gateway, a Host, Composition, the package itself. They call it directly, and the dispatcher never sees the call: no authorization, scope,
unit of work or audit.

```csharp
// Wrong: Application
internal sealed class JobBroker : IWorkDesk { public Task<Job?> NextAsync(...) { ... } }

// Right: the logic is a handler; Composition implements the shared interface by dispatching
[Operation("work.next", "runtime", Internal = true, Platform = true, ...)]
public sealed record NextJob(...) : IOperation<Job?>;

internal sealed class DispatchedDesk(Dispatcher dispatcher) : IWorkDesk
{
    public async Task<Job?> NextAsync(...) => (await dispatcher.SendAsync(new NextJob(...), principal, ct)).Value;
}
```

A shared base class (Kernel's or Contracts') with anything to override, abstract or virtual, public or protected, is a
way in the same: whatever holds the base calls the override, directly or through a template method. Deriving from one is
refused, except for value semantics (`Equals`, `GetHashCode`, `ToString`, `CompareTo`, a value object's
`GetEqualityComponents`) and a record's own. A package's base class isn't checked.

Application implements Levee's interfaces (handlers), its own ports' contracts and Domain's, never the shared ones. Domain
is held the same way, structs as much as classes. An operation is exempt: a shared marker on a message is data.

## LEVEE017: A Tool is a process

A Tool runs as the database's owner before the app exists: migrations, the role bootstrap. It may own its transaction
(LEVEE004 doesn't hold it) because nothing else runs it: nothing references a Tool, and it's built as an executable.

```xml
<PropertyGroup>
  <LeveeRole>Tool</LeveeRole>
  <OutputType>Exe</OutputType>
</PropertyGroup>
```

Every other rule holds: it drives the application only through the dispatcher, like a Host.

## LEVEE018: Data is reached only through the operation's unit of work

A connection of its own (`new NpgsqlConnection`, a `DbDataSource`'s, an `IDbContextFactory`'s context, a design-time
factory's `CreateDbContext`, which is `dotnet ef`'s to call, `new` on a context, the context's own connection through
`GetDbConnection`, EF's accessor to its internal services, `GetService<T>`, or a connection or data source the container
hands over, to a constructor, a minimal API handler's parameter (a lambda or a method group passed to a `Map*` call), an
action's parameter, or an injected property) is outside the operation's
transaction, or can be: not read-only when the operation declares only reads, not rolled back with it, not audited, and
free to set its own scope.

```csharp
// Wrong, in an adapter
await using var connection = await dataSource.OpenConnectionAsync(ct);

// Right: the context the unit of work holds, or its connection for something other than EF
internal sealed class Ledger(AppDbContext db, IAmbientTransaction ambient) : ILedger { ... }
```

Holding the operation's own connection, from `IAmbientTransaction`, in a field or a record is fine: it's the
transaction's. Hearing what changed (Postgres `LISTEN`) is Levee's own connection's: raise with `IChangeSignals.RaiseAsync`
in the operation, and watch with `IChangeSignals.WatchAsync` in a subscription. A record that must survive the operation rolling back (a paid vendor call) is an internal operation,
dispatched as the system. Work as the database's owner is a Tool's; Composition and a Host may resolve the data source
to configure the context with.

A separate bounded context's context (`[SeparateContext]`: identity's, for the library that serves it) is outside every
operation, so the app's own code never holds it: a field, a property, a parameter or a return of it outside Composition,
a Host or a Tool fails the build. Only the library's registration names it. See
[postgres.md](postgres.md#outside-an-operation).

## LEVEE019: Aggregate roots live in Domain

LEVEE007 guards an aggregate's state only in Domain; an aggregate declared anywhere else has no guard. Move it.

## LEVEE020: The catalog is the generator's

The dispatcher trusts what a descriptor says: whether the operation is platform-wide, internal, read-only. The generator
writes descriptors only from `[Operation(...)]`, after LEVEE104–106 have checked it. A descriptor, catalog or catalog part
built by hand (`OperationDescriptor.Create`, `new OperationCatalog`, `AddLeveePart`) skips every one of those checks, so
outside tests it fails the build. So does calling the composition's own plumbing by hand (an assembly's generated
`LeveeRegistration_*`, `PortSelection`): public only because the root's generated code, in another assembly, calls it.

Sending by name is Levee's projections' alone. A route of the app's that takes an operation's name and its input as
JSON (reading the operation's type from its descriptor, `InputType` or `ResultType`, to deserialize it) is a second
surface beside `MapLeveeOperations`, without the strict wire, the catalog's refusals, problem responses or rechecks. So
is a contract that sends itself: Kernel, Contracts and Domain never call the `Dispatcher`. And so is reflecting into
Levee's own types (`typeof(Dispatcher).GetMethod("SendJsonAsync", ...)`): what Levee keeps internal is its own. An API of the app's own
design is a Projection, its routes typed:

```csharp
// Wrong: a by-name surface of the app's own
app.MapPost("/v1/ops/{name}", (string name, JsonElement body, Dispatcher d) =>
    d.SendAsync((IOperation<object>)body.Deserialize(catalog.Find(name)!.InputType)!, ...));

// Right: Levee's, or a Projection's typed route
app.MapLeveeOperations(identify);
app.MapPost("/v1/notes", (CreateNoteBody body, Dispatcher d, ...) => d.SendAsync(new CreateNote(...), ...));
``` Declare the operation, and let `AddLeveeOperations` (or `AddLeveeTestOperations`) register
it.

## LEVEE101: Every operation has a handler

```csharp
// Application
internal sealed class CreateNoteHandler(INotes notes) : IOperationHandler<CreateNote, NoteView>
{
    public async Task<Result<NoteView>> HandleAsync(CreateNote op, OperationContext context, CancellationToken ct) { ... }
}
```

A subscription's handler is an `ISubscriptionHandler<TSub, TItem>`. `dotnet levee generate operation` writes the
contract and its handler together.

## LEVEE102: Every operation has one handler

Two handlers for one operation means the pipeline can't know which runs. A test's own handler for one of the app's
operations would quietly replace the app's, so it's refused too: fake the port the handler uses instead. If they do different things, they are
different operations.

## LEVEE103: Operation names are unique

The name is the wire contract: the route, the MCP tool, the CLI verb, the audit's record. Rename the newer one.

## LEVEE104: Operations are declared whole

An operation is declared in Contracts (in Application only if it's the platform's own, `Internal = true`: a client never
sees Application; a test may declare its own), as a public, top-level, non-generic, sealed class or record with `[Operation(name, access)]` and `IOperation<TResult>`
(or `ISubscription<TItem>`).

- **Name**: lowercase snake_case segments joined by dots, area first: `notes.create`, `billing.invoice_lines.list`.
- **Access**: the app's own vocabulary (`member`, `admin`, `platform-admin`); its `IAccessPolicy` decides it.
- **Errors**: the codes its handler fails with, named like operations: `notes.too_long`.
- **Subscriptions** only read: no `Effects`, no `Idempotency`, no `Errors`. Their input travels in the URL, which proxies
  and access logs keep, so it holds no `[Secret]` at any depth.
- **Typed all the way down**: no member, at any depth (fields `[JsonInclude]` adds, a base record's, a declared derived
  type's, the items of a collection of the app's own), is `object`, `dynamic`, raw JSON, `Array` or a non-generic
  collection (`ArrayList`, `Hashtable`, `ExpandoObject`). Redaction and the secret check go by type, and the wire's schema says nothing about an untyped member.
- **Value objects say what they write**: a type of the app's that a converter of its own writes (on the type, or on the
  member), in the input or the result, declares the primitive it's written as. No schema can read a converter, so
  without it every client and agent sees the member as anything at all. The members checked, here and for types and
  secrets, are the ones System.Text.Json writes: public properties, and fields and non-public properties `[JsonInclude]`
  adds, less any `[JsonIgnore]` always drops (`[JsonIgnore(Condition = WhenWritingNull)]` still writes it).

```csharp
[JsonConverter(typeof(ZipJson))]
[ValueObject(typeof(string), Pattern = "^[0-9]{5}$")]
public readonly record struct Zip(string Value);

public sealed record FindHomes([property: Description("The area's five-digit zip.")] Zip Area) : IOperation<HomeList>;
```

  A member's `[Description]` (System.ComponentModel's) is its description in every schema: the catalog's and each MCP
  tool's.

## LEVEE105: Operations with effects declare their idempotency

Requests are retried and jobs run at least once, so a write says what a repeat does:

- `Idempotent`: a repeat changes nothing (set a status, upsert by key).
- `Conflicts`: a repeat is refused. The conventional create: the client supplies the id, and a second insert is
  `conflict`.
- `None`: a repeat duplicates. Rarely right, and it can't be enqueued as a job.

```csharp
[Operation("tenants.create", "platform-admin", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record CreateTenant(Guid Id, string Name) : IOperation<TenantView>;
```

## LEVEE106: Every operation declares its scope

Scope is whose rows an operation may see; Postgres holds it with row-level security. Forgetting it can't silently
widen access, so each operation declares exactly one of three.

```csharp
// Scoped: the tenant (household, account) whose rows it touches
public sealed record CreateNote(Guid Id, string Tenant, string Text) : IOperation<NoteView>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

// The caller's: its credentials carry the tenant (an API key issued to one), so the input can't name another
[Operation("notes.mine", "key", CallerScoped = true)]
public sealed record MyNotes : IOperation<IReadOnlyList<NoteView>>;
// ...where the principal says it: public sealed record ApiKey(string Subject, string Tenant) : Principal(Subject), IScopedPrincipal

// Platform: an operator's operation across every scope; the access policy decides who may
[Operation("tenants.list", "platform-admin", Platform = true)]
public sealed record ListTenants : IOperation<IReadOnlyList<TenantView>>;
```

## LEVEE107: Every port is bound

A composition root that can't bind a port fails at the first request that needs it. Reference the Adapter project
that implements it, or write the adapter. A port that's off in some deployments gets a null-object adapter, named
and chosen by setting:

```csharp
[Port(SelectedBy = "Providers:Analytics")] public interface IAnalytics { ... }
[Adapter("posthog")] internal sealed class PostHogAnalytics(...) : IAnalytics { ... }
[Adapter("none")] internal sealed class NoAnalytics : IAnalytics { ... }
```

## LEVEE108: Every port is bound one way

- One adapter: unnamed, no `SelectedBy`.
- Several, one at a time: `[Port(SelectedBy = "Section:Key")]`, every adapter named, the setting naming one. Always
  set, never inferred.
- Several, all at once: `[Port(Many = true)]`, taken as `IEnumerable<TPort>`; name every adapter (or none) to route
  by name with `IAdapters<TPort>`. A set has no `SelectedBy` and no `Default`: every adapter serves, so there's no one
  for a setting to choose or a fallback to stand in for.

```csharp
// Wrong
[Port(Many = true, Default = "email")] public interface IChannels { ... }

// Right: every channel serves; route by name with IAdapters<IChannels>
[Port(Many = true)] public interface IChannels { ... }
```

## LEVEE109: Named adapters adapt a port

`[Adapter]` marks an implementation of a `[Port]` (or `IRepository<T>`). On anything else it registers nothing: implement
the port it was meant to serve, or remove the attribute.

## LEVEE110: Handlers fail only with the errors their operation declares

An operation's error codes are its contract: what a client branches on, listed in the catalog beside its input and
result. A code its handler returns but doesn't declare is one no client knows to expect.

```csharp
// Contracts
[Operation("notes.create", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts,
    Errors = [NoteErrors.TooLong, NoteErrors.Banned])]
public sealed record CreateNote(Guid Id, string Tenant, string Text) : IOperation<NoteView>, IScopedOperation { ... }

public static class NoteErrors
{
    public const string TooLong = "notes.too_long";
    public const string Banned = "notes.banned";
}

// Application
return Error.Validation(NoteErrors.TooLong, "A note holds 500 characters at most.");
```

Levee's own codes (`LeveeErrors`: `conflict`, `input.invalid`, `internal`, `store.contention` and the rest) need no
declaring. The build sees literal and constant codes in the handler; a code from elsewhere (a domain method, a port) that
the operation doesn't declare is logged as an error when it's returned.

In tests, `AddLeveeOperations(configuration, o => o.RefuseUndeclaredErrors = true)` turns that log into a failure: the
call answers `internal`, so the test asserting its code fails and the log says which code to declare.

Don't throw for a failure the caller should see: return it. A thrown exception is a bug: the dispatcher rolls back,
logs it once with its trace, and answers `internal` with nothing but the trace.

## LEVEE111: Access policies and admissions refuse only with the codes they declare

The access policy and admissions run before any operation, so what they refuse with can come back from every
operation. They declare it once, and the catalog lists it once (`OperationCatalog.Refusals`, `refusals` in `GET /ops`).

```csharp
[Refuses("access.tenant", "access.platform")]
internal sealed class AccessPolicy : IAccessPolicy
{
    public ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct) => ...
        Error.Forbidden("access.tenant", "Not your tenant.") ...
}
```

A client that sees only Contracts (`LeveeCatalog`) doesn't see these: the policy lives where the app composes.
