using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Levee.Analyzers;

/// <summary>
/// LEVEE113: an access policy reads the scope it authorizes. An input-scoped operation runs in the scope its input names,
/// so a policy that grants by the access word alone lets a caller into every tenant.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PolicyAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.ScopeUnread);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var role = Roles.Of(start.Options);
            // A test's policy is a fake, there to let its operations through.
            if (role == Role.None || Roles.IsTest(role)
                || start.Compilation.GetTypeByMetadataName("Levee.IAccessPolicy") is not { } policy
                || start.Compilation.GetTypeByMetadataName("Levee.AccessRequest") is not { } request)
            {
                return;
            }
            start.RegisterSymbolStartAction(symbol => OnType(symbol, policy, request), SymbolKind.NamedType);
        });
    }

    private static void OnType(SymbolStartAnalysisContext context, INamedTypeSymbol policy, INamedTypeSymbol request)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        // Judged where AuthorizeAsync is written: a policy that inherits it is its base's.
        if (!type.AllInterfaces.Contains(policy, SymbolEqualityComparer.Default)
            || policy.GetMembers("AuthorizeAsync").FirstOrDefault() is not { } authorize
            || !SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(authorize)?.ContainingType, type))
        {
            return;
        }
        var read = 0;
        context.RegisterOperationAction(c =>
        {
            if (ReadsScope(c.Operation, request))
            {
                Interlocked.Exchange(ref read, 1);
            }
        }, OperationKind.PropertyReference, OperationKind.DeconstructionAssignment, OperationKind.RecursivePattern);
        context.RegisterSymbolEndAction(end =>
        {
            if (Volatile.Read(ref read) == 0 && type.Locations.FirstOrDefault(l => l.IsInSource) is { } location && !Known.IsLeveeGenerated(location.SourceTree))
            {
                end.ReportDiagnostic(Diagnostic.Create(Descriptors.ScopeUnread, location, type.Name));
            }
        });
    }

    /// <summary>request.Scope, in a member access or a property pattern, or the request deconstructed.</summary>
    private static bool ReadsScope(IOperation operation, INamedTypeSymbol request) => operation switch
    {
        IPropertyReferenceOperation { Property.Name: "Scope" } p => SymbolEqualityComparer.Default.Equals(p.Property.ContainingType, request),
        IDeconstructionAssignmentOperation d => SymbolEqualityComparer.Default.Equals(d.Value.Type, request),
        IRecursivePatternOperation { DeconstructSymbol: IMethodSymbol deconstruct } => SymbolEqualityComparer.Default.Equals(deconstruct.ContainingType, request),
        _ => false,
    };
}
