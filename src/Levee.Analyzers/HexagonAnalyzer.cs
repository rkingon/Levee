using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Levee.Analyzers;

/// <summary>
/// Ports and adapters by construction. LEVEE010: a port is declared in Application. LEVEE011: a port is implemented only in
/// an Adapter project, and an adapter implements nothing of Application's but ports. LEVEE012: Contracts, Domain and
/// Application do no I/O (network, files, processes, databases, the console, the environment): that's an adapter's, behind a
/// port. LEVEE013: a Host names nothing of Application's or Domain's: it reaches them through the dispatcher. LEVEE014–015:
/// Composition exposes only registration and never calls into the application. LEVEE016: Application implements nothing
/// of Kernel's or Contracts', which would hand it to whoever holds that interface.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HexagonAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> IoNamespaces = ImmutableHashSet.Create(
        "System.Net.Http", "System.Net.Http.Json", "System.Net.Sockets", "System.Net.WebSockets",
        "System.Net.NetworkInformation", "System.Net.Security", "System.Net.Quic", "System.Data.Common", "System.Data.Odbc",
        "System.Data.OleDb", "System.Data.SqlClient", "System.IO.Pipes", "System.IO.Ports", "System.IO.MemoryMappedFiles",
        "System.IO.IsolatedStorage", "Microsoft.Win32");

    private static readonly ImmutableHashSet<string> IoTypes = ImmutableHashSet.Create(
        "System.Net.WebClient", "System.Net.WebRequest", "System.Net.HttpWebRequest", "System.Net.FtpWebRequest", "System.Net.Dns",
        "System.Net.HttpListener", "System.Net.Mail.SmtpClient", "System.Data.IDbConnection", "System.Data.IDbCommand",
        "System.Data.IDbTransaction", "System.IO.File", "System.IO.Directory", "System.IO.FileInfo", "System.IO.DirectoryInfo",
        "System.IO.FileStream", "System.IO.DriveInfo", "System.IO.FileSystemWatcher", "System.IO.RandomAccess", "System.Console",
        "System.Diagnostics.Process", "System.Diagnostics.ProcessStartInfo");

    private static readonly ImmutableHashSet<string> EnvironmentIo = ImmutableHashSet.Create(
        "GetEnvironmentVariable", "GetEnvironmentVariables", "SetEnvironmentVariable", "Exit", "FailFast", "CurrentDirectory", "GetCommandLineArgs",
        "ExpandEnvironmentVariables", "GetFolderPath", "MachineName", "UserName", "UserDomainName");

    // Values a contract may carry, though their namespace is the network's.
    private static readonly ImmutableHashSet<string> IoValues = ImmutableHashSet.Create("System.Net.Http.HttpMethod", "System.Net.Http.HttpVersionPolicy");

    /// <summary>Members that touch the file system when given a path, on types that are pure otherwise.</summary>
    private static readonly ImmutableHashSet<string> PathIo = ImmutableHashSet.Create(
        "System.IO.StreamReader", "System.IO.StreamWriter", "System.Xml.Linq.XDocument", "System.Xml.Linq.XElement", "System.Xml.XmlReader",
        "System.Xml.XmlWriter", "System.Xml.XmlDocument");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.PortPlacement, Descriptors.AdapterPlacement, Descriptors.CoreIo, Descriptors.HostReach, Descriptors.CompositionSurface,
            Descriptors.CompositionDrives, Descriptors.ApplicationExposed);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var role = Roles.Of(start.Options);
            if (role is Role.None or Role.Test || !Roles.UsesLevee(start.Compilation))
            {
                return;
            }
            var roles = new ConcurrentDictionary<IAssemblySymbol, Role>(SymbolEqualityComparer.Default);
            Role RoleOf(IAssemblySymbol assembly) =>
                SymbolEqualityComparer.Default.Equals(assembly, start.Compilation.Assembly) ? role : roles.GetOrAdd(assembly, Roles.Of);

            start.RegisterSymbolAction(c =>
            {
                if (!c.Symbol.Locations.All(l => Known.IsLeveeGenerated(l.SourceTree)))
                {
                    OnType(c, role, RoleOf);
                }
            }, SymbolKind.NamedType);
            if (Roles.IsInner(role) || Roles.Drives(role))
            {
                start.RegisterSyntaxNodeAction(c =>
                {
                    if (!Known.IsLeveeGenerated(c.Node.SyntaxTree))
                    {
                        OnName(c, role, RoleOf);
                    }
                }, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
            }
            if (Roles.IsInner(role))
            {
                start.RegisterOperationAction(c => Known.Unless(c, () => OnPathIo(c, role)), OperationKind.Invocation, OperationKind.ObjectCreation);
            }
            if (Roles.Drives(role))
            {
                // What a Host holds, however it got it (var, a method's result): not only what it names.
                start.RegisterOperationAction(c => Known.Unless(c, () => OnHostValue(c, RoleOf)), OperationKind.Invocation, OperationKind.PropertyReference,
                    OperationKind.FieldReference, OperationKind.MethodReference);
            }
            if (role == Role.Composition)
            {
                // A method group is a call deferred: f = handler.RunAsync; f() reaches the application all the same.
                start.RegisterOperationAction(c => Known.Unless(c, () => OnCompositionCall(c, RoleOf)), OperationKind.Invocation, OperationKind.MethodReference);
            }
        });
    }

    private static void OnType(SymbolAnalysisContext context, Role role, Func<IAssemblySymbol, Role> roleOf)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var location = type.Locations.FirstOrDefault() ?? Location.None;
        if (Known.IsPort(type) && role != Role.Application)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.PortPlacement, location, type.Name, role));
        }
        if (role == Role.Composition && IsPublic(type))
        {
            var members = type.GetMembers().Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsImplicitlyDeclared).ToList();
            var exposed = !type.IsStatic
                ? type.Name
                : members.FirstOrDefault(m => m is not IMethodSymbol { IsExtensionMethod: true } and not INamedTypeSymbol) is { } member
                    ? $"{type.Name}.{member.Name}"
                    // An extension method that hands out the application's (or an adapter's) types is a way in all the same.
                    : members.OfType<IMethodSymbol>().FirstOrDefault(m => Signature(m).Any(t => Inner(t, roleOf))) is { } leak
                        ? $"{type.Name}.{leak.Name}, which takes or returns the application's types,"
                        : null;
            if (exposed is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.CompositionSurface, location, exposed));
            }
        }
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return;
        }
        // An operation (or a subscription) is a message: a shared marker on it (a tenant id) is data, not a way in.
        if (role is Role.Application or Role.Domain && !type.AllInterfaces.Any(i => i.ToDisplayString() == Known.Operation)
            && type.AllInterfaces.FirstOrDefault(i => roleOf(i.ContainingAssembly) is Role.Kernel or Role.Contracts) is { } shared)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ApplicationExposed, location, type.Name, shared.Name, roleOf(shared.ContainingAssembly)));
        }
        // A port reached through an interface of the app's own that extends it is a port all the same.
        static bool PortLike(INamedTypeSymbol i) => Known.IsPort(i) || i.AllInterfaces.Any(Known.IsPort);
        foreach (var contract in type.Interfaces)
        {
            if (PortLike(contract) && role != Role.Adapter)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AdapterPlacement, location, type.Name,
                    $"implements port {contract.Name} in a {role} project. Fix: move it to an Adapter project and mark it [Adapter]"));
            }
            else if (role == Role.Adapter && !PortLike(contract)
                && roleOf(contract.ContainingAssembly) is Role.Application or Role.Domain or Role.Kernel or Role.Contracts)
            {
                // A shared interface an adapter implements is a way to drive it (a migrator, a store) around the dispatcher.
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AdapterPlacement, location, type.Name,
                    $"implements {contract.Name}, which isn't a port. Fix: if the application needs {contract.Name} from outside, declare it a [Port] in Application; for work run as the database's owner (migrations, bootstrap), call the adapter from a Tool project"));
            }
        }
    }

    private static void OnName(SyntaxNodeAnalysisContext context, Role role, Func<IAssemblySymbol, Role> roleOf)
    {
        // A doc comment's cref names a type without using it.
        if (context.Node is IdentifierNameSyntax { IsVar: true } || context.Node.IsPartOfStructuredTrivia())
        {
            return;
        }
        var symbol = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol;
        if (symbol is null)
        {
            return;
        }
        if (Roles.Drives(role))
        {
            // Naming an adapter is how a Host without a Composition project registers it; calling it is what's refused.
            if (symbol is INamedTypeSymbol type && roleOf(type.ContainingAssembly) is Role.Application or Role.Domain)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.HostReach, context.Node.GetLocation(), type.Name, roleOf(type.ContainingAssembly)));
            }
            return;
        }
        if (Io(symbol) is { } io)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.CoreIo, context.Node.GetLocation(), io, role));
        }
    }

    private static void OnHostValue(OperationAnalysisContext context, Func<IAssemblySymbol, Role> roleOf)
    {
        var (type, member) = context.Operation switch
        {
            IInvocationOperation i => (i.Type, (ISymbol)i.TargetMethod),
            IPropertyReferenceOperation p => (p.Type, p.Property),
            IFieldReferenceOperation f => (f.Type, f.Field),
            IMethodReferenceOperation m => (null, m.Method),
            _ => (null, null!),
        };
        var reached = new[] { type, member?.ContainingType }.OfType<ITypeSymbol>().SelectMany(Expand).FirstOrDefault(t => Reached(t, roleOf) is not null);
        if (reached is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.HostReach, context.Operation.Syntax.GetLocation(), reached.Name, Reached(reached, roleOf)));
        }
    }

    private static void OnCompositionCall(OperationAnalysisContext context, Func<IAssemblySymbol, Role> roleOf)
    {
        var method = context.Operation switch
        {
            IInvocationOperation i => i.TargetMethod,
            IMethodReferenceOperation m => m.Method,
            _ => null,
        };
        if (method is null)
        {
            return;
        }
        if (method.MethodKind == MethodKind.Ordinary && roleOf(method.ContainingType.ContainingAssembly) is Role.Application or Role.Domain or Role.Adapter)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.CompositionDrives, context.Operation.Syntax.GetLocation(), $"{method.ContainingType.Name}.{method.Name}"));
        }
    }

    private static Role? Reached(ITypeSymbol type, Func<IAssemblySymbol, Role> roleOf)
    {
        var role = type.ContainingAssembly is { } assembly ? roleOf(assembly) : Role.None;
        return role is Role.Application or Role.Domain or Role.Adapter ? role : null;
    }

    private static bool Inner(ITypeSymbol type, Func<IAssemblySymbol, Role> roleOf) =>
        Expand(type).Any(t => t.ContainingAssembly is { } a && roleOf(a) is Role.Application or Role.Domain or Role.Adapter);

    private static IEnumerable<ITypeSymbol> Signature(IMethodSymbol method) => method.Parameters.Select(p => p.Type).Prepend(method.ReturnType);

    /// <summary>A type and every type it's made of: generic arguments, array elements.</summary>
    private static IEnumerable<ITypeSymbol> Expand(ITypeSymbol type) => type switch
    {
        INamedTypeSymbol named => new[] { type }.Concat(named.TypeArguments.SelectMany(Expand)),
        IArrayTypeSymbol array => new[] { type }.Concat(Expand(array.ElementType)),
        _ => [type],
    };

    private static bool IsPublic(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsException(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.ToDisplayString() == "System.Exception")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The I/O a name reaches: a type that does it, or one of Environment's process-level members.</summary>
    private static string? Io(ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol type)
        {
            // Naming an exception (to classify a port's failure) or an enum (to read a value) does no I/O.
            if (type.TypeKind == TypeKind.Enum || IsException(type))
            {
                return null;
            }
            return IsIoType(type) ? type.Name : null;
        }
        if (symbol.ContainingType?.ToDisplayString() == "System.Environment")
        {
            return EnvironmentIo.Contains(symbol.Name) ? $"Environment.{symbol.Name}" : null;
        }
        // A member named without its type (using static System.IO.File; ReadAllText(...)) is its type's I/O.
        return symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol && symbol.ContainingType is { } owner
            && owner.TypeKind != TypeKind.Enum && !IsException(owner) && IsIoType(owner)
            ? $"{owner.Name}.{symbol.Name}"
            : null;
    }

    private static bool IsIoType(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        var name = definition.ToDisplayString();
        var ns = definition.ContainingNamespace?.ToDisplayString() ?? "";
        return !IoValues.Contains(name) && (IoNamespaces.Contains(ns) || IoTypes.Contains(name));
    }

    /// <summary>A path handed to a reader, writer or document, or a temporary file asked for: the file system, by another door.</summary>
    private static void OnPathIo(OperationAnalysisContext context, Role role)
    {
        var (owner, member, arguments) = context.Operation switch
        {
            IInvocationOperation i => (i.TargetMethod.ContainingType, i.TargetMethod.Name, i.Arguments),
            IObjectCreationOperation o => (o.Type as INamedTypeSymbol, "new", o.Arguments),
            _ => (null, "", ImmutableArray<IArgumentOperation>.Empty),
        };
        var name = owner?.OriginalDefinition.ToDisplayString();
        var io = name == "System.IO.Path" && member is "GetTempFileName" or "GetTempPath"
            || (name is not null && PathIo.Contains(name) && member is "new" or "Load" or "Save" or "Create"
                && arguments.FirstOrDefault()?.Parameter?.Type.SpecialType == SpecialType.System_String);
        if (io)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.CoreIo, context.Operation.Syntax.GetLocation(), $"{owner!.Name}.{member}", role));
        }
    }
}
