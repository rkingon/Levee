using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Levee.Analyzers;

/// <summary>LEVEE007: in a Domain project, an aggregate root's state is settable only from inside it (private setters, init, readonly fields).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AggregateAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.AggregateState);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (Roles.Of(start.Options) == Role.Domain && start.Compilation.GetTypeByMetadataName(Known.AggregateRoot) is { } root)
            {
                start.RegisterSymbolAction(c => Analyze(c, root), SymbolKind.Property, SymbolKind.Field);
            }
        });
    }

    private static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol root)
    {
        var owner = context.Symbol.ContainingType;
        if (owner is null || !owner.AllInterfaces.Contains(root, SymbolEqualityComparer.Default) || context.Symbol.IsImplicitlyDeclared)
        {
            return;
        }
        var open = context.Symbol switch
        {
            IPropertySymbol { SetMethod: { IsInitOnly: false } set } when set.DeclaredAccessibility != Accessibility.Private => true,
            IPropertySymbol { GetMethod: { } get } property => get.DeclaredAccessibility != Accessibility.Private && IsMutableCollection(property.Type),
            IFieldSymbol { IsConst: false } field when field.DeclaredAccessibility != Accessibility.Private => !field.IsReadOnly || IsMutableCollection(field.Type),
            _ => false,
        };
        if (open)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.AggregateState, context.Symbol.Locations.FirstOrDefault(), context.Symbol.Name, owner.Name));
        }
    }

    private static readonly ImmutableHashSet<string> MutableCollections = ImmutableHashSet.Create(
        "System.Collections.Generic.List<T>", "System.Collections.Generic.Dictionary<TKey, TValue>", "System.Collections.Generic.HashSet<T>",
        "System.Collections.Generic.SortedSet<T>", "System.Collections.Generic.SortedDictionary<TKey, TValue>",
        "System.Collections.Generic.SortedList<TKey, TValue>", "System.Collections.Generic.Queue<T>", "System.Collections.Generic.Stack<T>",
        "System.Collections.Generic.LinkedList<T>", "System.Collections.Generic.ICollection<T>", "System.Collections.Generic.IList<T>",
        "System.Collections.Generic.ISet<T>", "System.Collections.Generic.IDictionary<TKey, TValue>",
        "System.Collections.ObjectModel.Collection<T>", "System.Collections.ObjectModel.ObservableCollection<T>");

    private static bool IsMutableCollection(ITypeSymbol type) =>
        type is IArrayTypeSymbol || (type is INamedTypeSymbol named && MutableCollections.Contains(named.OriginalDefinition.ToDisplayString()));
}
