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
    private static readonly ImmutableHashSet<string> Factories = ImmutableHashSet.Create("Validation", "NotFound", "Conflict", "Forbidden", "Unavailable", "Of");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.UndeclaredError, Descriptors.UndeclaredRefusal);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
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
            start.RegisterOperationAction(c => Known.Unless(c, () => OnInvocation(c, error, handler, own)), OperationKind.Invocation);
            start.RegisterOperationAction(c => Known.Unless(c, () => OnCreation(c, error, handler, own)), OperationKind.ObjectCreation);
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

    /// <summary>Every code an expression can be: a constant, either side of a conditional, a switch's arms.</summary>
    private static IEnumerable<(string Code, IOperation At)> Codes(IOperation operation) => operation switch
    {
        { ConstantValue: { HasValue: true, Value: string value } } => [(value, operation)],
        IConditionalOperation conditional => Codes(conditional.WhenTrue).Concat(conditional.WhenFalse is { } no ? Codes(no) : []),
        ICoalesceOperation coalesce => Codes(coalesce.Value).Concat(Codes(coalesce.WhenNull)),
        ISwitchExpressionOperation choice => choice.Arms.SelectMany(a => Codes(a.Value)),
        IConversionOperation conversion => Codes(conversion.Operand),
        _ => [],
    };

    private static void Check(OperationAnalysisContext context, IOperation expression, INamedTypeSymbol handler, ImmutableHashSet<string?> own)
    {
        foreach (var (code, at) in Codes(expression))
        {
            CheckOne(context, code, at, handler, own);
        }
    }

    /// <summary>The type whose contract a code belongs to: the handler or policy it's written in, or the one a nested helper sits in.</summary>
    private static INamedTypeSymbol? OwnerOf(ISymbol symbol)
    {
        for (var t = symbol.ContainingType; t is not null; t = t.ContainingType)
        {
            if (t.AllInterfaces.Any(i => i.ToDisplayString() is "Levee.IAccessPolicy" or "Levee.IAdmission"
                || i.OriginalDefinition.ToDisplayString() == "Levee.IOperationHandler<TOperation, TResult>"))
            {
                return t;
            }
        }
        return symbol.ContainingType;
    }

    private static void CheckOne(OperationAnalysisContext context, string value, IOperation code, INamedTypeSymbol handler, ImmutableHashSet<string?> own)
    {
        if (own.Contains(value))
        {
            return;
        }
        var owner = OwnerOf(context.ContainingSymbol);
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
        var operations = owner?.AllInterfaces
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
