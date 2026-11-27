using Microsoft.CodeAnalysis;

namespace Levee.Analyzers;

/// <summary>
/// Every message says what's wrong, then "Fix:" and the conventional shape, because the build output is the one place
/// every agent in every repo reads. <c>dotnet levee explain</c> has the long form.
/// </summary>
internal static class Descriptors
{
    private const string Category = "Levee";

    private static DiagnosticDescriptor Rule(string id, string title, string message, params string[] tags) =>
        new(id, title, $"{message} (dotnet levee explain {id})", Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: $"https://rkingon.github.io/Levee/rules/{id.ToLowerInvariant()}/", customTags: tags);

    public static readonly DiagnosticDescriptor NoRole = Rule(
        "LEVEE001", "Every Levee project declares its role",
        "This project uses Levee but {1} Fix: set <LeveeRole> in the project file to the layer it is, one of: {0}",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor RoleReference = Rule(
        "LEVEE002", "A role references only what it may",
        "A {0} project may not reference {1} ({2}); it may reference only {3}. Fix: move the shared type inward (Kernel for ids and values, Contracts for what clients see); if the application needs something from outside, declare a port in Application and let an Adapter implement it",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor WallClock = Rule(
        "LEVEE003", "Time comes from TimeProvider",
        "{0} reads the wall clock. Fix: in an operation's handler, use OperationContext.Now (the operation's one time) and OperationContext.NewId() for a new id, never a clock of its own; a subscription's handler reads time through the operations it sends. In an adapter or a process, take a TimeProvider (or NodaTime's IClock) through the constructor. Only Composition, a Host or a Tool uses TimeProvider.System");

    public static readonly DiagnosticDescriptor HandlerClock = Rule(
        "LEVEE003", "Time comes from TimeProvider",
        "{0} is a handler holding a {1}, a clock of its own: in production it's the wall clock, not the operation's one time. Fix: take nothing for time; in an operation's handler, use OperationContext.Now (and OperationContext.NewId() for a new id); a subscription's handler reads time through the operations it sends. A TimeProvider (or NodaTime's IClock) through the constructor is for an adapter or a process, never a handler");

    public static readonly DiagnosticDescriptor Transaction = Rule(
        "LEVEE004", "Only the dispatcher saves and commits",
        "{0} saves or controls a transaction, but the dispatcher commits each operation once. Fix: change tracked state and return; take IFlush if a later query in the same operation must see pending writes; return an error, and the dispatcher rolls back");

    public static readonly DiagnosticDescriptor ScopeSetting = Rule(
        "LEVEE004", "Only the dispatcher saves and commits",
        "This SQL sets the scope row-level security trusts (levee.scope, levee.platform) or writes the audit, both the unit of work's alone. Fix: declare the operation's scope (IScopedOperation, CallerScoped or Platform) and let the dispatcher set it; read it with current_setting; every write is audited by the dispatcher");

    public static readonly DiagnosticDescriptor SignalByHand = Rule(
        "LEVEE022", "Change signals go only through IChangeSignals",
        "This SQL names levee_signals, the channel IChangeSignals raises change signals on, whose watchers trust the scope its payload names: a pg_notify, NOTIFY or LISTEN of the app's own reaches, or hears, any tenant's. Fix: raise the signal with IChangeSignals.RaiseAsync in the operation, which sends it with the operation's transaction, in its scope; watch with IChangeSignals.WatchAsync in a subscription's handler");

    public static readonly DiagnosticDescriptor ServiceLocation = Rule(
        "LEVEE005", "No service location outside hosts",
        "{0} locates services. Fix: take the dependency through the constructor; to choose an adapter by setting, declare [Port(SelectedBy = \"...\")]; to choose one at run time by name, take IAdapters<TPort>");

    public static readonly DiagnosticDescriptor Suppression = Rule(
        "LEVEE006", "Levee rules are suppressed only with a reason",
        "Suppressing {0}: {1}. Fix: change the code to follow the rule. While an app migrates onto Levee (<LeveeAllowSuppressions>true</LeveeAllowSuppressions>), a suppression beside the code, with a reason (#pragma warning disable LEVEEnnn // why, or [SuppressMessage(..., Justification = \"why\")]), lets it build while the code still breaks the rule, but dotnet levee check stays red until it's gone; where Levee can't express what the code needs, report that as a gap in Levee",
        WellKnownDiagnosticTags.NotConfigurable, WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor AggregateState = Rule(
        "LEVEE007", "Aggregates change only through their own methods",
        "{0} can be changed from outside aggregate root {1}. Fix: make its setter private (or init), expose a collection as IReadOnlyList<T> over a private List<T>, and change state through a method on {1} that enforces its invariants");

    public static readonly DiagnosticDescriptor PackageReference = Rule(
        "LEVEE008", "Inner roles reference only the BCL and abstractions",
        "A {0} project may not reference {1}: {2}",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor Unanalyzed = Rule(
        "LEVEE009", "Everything that uses Levee is built with its analyzers",
        "{0} references Levee but was built without its generator and analyzers, so none of its rules ran there. Fix: reference Levee from {0} without ExcludeAssets or PrivateAssets naming analyzers",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor PortPlacement = Rule(
        "LEVEE010", "Ports are declared in Application",
        "Port {0} is declared in a {1} project. Fix: move it to Application; a port is what the application needs, named for the need, and adapters implement it");

    public static readonly DiagnosticDescriptor AdapterPlacement = Rule(
        "LEVEE011", "Adapters implement ports, in Adapter projects",
        "{0} {1}");

    public static readonly DiagnosticDescriptor CoreIo = Rule(
        "LEVEE012", "The core does I/O only through ports",
        "{0} does I/O in a {1} project. Fix: declare a port in Application named for what the application needs (not for the vendor or the transport), take it through the handler's constructor, and implement it in an Adapter project");

    public static readonly DiagnosticDescriptor HostReach = Rule(
        "LEVEE013", "The Host reaches the application only through the dispatcher",
        "{0} is {1}'s, and a Host names nothing of Application's or Domain's and drives nothing of theirs or an adapter's. Fix: send an operation through the Dispatcher; move wiring that needs {0} into Composition, and owner work (migrations) into a Tool");

    public static readonly DiagnosticDescriptor CompositionSurface = Rule(
        "LEVEE014", "Composition exposes only registration",
        "{0} is public in a Composition project. Fix: make it internal, and expose an extension method that registers or maps it (on IServiceCollection, an endpoint builder), taking and returning only framework, Kernel and Contracts types");

    public static readonly DiagnosticDescriptor CompositionDrives = Rule(
        "LEVEE015", "Composition wires the application, never drives it",
        "{0} calls into the application from Composition. Fix: construct, bind and register it here, and drive it by sending an operation through the Dispatcher; an adapter's own setup (its options, an HttpClient) is one IServiceCollection extension in its Adapter project, which Composition calls");

    public static readonly DiagnosticDescriptor ApplicationExposed = Rule(
        "LEVEE016", "Application is reached only through operations",
        "{0} implements or derives from {1} from {2}, so whatever holds a {1} calls the application around the dispatcher. Fix: keep the logic in a handler, and implement {1} in Composition by sending operations through the Dispatcher, as a principal it makes there");

    public static readonly DiagnosticDescriptor ToolProcess = Rule(
        "LEVEE017", "A Tool is a process",
        "This Tool project builds a library, but a Tool is a process, run as the database's owner, that nothing references. Fix: set <OutputType>Exe</OutputType>; code a Host or Composition needs belongs in another role",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor SideConnection = Rule(
        "LEVEE018", "Data is reached only through the operation's unit of work",
        "{0} opens a connection of its own, outside the operation's transaction: it isn't read-only when declared, isn't rolled back, isn't audited, and can set its own scope. Fix: use the DbContext the unit of work holds (or IAmbientTransaction's connection); a write that must outlive the operation is an internal operation sent as the system by code of Composition's or the Host's own, which makes the principal there and never hands it on (LEVEE021); to hear what changed (LISTEN), raise and watch IChangeSignals; work as the database's owner is a Tool's");

    public static readonly DiagnosticDescriptor SeparateContextReach = Rule(
        "LEVEE018", "Data is reached only through the operation's unit of work",
        "{0} reaches {1}, a separate bounded context's store ([SeparateContext]), from the app's own code: its data is outside every operation, unscoped and unaudited. Fix: leave {1} to the library it serves, and name it only as a registration's type argument in Composition or the Host (AddDbContext<{1}>, UseDbContext<{1}>), never as a parameter, field, property or local, nor resolved from the container; what the app needs of that context is an operation of its own, or a port the context's adapter serves through the unit of work");

    public static readonly DiagnosticDescriptor OwnerRun = Rule(
        "LEVEE018", "Data is reached only through the operation's unit of work",
        "LeveeOwner.Run steps around the guard that keeps the app's context inside operations, for work as the database's owner. Fix: move migrations and bootstrap into a Tool project; everything else sends an operation through the Dispatcher");

    public static readonly DiagnosticDescriptor AggregatePlacement = Rule(
        "LEVEE019", "Aggregate roots live in Domain",
        "{0} is an aggregate root in a {1} project, where nothing guards its state. Fix: move it to a Domain project, where LEVEE007 holds it to changing only through its own methods");

    public static readonly DiagnosticDescriptor CatalogByHand = Rule(
        "LEVEE020", "The catalog is the generator's",
        "{0} builds part of the catalog or the composition by hand, where no rule checks what it claims (Platform, Internal, Effects, which adapter serves a port) and the dispatcher trusts it. Fix: declare the operation with [Operation(...)] and let the generated AddLeveeOperations (or AddLeveeTestOperations) register it");

    public static readonly DiagnosticDescriptor ByName = Rule(
        "LEVEE020", "The catalog is the generator's",
        "{0}: a surface of the app's own that sends operations by name, beside Levee's, holds none of what Levee's does (the strict wire, the catalog's refusals, problem responses, rechecks). Fix: MapLeveeOperations (HTTP) and WithLeveeOperations (MCP) are the by-name surface; an API of the app's own design is a Projection project, its routes typed, each sending its operation through the Dispatcher");

    public static readonly DiagnosticDescriptor Reflected = Rule(
        "LEVEE020", "The catalog is the generator's",
        "{0} reflects into {1}, one of Levee's own types: what Levee keeps internal is its own, and what it makes public is called, not looked up. Fix: call the public API; to send by name, MapLeveeOperations (HTTP) and WithLeveeOperations (MCP) are the surface, and in-process code sends the operation it holds through Dispatcher.SendAsync");

    public static readonly DiagnosticDescriptor ProcessSends = Rule(
        "LEVEE021", "Who an operation runs as is decided where the app composes",
        "{0} is a process that holds the Dispatcher, so what it sends runs as whoever it names, in any scope, not as the caller that started it, in theirs. Fix: send each step through the ProcessContext RunAsync is given (context.SendAsync), which runs it as the process's starter, in their scope");

    public static readonly DiagnosticDescriptor PrincipalMade = Rule(
        "LEVEE021", "Who an operation runs as is decided where the app composes",
        "{0} makes a principal in a {1} project, deciding there who an operation runs as and so what it may reach. Fix: act as the caller, the one the context you're given names: a handler's OperationContext.Principal, a subscription's SubscriptionContext.Principal, a process's ProcessContext.SendAsync. Acting as the system is the Host's or Composition's own code (a hosted service, a scheduled trigger, a Composition class implementing a shared interface), which makes the principal and sends through the Dispatcher there, never handing it to Application or an adapter");

    public static readonly DiagnosticDescriptor PrincipalHeld = Rule(
        "LEVEE021", "Who an operation runs as is decided where the app composes",
        "{0} holds a {1} in a {2} project, so what it sends runs as that principal, in any scope, not as its caller in theirs; whatever holds it (a process, a handler) escapes the caller too. Fix: act as the caller, the one the context you're given names: a handler's OperationContext.Principal, a subscription's SubscriptionContext.Principal, a process's ProcessContext.SendAsync; a helper that sends takes the principal as a method's argument, never holds it. Acting as the system is the Host's or Composition's own code (a hosted service, a scheduled trigger, a Composition class implementing a shared interface), which makes the principal and sends through the Dispatcher there");

    public static readonly DiagnosticDescriptor MissingHandler = Rule(
        "LEVEE101", "Every operation has a handler",
        "Operation {0} ({1}) has no handler. Fix: add an internal sealed IOperationHandler<{1}, TResult> (ISubscriptionHandler for a subscription) in Application; dotnet levee generate operation scaffolds one");

    public static readonly DiagnosticDescriptor DuplicateHandler = Rule(
        "LEVEE102", "Every operation has one handler",
        "Operation {0} ({1}) has {2} handlers. Fix: keep one; if they do different things, they are different operations");

    public static readonly DiagnosticDescriptor DuplicateName = Rule(
        "LEVEE103", "Operation names are unique",
        "Operation name '{0}' is declared by {1}. Fix: rename one; a name is the wire contract, so keep the one clients already call");

    public static readonly DiagnosticDescriptor MalformedOperation = Rule(
        "LEVEE104", "Operations are declared whole", "{0}: {1}");

    public static readonly DiagnosticDescriptor WriteIdempotency = Rule(
        "LEVEE105", "Operations with effects declare their idempotency",
        "{0} declares effects {1} but not its idempotency. Fix: set Idempotency = Idempotent if a repeat changes nothing (set a status, upsert by key); Conflicts if a repeat is refused (a create with a client-supplied id: the conventional create); None only if a repeat duplicates, which also bars it from jobs");

    public static readonly DiagnosticDescriptor ScopeUndeclared = Rule(
        "LEVEE106", "Every operation declares its scope",
        "{0} {1}. Fix: declare exactly one: IScopedOperation when its input names the tenant (or household, account) whose rows it touches; CallerScoped = true when the caller's credentials carry it (an API key issued to a tenant); Platform = true only for an operator's operation across every scope");

    public static readonly DiagnosticDescriptor UnboundPort = Rule(
        "LEVEE107", "Every port is bound",
        "Port {0} has no adapter among what this root references. Fix: reference the Adapter project that implements it, or write an [Adapter]; a port that may be off in some deployments gets a named null-object adapter (\"none\") chosen by its SelectedBy setting");

    public static readonly DiagnosticDescriptor PortBinding = Rule(
        "LEVEE108", "Every port is bound one way", "Port {0}: {1}");

    public static readonly DiagnosticDescriptor OrphanAdapter = Rule(
        "LEVEE109", "Named adapters adapt a port",
        "{0} is marked [Adapter] but implements no port. Fix: implement the [Port] interface (or IRepository<T>) it serves, or remove [Adapter]");

    public static readonly DiagnosticDescriptor UndeclaredError = Rule(
        "LEVEE110", "Handlers fail only with the errors their operation declares",
        "The handler for {0} fails with '{1}', which {0} doesn't declare. Fix: add it to {0}'s [Operation(..., Errors = [\"{1}\"])] so every client knows to expect it, or fail with a code it declares");

    public static readonly DiagnosticDescriptor UndeclaredRefusal = Rule(
        "LEVEE111", "Access policies and admissions refuse only with the codes they declare",
        "{0} refuses with '{1}', which it doesn't declare. Fix: add it to [Refuses(...)] on {0}, so the catalog lists it for every client");

    public static readonly DiagnosticDescriptor DuplicateProcess = Rule(
        "LEVEE112", "Every process input has one process",
        "Process input {0} is run by {1} processes ({2}), and a queued job names only its input, so which one runs is the container's registration order. Fix: keep one IProcess<{0}>; if they do different things, give each an input type of its own");

    public static readonly DiagnosticDescriptor ScopeUnread = Rule(
        "LEVEE113", "An access policy decides by scope",
        "{0} authorizes without reading request.Scope.Key, but an input-scoped operation runs in whatever scope its input names, so a caller it grants in one tenant is granted every tenant's rows; asking only whether the scope is the platform's (Scope.Platform) doesn't tell one tenant from another. Fix: grant a tenant's operation only where request.Scope.Key is what the caller may reach (request.Principal is Member m && request.Scope.Key == m.Tenant), and, as a separate case, grant platform scope (request.Scope.Platform) only to the platform's operators");

    /// <summary>Every rule's id, for LEVEE006's check of what the build configuration weakens.</summary>
    public static readonly string[] Ids =
    [
        "LEVEE001", "LEVEE002", "LEVEE003", "LEVEE004", "LEVEE005", "LEVEE006", "LEVEE007", "LEVEE008", "LEVEE009",
        "LEVEE010", "LEVEE011", "LEVEE012", "LEVEE013", "LEVEE014", "LEVEE015", "LEVEE016", "LEVEE017", "LEVEE018", "LEVEE019", "LEVEE020",
        "LEVEE021", "LEVEE022",
        "LEVEE101", "LEVEE102", "LEVEE103", "LEVEE104", "LEVEE105", "LEVEE106", "LEVEE107", "LEVEE108", "LEVEE109", "LEVEE110", "LEVEE111",
        "LEVEE112", "LEVEE113",
    ];
}
