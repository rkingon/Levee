using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Levee.Analyzers;

/// <summary>
/// LEVEE110: a handler fails only with codes its operation declares (or Levee's own), so the catalog is every client's
/// complete list. Only literal and constant codes are seen; the dispatcher logs any other undeclared one at run time.
/// LEVEE111: an access policy or admission refuses only with the codes its [Refuses] declares, listed once in the catalog.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ErrorAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> Factories = ImmutableHashSet.Create("Validation", "NotFound", "Conflict", "Forbidden", "Unavailable");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.UndeclaredError, Descriptors.UndeclaredRefusal);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var compilation = start.Compilation;
            if (compilation.GetTypeByMetadataName("Levee.Error") is not { } error
                || compilation.GetTypeByMetadataName("Levee.IOperationHandler`2") is not { } handler
                || compilation.GetTypeByMetadataName("Levee.LeveeErrors") is not { } levee)
            {
                return;
            }
            var own = levee.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst).Select(f => f.ConstantValue as string)
                .Where(c => c is not null).ToImmutableHashSet(StringComparer.Ordinal);
            start.RegisterOperationAction(c => OnInvocation(c, error, handler, own), OperationKind.Invocation);
            start.RegisterOperationAction(c => OnCreation(c, error, handler, own), OperationKind.ObjectCreation);
        });
    }

    private static void OnInvocation(OperationAnalysisContext context, INamedTypeSymbol error, INamedTypeSymbol handler, ImmutableHashSet<string?> own)
    {
        var call = (IInvocationOperation)context.Operation;
        if (SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, error) && Factories.Contains(call.TargetMethod.Name)
            && call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "code") is { } code)
        {
            Check(context, code.Value, handler, own);
        }
    }

    private static void OnCreation(OperationAnalysisContext context, INamedTypeSymbol error, INamedTypeSymbol handler, ImmutableHashSet<string?> own)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (SymbolEqualityComparer.Default.Equals(creation.Type, error) && creation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "Code") is { } code)
        {
            Check(context, code.Value, handler, own);
        }
    }

    private static void Check(OperationAnalysisContext context, IOperation code, INamedTypeSymbol handler, ImmutableHashSet<string?> own)
    {
        if (code.ConstantValue is not { HasValue: true, Value: string value } || own.Contains(value))
        {
            return;
        }
        var owner = context.ContainingSymbol.ContainingType;
        if (owner is not null && owner.AllInterfaces.Any(i => i.ToDisplayString() is "Levee.IAccessPolicy" or "Levee.IAdmission"))
        {
            var refuses = owner.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "Levee.RefusesAttribute")?
                .ConstructorArguments.SelectMany(a => a.Kind == TypedConstantKind.Array ? a.Values : [a]).Select(v => v.Value as string) ?? [];
            if (!refuses.Contains(value))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.UndeclaredRefusal, code.Syntax.GetLocation(), owner.Name, value));
            }
            return;
        }
        var operations = context.ContainingSymbol.ContainingType?.AllInterfaces
            .Where(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, handler))
            .Select(i => i.TypeArguments[0])
            .ToList() ?? [];
        if (operations.Count == 0 || operations.Any(op => Declared(op).Contains(value)))
        {
            return;
        }
        var names = string.Join(", ", operations.Select(o => o.Name));
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.UndeclaredError, code.Syntax.GetLocation(), names, value));
    }

    private static IEnumerable<string> Declared(ITypeSymbol operation) =>
        operation.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == Known.OperationAttribute)?
            .NamedArguments.FirstOrDefault(a => a.Key == "Errors").Value is { Kind: TypedConstantKind.Array } declared
            ? declared.Values.Select(v => v.Value as string ?? "")
            : [];
}
