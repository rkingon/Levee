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
        "{0} reads the wall clock. Fix: in a handler, use OperationContext.Now (the operation's one time); elsewhere, take a TimeProvider through the constructor. Only Composition, a Host or a Tool uses TimeProvider.System");

    public static readonly DiagnosticDescriptor Transaction = Rule(
        "LEVEE004", "Only the dispatcher saves and commits",
        "{0} saves or controls a transaction, but the dispatcher commits each operation once. Fix: change tracked state and return; take IFlush if a later query in the same operation must see pending writes; return an error, and the dispatcher rolls back");

    public static readonly DiagnosticDescriptor ScopeSetting = Rule(
        "LEVEE004", "Only the dispatcher saves and commits",
        "This SQL sets the scope row-level security trusts (levee.scope, levee.platform) or writes the audit, both the unit of work's alone. Fix: declare the operation's scope (IScopedOperation, CallerScoped or Platform) and let the dispatcher set it; read it with current_setting; every write is audited by the dispatcher");

    public static readonly DiagnosticDescriptor ServiceLocation = Rule(
        "LEVEE005", "No service location outside hosts",
        "{0} locates services. Fix: take the dependency through the constructor; to choose an adapter by setting, declare [Port(SelectedBy = \"...\")]; to choose one at run time by name, take IAdapters<TPort>");

    public static readonly DiagnosticDescriptor Suppression = Rule(
        "LEVEE006", "Levee rules are suppressed only with a reason",
        "Suppressing {0}: {1}. Fix: change the code to follow the rule. A suppression beside the code, with a reason (#pragma warning disable LEVEEnnn // why, or [SuppressMessage(..., Justification = \"why\")]), lets a migration build while the code still breaks the rule, but dotnet levee check stays red until it's gone; where Levee can't express what the code needs, report that as a gap in Levee",
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
        "{0} calls into the application from Composition. Fix: construct, bind and register it here, and drive it by sending an operation through the Dispatcher");

    public static readonly DiagnosticDescriptor ApplicationExposed = Rule(
        "LEVEE016", "Application is reached only through operations",
        "{0} implements or derives from {1} from {2}, so whatever holds a {1} calls the application around the dispatcher. Fix: keep the logic in a handler, and implement {1} in Composition or an Adapter by sending operations through the Dispatcher");

    public static readonly DiagnosticDescriptor ToolProcess = Rule(
        "LEVEE017", "A Tool is a process",
        "This Tool project builds a library, but a Tool is a process, run as the database's owner, that nothing references. Fix: set <OutputType>Exe</OutputType>; code a Host or Composition needs belongs in another role",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor SideConnection = Rule(
        "LEVEE018", "Data is reached only through the operation's unit of work",
        "{0} opens a connection of its own, outside the operation's transaction: it isn't read-only when declared, isn't rolled back, isn't audited, and can set its own scope. Fix: use the DbContext the unit of work holds (or IAmbientTransaction's connection); a write that must outlive the operation is an internal operation dispatched as the system; work as the database's owner is a Tool's");

    public static readonly DiagnosticDescriptor OwnerRun = Rule(
        "LEVEE018", "Data is reached only through the operation's unit of work",
        "LeveeOwner.Run steps around the guard that keeps the app's context inside operations, for work as the database's owner. Fix: move migrations and bootstrap into a Tool project; everything else sends an operation through the Dispatcher");

    public static readonly DiagnosticDescriptor AggregatePlacement = Rule(
        "LEVEE019", "Aggregate roots live in Domain",
        "{0} is an aggregate root in a {1} project, where nothing guards its state. Fix: move it to a Domain project, where LEVEE007 holds it to changing only through its own methods");

    public static readonly DiagnosticDescriptor CatalogByHand = Rule(
        "LEVEE020", "The catalog is the generator's",
        "{0} builds part of the catalog by hand, where no rule checks the flags it claims (Platform, Internal, Effects) and the dispatcher trusts them. Fix: declare the operation with [Operation(...)] and let the generated AddLeveeOperations (or AddLeveeTestOperations) register it");

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

    /// <summary>Every rule's id, for LEVEE006's check of what the build configuration weakens.</summary>
    public static readonly string[] Ids =
    [
        "LEVEE001", "LEVEE002", "LEVEE003", "LEVEE004", "LEVEE005", "LEVEE006", "LEVEE007", "LEVEE008", "LEVEE009",
        "LEVEE010", "LEVEE011", "LEVEE012", "LEVEE013", "LEVEE014", "LEVEE015", "LEVEE016", "LEVEE017", "LEVEE018", "LEVEE019", "LEVEE020",
        "LEVEE101", "LEVEE102", "LEVEE103", "LEVEE104", "LEVEE105", "LEVEE106", "LEVEE107", "LEVEE108", "LEVEE109", "LEVEE110", "LEVEE111",
    ];
}
