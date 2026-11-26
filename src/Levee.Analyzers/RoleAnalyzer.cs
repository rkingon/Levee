using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Levee.Analyzers;

/// <summary>
/// LEVEE001: a project that uses Levee declares its role. LEVEE002: it references only the roles its own may. LEVEE008: a
/// Contracts, Domain or Application project references nothing beyond the BCL, Microsoft.Extensions abstractions, Levee
/// and what <c>LeveeAllowedReferences</c> names. LEVEE009: nothing it references uses Levee without Levee's rules.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RoleAnalyzer : DiagnosticAnalyzer
{
    private const string AllowedProperty = "build_property.LeveeAllowedReferences";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.NoRole, Descriptors.RoleReference, Descriptors.PackageReference, Descriptors.Unanalyzed, Descriptors.ToolProcess);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext context)
    {
        if (!Roles.UsesLevee(context.Compilation))
        {
            return;
        }
        var global = context.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
        var declared = Roles.Declared(global);
        var role = Roles.Parse(declared);
        if (role == Role.None)
        {
            var why = declared is null ? "declares no role." : $"its role '{declared}' isn't one.";
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NoRole, Location.None, Roles.Names, why));
            return;
        }

        if (role == Role.Tool && context.Compilation.Options.OutputKind is OutputKind.DynamicallyLinkedLibrary or OutputKind.NetModule)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ToolProcess, Location.None));
        }

        var allowed = global.TryGetValue(AllowedProperty, out var list)
            ? list.Split([';', ','], StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).Where(e => e.Length > 0).ToArray()
            : [];
        var inner = Roles.IsInner(role);
        if (inner)
        {
            foreach (var entry in allowed.Where(Framework))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.PackageReference, Location.None, role, entry,
                    $"<LeveeAllowedReferences> admits only pure libraries, and {entry} is framework. Fix: remove {entry} from <LeveeAllowedReferences> (what an allowed abstractions package brings with it is already allowed); take settings as a plain record Composition binds, and declare a port in Application for anything {entry} does, implemented in an Adapter"));
            }
        }
        foreach (var assembly in context.Compilation.SourceModule.ReferencedAssemblySymbols)
        {
            var theirs = Roles.Of(assembly);
            if (theirs != Role.None)
            {
                if (!Roles.MayReference(role, theirs))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.RoleReference, Location.None, role, assembly.Name, theirs, Roles.MayReferenceOf(role)));
                }
                continue;
            }
            if (!IsLevee(assembly.Name) && assembly.Modules.SelectMany(m => m.ReferencedAssemblies).Any(a => a.Name == "Levee"))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.Unanalyzed, Location.None, assembly.Name));
            }
            if (inner && assembly.Name.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.PackageReference, Location.None, role, assembly.Name,
                    "hosting is composition's. Fix: move the hosted service or loop into Composition or an Adapter, and have it send operations through the Dispatcher"));
            }
            else if (inner && !IsBaseLibrary(assembly.Name) && !allowed.Any(a => Matches(a, assembly.Name)))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.PackageReference, Location.None, role, assembly.Name,
                    $"it holds only the BCL, Microsoft.Extensions abstractions and Levee. Fix: declare a port in Application for what {assembly.Name} does, and use {assembly.Name} in the Adapter that implements it. Only a pure library of values and functions (no I/O, caching, clients or framework, like NodaTime) belongs in <LeveeAllowedReferences>"));
            }
        }
    }

    private static readonly string[] Frameworks =
        ["Microsoft.Extensions.", "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Microsoft.Data.", "Npgsql", "Levee."];

    /// <summary>An allowed entry that admits a framework, as a name or by a wildcard wide enough to cover one.</summary>
    private static bool Framework(string entry)
    {
        var wildcard = entry.EndsWith("*", StringComparison.Ordinal);
        var prefix = wildcard ? entry.Substring(0, entry.Length - 1) : entry;
        return Frameworks.Any(f => prefix.StartsWith(f, StringComparison.Ordinal) || (wildcard && f.StartsWith(prefix, StringComparison.Ordinal)));
    }

    private static bool IsLevee(string name) => name == "Levee" || name.StartsWith("Levee.", StringComparison.Ordinal);

    /// <summary>The BCL, the abstractions the BCL's own packages use, and Levee's core.</summary>
    private static bool IsBaseLibrary(string name) =>
        name is "System" or "mscorlib" or "netstandard" or "WindowsBase" or "Microsoft.CSharp" or "Levee" or "Microsoft.Extensions.Primitives"
            or "Microsoft.Extensions.ObjectPool"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.VisualBasic", StringComparison.Ordinal)
        || (name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && name.EndsWith(".Abstractions", StringComparison.Ordinal));

    private static bool Matches(string pattern, string name) =>
        pattern.EndsWith("*", StringComparison.Ordinal)
            ? name.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal)
            : name == pattern;
}
