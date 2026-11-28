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

// Wrong: a handler with a clock of its own, the wall clock in production
internal sealed class AnnotateHandler(TimeProvider time) : IOperationHandler<Annotate, Annotated> { ... time.GetUtcNow() ... }

// Right, in a handler: the operation's one time, at Postgres's precision
var due = context.Now.AddDays(30);

// Right, in an adapter or a process
internal sealed class Lease(TimeProvider time) : ILease { DateTimeOffset Now => time.GetUtcNow(); }
```

A handler (`IOperationHandler`, `ISubscriptionHandler`) holds no clock: a `TimeProvider` or NodaTime `IClock` as its
constructor's parameter, a field or a property is refused, wrapped or not. So is a port that tells the time (an interface
or a delegate answering a date or time at once without being handed one: `ITime.Current()`, `Func<DateTime>`,
`IBusinessCalendar.Today(timeZone)`): whatever it wraps, it's the wall clock in production. A time worked out from
another is handed it (`calendar.NextBusinessDay(context.Now)`); a vendor's is awaited. In Application only a process
holds a clock: a helper the handlers share takes the time as an argument, `Deadlines.Due(context.Now)`. An operation's handler reads
`OperationContext.Now`; a subscription's handler reads time through the operations it sends, each with its own.

`Guid.CreateVersion7()` reads the clock too. In a handler, `context.NewId()` is the id: a UUIDv7 at the operation's time,
and later than every id the operation made before it, which `Guid.CreateVersion7(context.Now)` isn't (every id in one
operation shares its one time, so their order would be random). Elsewhere, pass the time.

Only Composition, a Host or a Tool uses `TimeProvider.System`; tests register a `FakeTimeProvider`. NodaTime is held the
same way: its `SystemClock.Instance` is registered as the `IClock` where the app composes, and everything else takes the
`IClock` through its constructor. `Microsoft.VisualBasic.DateAndTime`'s `Now`, `Today`, `TimeOfDay`, `Timer`, `DateString`
and `TimeString` are the wall clock, like `DateTime.Now`.

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
tenant's rows. Only the unit of work sets them, and only it calls the idempotency key functions (`levee_idempotency_*`);
text that names them without a statement in it (a message) is fine. A setting's bare name is allowed only where it's read: passed with SQL
that reads it (`current_setting`) and sets nothing, or held in a `const` or a local used only that way. Anywhere else (a
`static readonly` field, a parameter's value, a format string's argument) it's refused. An EF migration's SQL is the
owner's, run at migrate and never inside an operation, so it may write the audit (a backfill from an old one): the SQL
written inline as an argument to the `MigrationBuilder` (`migrationBuilder.Sql(...)`, a column's `defaultValueSql`) in a
`Migration` subclass. The rest of the migration class (a helper method, a constant, a local holding the SQL) is held like
any other code.

```csharp
// Wrong: a migration's helper, callable from anywhere
public static string Backfill() => "select levee_audit_write(...)";

// Right: handed to the builder where it's written
protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("select levee_audit_write(...)");
```

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

`IKeyedServiceProvider` and `ActivatorUtilities` are service location by another name, and so is reflecting on what
locates (`typeof(IServiceProvider).GetMethod("GetService").Invoke(...)`, the DI extension classes, `ActivatorUtilities`),
seen as LEVEE020 sees reflection into Levee's own types. To choose an adapter by
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
a Test role on a project that doesn't set `IsTestProject` and reference a test framework are refused. So is a
TestSupport role on a test project or an executable: TestSupport is held to no code rule because only tests reference it,
and nothing references a process, so it's a library (a fixture library references `xunit.v3.extensibility.core`, not
`xunit.v3`, which makes an executable). `dotnet levee check` compiles once without analyzers, recognised by a token it
puts in its own environment.

A file marked `// <auto-generated/>` is held to the rules like any other. The one exemption is .NET's own source
generators' output (the regex generator's is a bare `#pragma warning disable`), recognised by the path the compiler gives
it: the generator's assembly, then its generator type, then the file
(`obj/Debug/net10.0/System.Text.RegularExpressions.Generator/System.Text.RegularExpressions.Generator.RegexGenerator/RegexGenerator.g.cs`),
so a directory of the app's own named like a generator isn't exempt. LEVEE006 itself is never suppressed.

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
package, and a hosted service or loop lives in Composition or the Host, sending operations through the `Dispatcher` as a
principal it makes there (an adapter can neither make nor hold one: LEVEE021).

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
called from Composition, so its options are parsed inside that extension, not by Composition. The extension has the
registration's shape: it returns the `IServiceCollection` (or nothing), and Composition calls it to register, as a
statement, chained into the next registration, or returned from its own; one called for a value (a count, a client it
builds) is the adapter's logic run from Composition, and is refused.

```csharp
// Adapter
public static class ZipCodeApi
{
    public static IServiceCollection AddZipCodeApi(this IServiceCollection services, IConfiguration configuration) =>
        services.Configure<ZipCodeApiOptions>(configuration.GetSection("ZipCodeApi"));
}

// Composition
services.AddZipCodeApi(configuration);

// Wrong: an adapter's calculation, run from Composition
var limit = services.ZipCodeQuota(configuration);
```

## LEVEE016: Application is reached only through operations

An Application class that implements an interface from Kernel, Contracts or a package (a vendor's `IHealthCheck`, a
library's callback) is handed to whatever holds that interface: a gateway, a Host, Composition, the package itself. They call it directly, and the dispatcher never sees the call: no authorization, scope,
unit of work or audit.

```csharp
// Wrong: Application
internal sealed class JobBroker : IWorkDesk { public Task<Job?> NextAsync(...) { ... } }

// Right: the logic is a handler; Composition implements the shared interface by dispatching, as a principal it makes
[Operation("work.next", "runtime", Internal = true, Platform = true, ...)]
public sealed record NextJob(...) : IOperation<Job?>;

internal sealed class DispatchedDesk(Dispatcher dispatcher) : IWorkDesk
{
    private static readonly SystemPrincipal Runtime = new("runtime");

    public async Task<Job?> NextAsync(...) => (await dispatcher.SendAsync(new NextJob(...), Runtime, ct)).Value;
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
sent as the system by Composition's or the Host's own code, which makes the principal there and hands it to nothing
(LEVEE021): the adapter takes a shared interface that Composition implements by dispatching, as LEVEE016 shows. Work as the database's owner is a Tool's; Composition and a Host may resolve the data source
to configure the context with.

A separate bounded context's context (`[SeparateContext]`: identity's, for the library that serves it) is outside every
operation, so the app's own code never holds it, in any role: a field, a property, a parameter, a local, a return of it,
or resolving it from the container (`GetRequiredService<IdentityDb>()`) fails the build, in Composition, a Host and a Tool
too. Only the library's registration names it, as a type argument; EF's design-time factory for it is `dotnet ef`'s. See
[postgres.md](postgres.md#outside-an-operation).

```csharp
// Wrong: in Composition, a Host or a Tool as anywhere
var identity = provider.GetRequiredService<IdentityDb>();

// Right: named for the library to use
services.AddDbContext<IdentityDb>(o => o.UseNpgsql(identityConnectionString));
services.AddOpenIddict().AddCore(o => o.UseEntityFrameworkCore().UseDbContext<IdentityDb>());
```

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
is a contract that sends itself: Kernel, Contracts and Domain never call the `Dispatcher`. So is the same name-to-type map
read off the contract by reflection: `OperationAttribute` is only ever written on a contract, so naming it anywhere but an
attribute (`GetCustomAttribute<OperationAttribute>()`, `GetCustomAttributes(false).OfType<OperationAttribute>()`,
`a is OperationAttribute`, a cast, `typeof(OperationAttribute)` outside an attribute's argument, an alias) is refused, and an operation whose type is known only at run time sent through `dynamic`
(`dispatcher.SendAsync((dynamic)operation, ...)`). And so is reflecting into
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

Reflection is refused where the analyzer can name the Levee type statically: a lookup (`Type`'s `GetMethod`,
`GetField`, `GetMembers` and the rest, `System.Reflection.TypeExtensions`' and `RuntimeReflectionExtensions`' of them,
`TypeInfo`'s `GetDeclaredMethod` and `DeclaredMethods`) or an instance made (`Activator.CreateInstance`,
`RuntimeHelpers.GetUninitializedObject`) on `typeof(T)`, `x.GetType()` of one, `Type.GetType("Levee....")`, its
`GetTypeInfo()`, or a local, or a field initialized in the same file, holding one; and an `[UnsafeAccessor]` whose target
is one (its first parameter's type, or the type a constructor accessor returns; `[UnsafeAccessorType("Levee...")]` too).
It's static analysis: a type reached through a parameter, a field set elsewhere, a name built at run time or an assembly's
`GetTypes()` isn't seen. Those are deliberate, and the [trust model](decisions.md#trust-model) answers them, not the
build.

## LEVEE021: Who an operation runs as is decided where the app composes

The principal an operation runs as decides what the access policy lets it do and, with the operation's scope, which rows
it reaches. An operation acts as its caller, and a process as the caller that started it, in the scope it was started in.
A principal made anywhere else (a `SystemPrincipal` in a handler, a `JobPrincipal` in an adapter) acts as someone the
caller isn't, and a process holding the `Dispatcher` sends as whoever it names, in any scope.

```csharp
// Wrong: started by a member in one tenant, it runs at platform scope as the system
internal sealed class RecountProcess(Dispatcher dispatcher) : IProcess<RecountInput>
{
    public Task RunAsync(RecountInput input, ProcessContext context, CancellationToken ct) =>
        dispatcher.SendAsync(new TallyAllNotes(), new SystemPrincipal("recount"), ct);
}

// Right: each step as its starter, in their scope
internal sealed class RecountProcess : IProcess<RecountInput>
{
    public Task RunAsync(RecountInput input, ProcessContext context, CancellationToken ct) =>
        context.SendAsync(new TallyNotes(input.Tenant), ct);
}
```

A principal is made only in Composition, a Host or a Tool (and tests): constructing one (or a `with` of one) in Kernel,
Contracts, Domain, Application or an Adapter fails the build. Nor is one held there: a constructor's parameter, a field
or a property of a principal type (`SystemPrincipal`, `Principal`, the app's own, or one wrapped in `Lazy<T>`, `Func<T>`
or an array) is refused, because what holds one sends as it, and so does whatever holds the holder:

```csharp
// Wrong: the Host registers a SystemPrincipal; a class of Application's takes it, and the process takes the class
internal sealed class Escalator(Dispatcher dispatcher, SystemPrincipal system)
{
    public Task<Result<int>> TallyAsync(CancellationToken ct) => dispatcher.SendAsync(new TallyAllNotes(), system, ct);
}
internal sealed class RecountProcess(Escalator escalator) : IProcess<RecountInput> { ... }
```

Code in those roles acts as its caller, the one the context it's given names: a handler's `OperationContext.Principal`, a
subscription's `SubscriptionContext.Principal`, a process's `ProcessContext.SendAsync`. A helper that sends takes the
principal as a method's argument. Work that must run as the system (a record that outlives the operation's rollback, a
nightly sweep) is the Host's or Composition's own code: a hosted service, a scheduled trigger, or a Composition class
implementing a shared interface (LEVEE016's example), which makes the principal and sends through the `Dispatcher`
there, and hands it to nothing.

## LEVEE022: Change signals go only through IChangeSignals

`IChangeSignals` raises a change signal on Postgres's `levee_signals` channel, with the operation's transaction and in
its scope, and every watcher trusts the scope the payload names. SQL of the app's own naming the channel
(`pg_notify('levee_signals', ...)`, `NOTIFY levee_signals`, `LISTEN levee_signals`) names any scope it likes, so its
notification reaches every tenant's watchers, and a listener of its own hears them all.

```csharp
// Wrong: a signal by hand, in whatever scope the payload says
await db.Database.ExecuteSqlAsync($"select pg_notify('levee_signals', {payload})", ct);

// Right: raised in the operation, sent when it commits, in its scope
await signals.RaiseAsync("notes.changed", note.Id.ToString(), ct);
```

Watch with `IChangeSignals.WatchAsync` in a subscription's handler. The analyzer sees the channel's name in a literal, a
constant, a concatenation or an interpolation; a name put together at run time by a call (`string.Join`, configuration)
isn't seen, and is the trust model's to answer.

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
sees Application; a test may declare its own), as a public, top-level, non-generic, sealed class or record with `[Operation(name, access)]` and exactly one
`IOperation<TResult>` (or `ISubscription<TItem>`): a second would have a caller sending it as one read the result of the other.

- **Name**: lowercase snake_case segments joined by dots, area first: `notes.create`, `billing.invoice_lines.list`.
- **Access**: the app's own vocabulary (`member`, `admin`, `platform-admin`); its `IAccessPolicy` decides it.
- **Errors**: the codes its handler fails with, named like operations: `notes.too_long`.
- **Subscriptions** only read: no `Effects`, no `Idempotency`, no `Errors`. Their input travels in the URL, which proxies
  and access logs keep, so it holds no `[Secret]` at any depth.
- **Typed all the way down**: no member, at any depth (fields `[JsonInclude]` adds, a base record's, a declared derived
  type's, the items of a collection of the app's own), is `object`, `dynamic`, raw JSON, `Array` or a non-generic
  collection (`ArrayList`, `Hashtable`, `ExpandoObject`). Redaction and the secret check go by type, and the wire's schema says nothing about an untyped member.
- **Contracts are written in the contract language**, small enough that a type's schema is its parameter list:
  values (strings, numbers, bool, Guid, dates and times, `Uri`, `Version`, enums, value objects); arrays, `List<T>`,
  `IReadOnlyList<T>` and their kin, and dictionaries keyed by a value; plain positional records,
  `record Line(string Sku, int Quantity)`, whose members are their parameters; and a choice of shapes, an abstract record
  with no members whose `[JsonDerivedType(typeof(Card), "card")]` subtypes are plain positional records. A contract's
  records are its own, declared beside its operations. Anything else C# can say about a type is outside it: another
  property or field, a constructor, a base, a struct, an interface, a JSON attribute (`[JsonPropertyName]` aside). A
  checked value (an email, a positive quantity) is a value object, its converter the check. The catalog holds the same
  rule where it's made, from the wire's own metadata.
- **Value objects say what they write**: a type of the app's that a converter of its own writes, in the input or the
  result, declares the primitive it's written as. No schema can read a converter, so without it every client and agent
  sees the member as anything at all. A value object is one JSON scalar (a string, number, bool, enum, Guid, date or
  time): `[ValueObject(typeof(JsonElement))]` or a record would hand its converter whatever shape it likes. A value with
  members of its own is a record the serializer writes, with no converter. The converter goes on the value's type,
  never on a member, where it would outrank the type's and its check; a library's attribute deriving
  `JsonConverterAttribute` is a converter too. The contract wire holds the same at run time. The members checked, here
  and for types and secrets, are the ones System.Text.Json writes: public properties, and fields and non-public properties `[JsonInclude]`
  adds. A contract member left off the wire (`[JsonIgnore]`, with any condition) is refused: sent anyway, it would be
  skipped, not refused.

```csharp
[JsonConverter(typeof(ZipJson))]
[ValueObject(typeof(string), Pattern = "^[0-9]{5}$")]
public readonly record struct Zip(string Value);

public sealed record FindHomes([property: Description("The area's five-digit zip.")] Zip Area) : IOperation<HomeList>;
```

  A member's `[Description]` (System.ComponentModel's) is its description in every schema: the catalog's and each MCP
  tool's. The contract wire is Levee's, one and unconfigurable (`LeveeJson.Options`): strict, with
  numbers as numbers, so a value's JSON is its value object's own (on the type, where the build sees it) and a .NET
  type's is System.Text.Json's. A contract can't loosen it either: `[JsonNumberHandling]`,
  `[JsonUnmappedMemberHandling(Skip)]`, `[JsonExtensionData]` and `[JsonIgnore]` are refused, in the build and on the wire: a
  contract carries what's on its wire and nothing else. On the way in, a value object's converter is handed only the JSON its `[ValueObject]`
  says it writes, so a converter that would take anything can't make its schema a lie.

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
widen access, so each operation declares exactly one of four.

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

// Unscoped: no tenant's rows at all (a health check, a public lookup in a Shared table); anyone may run it safely
[Operation("system.ping", "anyone", Unscoped = true)]
public sealed record Ping : IOperation<string>;

// Platform: an operator's operation across every scope; the access policy decides who may
[Operation("tenants.list", "platform-admin", Platform = true)]
public sealed record ListTenants : IOperation<IReadOnlyList<TenantView>>;
```

`Unscoped` isn't a smaller `Platform`: platform scope sees every tenant's rows, an unscoped operation sees none.
Reaching for `Platform` because an operation touches no tenant hands every row to whoever may run it.

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

## LEVEE112: Every process input has one process

A queued job names only its input's type, and the worker runs the one `IProcess<TInput>` the container resolves for it.
Two processes for one input (two Application projects', a base class's subclasses, a test's beside the app's) build and
register, and which one runs an already queued job is registration order. Checked where the catalog is built, across the
composed app.

```csharp
// Wrong: two processes claim TurnStarted
internal sealed class RunTurn : IProcess<TurnStarted> { ... }
internal sealed class ScoreTurn : IProcess<TurnStarted> { ... }

// Right: one process per input; different work is a different input
internal sealed class RunTurn : IProcess<TurnStarted> { ... }
internal sealed class ScoreTurn : IProcess<TurnScored> { ... }
```

## LEVEE113: An access policy decides by scope

An input-scoped operation runs in the scope its input names, and row-level security then holds it to exactly that
scope: the one the caller picked. So the access policy decides who may act *where*, not only who may act. A policy that
grants by the access word alone (`"member" => request.Principal is Member ? null : ...`) lets any member into any tenant,
and so does one that asks only whether the scope is the platform's: `Scope.Platform` says the operation sees every
tenant's rows, not which tenant's a keyed one sees. A null `Key` is either every tenant's (`Platform`) or none
(`Unscoped`), so a policy never grants on `Key is null` alone.

```csharp
// Wrong: any member, in whatever tenant the input names
"member" => request.Principal is Member ? null : Error.Forbidden("access.members_only", "Sign in."),

// Wrong: refuses the platform's scope, then grants any member any tenant
"member" when request.Scope.Platform => Error.Forbidden("access.members_only", "Members act in a tenant."),
"member" => request.Principal is Member ? null : Error.Forbidden("access.members_only", "Sign in."),

// Right: a member, in their own tenant; the platform's scope only to its operators
"member" => request.Principal is Member m && request.Scope.Key == m.Tenant ? null : Error.Forbidden("access.tenant", "Not your tenant."),
"platform-admin" => request.Scope.Platform && request.Principal is Operator ? null : Error.Forbidden("access.platform", "Operators only."),
// Right: anyone, only where no tenant's rows are seen
"anyone" when request.Scope.Unscoped => null,
```

The build refuses a policy (outside tests) that never reads the scope's `Key`, in a member access
(`request.Scope.Key`), a property pattern (`request is { Scope.Key: var tenant }`) or a deconstruction that binds it, in its
own code: a helper of its own that takes the scope and reads `Key` counts, one elsewhere doesn't. Reading only
`Scope.Platform` doesn't count. A policy that grants nothing (every path returns one of `Error`'s refusals, or throws)
needn't read it. Reading it is the build's to see; comparing it rightly is the policy's, and the access
tests'.

## LEVEE114: A versioned operation writes

An edit made against what its caller saw (`IVersionedOperation`) is held to the version of one aggregate: the one root
it loads and changes. The version is compared as that root is written (Postgres's own `xmin` in the update's `where`), so
an operation that names one has to write: a read can't be stale.

```csharp
// Wrong: a version held to nothing
[Operation("notes.edit", "member")]
public sealed record EditNote(Guid Id, string Tenant, string Text, uint Version) : IOperation<NoteView>, IScopedOperation, IVersionedOperation { ... }

// Right: it writes, and its handler loads the root it changes, changes it through its own method, and saves
[Operation("notes.edit", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent, Errors = ["note.empty", "note.missing"])]
public sealed record EditNote(Guid Id, string Tenant, string Text, uint Version) : IOperation<NoteView>, IScopedOperation, IVersionedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
    uint IVersionedOperation.ExpectedVersion => Version;
}
internal sealed class EditNoteHandler(INotes notes) : IOperationHandler<EditNote, NoteView>
{
    public async Task<Result<NoteView>> HandleAsync(EditNote operation, OperationContext context, CancellationToken ct)
    {
        var text = NoteText.From(operation.Text);   // the domain's own rule for a note's text
        if (!text.IsSuccess) return text.Error;
        if (await notes.FindAsync(operation.Id, ct) is not { } note) return Error.NotFound("note.missing", "There's no such note.");
        note.Edit(text.Value);
        await notes.SaveAsync(ct);   // flushes, so the view carries the version just written
        return View(note);
    }
}
```

How the handler reaches the root (its repository, or a helper of Application's that holds it) is the build's to leave
alone: it's held at run time. The one root the operation changes is held to the version (refused as 409 `store.stale`
when it moved on); one it loaded but didn't change is compared as it was read. A versioned operation that loads no root,
creates one, or changes more than one aggregate, is a bug, answered `internal`: what follows in another aggregate is an
operation of its own, enqueued as a job.
