using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Levee.Analyzers;

internal enum Role
{
    None,
    Kernel,
    Contracts,
    Domain,
    Application,
    Adapter,
    Infrastructure,
    Composition,
    Host,
    Client,
    Projection,
    Tool,
    Test,
}

internal static class Roles
{
    public const string Property = "build_property.LeveeRole";

    /// <summary>What each role may reference among roled assemblies. Test may reference anything; nothing else references Test.</summary>
    private static readonly ImmutableDictionary<Role, ImmutableHashSet<Role>> Allowed = new Dictionary<Role, ImmutableHashSet<Role>>
    {
        // The shared kernel: what every layer may share (typed ids, value types), depending on nothing of the app's.
        [Role.Kernel] = ImmutableHashSet.Create(Role.Kernel),
        [Role.Contracts] = ImmutableHashSet.Create(Role.Kernel, Role.Contracts),
        [Role.Domain] = ImmutableHashSet.Create(Role.Kernel, Role.Domain),
        [Role.Application] = ImmutableHashSet.Create(Role.Kernel, Role.Contracts, Role.Domain, Role.Application),
        // Adapters never depend on each other: what several share is Infrastructure, and only composition wires them.
        [Role.Adapter] = ImmutableHashSet.Create(Role.Kernel, Role.Contracts, Role.Domain, Role.Application, Role.Infrastructure),
        // Plumbing adapters share (a vendor transport): it may do I/O, and sees nothing of the application's.
        [Role.Infrastructure] = ImmutableHashSet.Create(Role.Kernel, Role.Infrastructure),
        // The composition root hosts share: the only place that wires the application, its adapters and its settings.
        [Role.Composition] = ImmutableHashSet.Create(Role.Kernel, Role.Contracts, Role.Domain, Role.Application, Role.Adapter, Role.Infrastructure, Role.Client,
            Role.Projection),
        [Role.Host] = ImmutableHashSet.Create(Role.Kernel, Role.Contracts, Role.Domain, Role.Application, Role.Adapter, Role.Infrastructure, Role.Composition,
            Role.Client, Role.Projection),
        [Role.Client] = ImmutableHashSet.Create(Role.Kernel, Role.Contracts, Role.Client),
        // An app's own public API: it sees the contract and reaches the application only through the dispatcher.
        [Role.Projection] = ImmutableHashSet.Create(Role.Kernel, Role.Contracts, Role.Projection),
        // A process run as the database's owner (migrations, role bootstrap): it sees what a Host does, nothing sees it.
        [Role.Tool] = ImmutableHashSet.Create(Role.Kernel, Role.Contracts, Role.Domain, Role.Application, Role.Adapter, Role.Infrastructure, Role.Composition),
        [Role.Test] = ImmutableHashSet.Create(Role.Kernel, Role.Contracts, Role.Domain, Role.Application, Role.Adapter, Role.Infrastructure, Role.Composition,
            Role.Host, Role.Client, Role.Projection, Role.Tool, Role.Test),
    }.ToImmutableDictionary();

    /// <summary>The roles that are the app's core: pure, no I/O, nothing past the BCL and abstractions.</summary>
    public static bool IsInner(Role role) => role is Role.Kernel or Role.Contracts or Role.Domain or Role.Application;

    /// <summary>The roles that compose: they alone may take the system clock and locate services.</summary>
    public static bool Composes(Role role) => role is Role.Composition or Role.Host or Role.Tool;

    /// <summary>The processes: they drive the application only through the dispatcher (LEVEE013).</summary>
    public static bool Drives(Role role) => role is Role.Host or Role.Tool;

    public static string? Declared(AnalyzerConfigOptions options) =>
        options.TryGetValue(Property, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public static Role Parse(string? value) =>
        value is not null && Enum.TryParse<Role>(value, ignoreCase: false, out var role) && role != Role.None ? role : Role.None;

    public static Role Of(AnalyzerOptions options) => Parse(Declared(options.AnalyzerConfigOptionsProvider.GlobalOptions));

    public static bool MayReference(Role from, Role to) => Allowed.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static string MayReferenceOf(Role from) =>
        Allowed.TryGetValue(from, out var allowed) ? string.Join(", ", Allowed.Keys.Where(allowed.Contains)) : "";

    public static string Names => string.Join(", ", Allowed.Keys.Select(r => r.ToString()));

    /// <summary>The role an assembly declared, from its generated <c>[assembly: LeveeRole]</c>; None for anything not built with Levee.</summary>
    public static Role Of(IAssemblySymbol assembly) =>
        assembly.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == Known.RoleAttribute && a.ConstructorArguments.Length == 1)
            .Select(a => Parse(a.ConstructorArguments[0].Value as string))
            .FirstOrDefault();

    public static bool UsesLevee(Compilation compilation) => compilation.GetTypeByMetadataName(Known.Operation) is not null;
}

internal static class Known
{
    /// <summary>Levee's own generated output, by where the generator puts it: everything else generated is held to the rules.</summary>
    public static bool IsLeveeGenerated(SyntaxTree? tree) =>
        tree?.FilePath.Replace('\\', '/').Contains("Levee.Analyzers.OperationGenerator/") == true;

    // .NET's own source generators, by the assembly whose name heads a generated file's path. Their output silences
    // warnings wholesale (the regex generator's is a bare #pragma warning disable) and holds none of the app's code.
    private static readonly string[] FrameworkGenerators =
    [
        "System.Text.RegularExpressions.Generator", "System.Text.Json.SourceGeneration", "Microsoft.Extensions.Logging.Generators",
        "Microsoft.Extensions.Options.SourceGeneration", "Microsoft.Extensions.Configuration.Binder.SourceGeneration",
        "Microsoft.Interop.LibraryImportGenerator", "Microsoft.Interop.ComInterfaceGenerator", "Microsoft.AspNetCore.Http.RequestDelegateGenerator",
    ];

    /// <summary>Output of one of .NET's own source generators.</summary>
    public static bool IsFrameworkGenerated(SyntaxTree? tree)
    {
        var path = tree?.FilePath.Replace('\\', '/');
        return path is not null && FrameworkGenerators.Any(g => path.StartsWith(g + "/", StringComparison.Ordinal) || path.Contains("/" + g + "/"));
    }

    /// <summary>Runs <paramref name="check"/> unless the operation is in Levee's own generated code.</summary>
    public static void Unless(Microsoft.CodeAnalysis.Diagnostics.OperationAnalysisContext context, Action check)
    {
        if (!IsLeveeGenerated(context.Operation.Syntax.SyntaxTree))
        {
            check();
        }
    }

    public const string Operation = "Levee.IOperation";
    public const string OperationAttribute = "Levee.OperationAttribute";
    public const string RoleAttribute = "Levee.LeveeRoleAttribute";
    public const string RegistrationAttribute = "Levee.LeveeRegistrationAttribute";
    public const string AdaptsAttribute = "Levee.LeveeAdaptsAttribute";
    public const string PortAttribute = "Levee.PortAttribute";
    public const string AdapterAttribute = "Levee.AdapterAttribute";
    public const string HandlesAttribute = "Levee.LeveeHandlesAttribute";
    public const string AggregateRoot = "Levee.IAggregateRoot";

    /// <summary>A port: an interface with <c>[Port]</c>, or one deriving <c>IRepository&lt;T&gt;</c>.</summary>
    public static bool IsPort(INamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Interface
        && !Is(type, "IRepository", 1)
        && (type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PortAttribute) || type.AllInterfaces.Any(i => Is(i, "IRepository", 1)));

    public static string? SelectedBy(INamedTypeSymbol port) =>
        port.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == PortAttribute)?
            .NamedArguments.FirstOrDefault(a => a.Key == "SelectedBy").Value.Value as string;

    public static string? AdapterName(INamedTypeSymbol type) =>
        type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AdapterAttribute)?
            .ConstructorArguments.FirstOrDefault().Value as string;

    public static bool IsNamedAdapter(INamedTypeSymbol type) =>
        type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == AdapterAttribute);

    /// <summary>0 scoped, 1 singleton, 2 transient, as <c>AdapterLifetime</c>.</summary>
    public static int AdapterLifetime(INamedTypeSymbol type) =>
        type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AdapterAttribute)?
            .NamedArguments.FirstOrDefault(a => a.Key == "Lifetime").Value.Value is int lifetime ? lifetime : 0;

    public static bool IsMany(INamedTypeSymbol port) =>
        port.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == PortAttribute)?
            .NamedArguments.FirstOrDefault(a => a.Key == "Many").Value.Value is true;

    public const string MetadataAttribute = "Levee.OperationMetadataAttribute";

    /// <summary>Levee's own generic interface <paramref name="name"/> of <paramref name="arity"/>, constructed or not.</summary>
    public static bool Is(INamedTypeSymbol type, string name, int arity) =>
        type.Name == name && type.Arity == arity && type.ContainingNamespace is { Name: "Levee", ContainingNamespace.IsGlobalNamespace: true };
}
