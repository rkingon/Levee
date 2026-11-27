using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Levee.Analyzers;

/// <summary>
/// LEVEE113: an access policy reads the key of the scope it authorizes. An input-scoped operation runs in the scope its
/// input names, so a policy that grants by the access word alone, or asks only whether the scope is the platform's, lets a
/// caller into every tenant.
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
                || start.Compilation.GetTypeByMetadataName("Levee.WorkScope") is not { } scope)
            {
                return;
            }
            start.RegisterSymbolStartAction(symbol => OnType(symbol, policy, scope), SymbolKind.NamedType);
        });
    }

    private static void OnType(SymbolStartAnalysisContext context, INamedTypeSymbol policy, INamedTypeSymbol scope)
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
        var deniesAll = 0;
        var implementation = type.FindImplementationForInterfaceMember(authorize);
        context.RegisterOperationBlockAction(b =>
        {
            if (SymbolEqualityComparer.Default.Equals(b.OwningSymbol, implementation) && b.OperationBlocks.All(block => Returns(block).All(Refuses)))
            {
                Interlocked.Exchange(ref deniesAll, 1);
            }
        });
        context.RegisterOperationAction(c =>
        {
            if (ReadsKey(c.Operation, scope))
            {
                Interlocked.Exchange(ref read, 1);
            }
        }, OperationKind.PropertyReference, OperationKind.DeconstructionAssignment, OperationKind.RecursivePattern);
        context.RegisterSymbolEndAction(end =>
        {
            if (Volatile.Read(ref read) == 0 && Volatile.Read(ref deniesAll) == 0 && type.Locations.FirstOrDefault(l => l.IsInSource) is { } location && !Known.IsLeveeGenerated(location.SourceTree))
            {
                end.ReportDiagnostic(Diagnostic.Create(Descriptors.ScopeUnread, location, type.Name));
            }
        });
    }

    /// <summary>
    /// The scope's Key, in a member access or a property pattern, or bound by a deconstruction. Platform alone is only
    /// whether there's a key: a policy that reads nothing else grants every tenant alike.
    /// </summary>
    private static bool ReadsKey(IOperation operation, INamedTypeSymbol scope) => operation switch
    {
        IPropertyReferenceOperation { Property.Name: "Key" } p => SymbolEqualityComparer.Default.Equals(p.Property.ContainingType, scope)
            && p.Parent is not IPropertySubpatternOperation { Pattern: IDiscardPatternOperation },
        IDeconstructionAssignmentOperation d => SymbolEqualityComparer.Default.Equals(d.Value.Type, scope)
            && (d.Target is IDeclarationExpressionOperation { Expression: ITupleOperation declared } ? declared : d.Target as ITupleOperation) is { } tuple
            && tuple.Elements.Length > 0 && tuple.Elements[0] is not IDiscardOperation,
        IRecursivePatternOperation { DeconstructSymbol: IMethodSymbol deconstruct } r => SymbolEqualityComparer.Default.Equals(deconstruct.ContainingType, scope)
            && r.DeconstructionSubpatterns.Length > 0 && r.DeconstructionSubpatterns[0] is not IDiscardPatternOperation,
        _ => false,
    };

    /// <summary>What the method returns, not what a lambda or local function in it does.</summary>
    private static IEnumerable<IOperation> Returns(IOperation operation)
    {
        foreach (var child in operation.ChildOperations)
        {
            if (child is IReturnOperation { ReturnedValue: { } value })
            {
                yield return value;
            }
            else if (child is not (IAnonymousFunctionOperation or ILocalFunctionOperation))
            {
                foreach (var nested in Returns(child))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// A value that's never a grant: one of Error's factories, a static readonly Error, a throw, or only those through a conditional, a switch or
    /// a completed task. A policy that returns nothing else grants no scope, so it needn't read one.
    /// </summary>
    private static bool Refuses(IOperation value)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }
        return value switch
        {
            IThrowOperation => true,
            // A refusal made once and returned every time: an Error held where nothing can swap it for null.
            IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true, NullableAnnotation: NullableAnnotation.NotAnnotated,
                Type: { Name: "Error", ContainingNamespace: { Name: "Levee", ContainingNamespace.IsGlobalNamespace: true } } } } => true,
            IInvocationOperation { TargetMethod: { IsStatic: true, ContainingType: { Name: "Error", ContainingNamespace: { Name: "Levee", ContainingNamespace.IsGlobalNamespace: true } } } } => true,
            IInvocationOperation { TargetMethod: { Name: "FromResult", ContainingType.Name: "ValueTask" or "Task" }, Arguments.Length: 1 } wrap => Refuses(wrap.Arguments[0].Value),
            IObjectCreationOperation { Type.Name: "ValueTask", Arguments.Length: 1 } wrap => Refuses(wrap.Arguments[0].Value),
            IConditionalOperation { WhenFalse: { } whenFalse } conditional => Refuses(conditional.WhenTrue) && Refuses(whenFalse),
            ISwitchExpressionOperation switched => switched.Arms.All(a => Refuses(a.Value)),
            _ => false,
        };
    }
}
