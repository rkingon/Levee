using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Levee.Analyzers;

/// <summary>LEVEE007: in a Domain project, an aggregate root's state is settable only from inside it (private setters, init, readonly fields).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AggregateAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.AggregateState, Descriptors.AggregatePlacement);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var role = Roles.Of(start.Options);
            if (role is Role.None or Role.Test || start.Compilation.GetTypeByMetadataName(Known.AggregateRoot) is not { } root)
            {
                return;
            }
            start.RegisterSymbolAction(c => Analyze(c, root, role), SymbolKind.NamedType);
        });
    }

    /// <summary>The root and every class it derives from: a public setter on a base class is a way in all the same.</summary>
    private static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol root, Role role)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || !type.AllInterfaces.Contains(root, SymbolEqualityComparer.Default)
            || type.Locations.All(l => Known.IsLeveeGenerated(l.SourceTree)))
        {
            return;
        }
        if (role != Role.Domain)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.AggregatePlacement, type.Locations.FirstOrDefault(), type.Name, role));
            return;
        }
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var member in t.GetMembers().Where(m => m is IPropertySymbol or IFieldSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary }
                && !m.IsImplicitlyDeclared && !m.IsStatic))
            {
                var open = member switch
                {
                    // A method that hands out a mutable collection hands out the state inside it.
                    IMethodSymbol method => method.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal && IsMutableCollection(method.ReturnType),
                    IPropertySymbol { SetMethod: { IsInitOnly: false } set } when set.DeclaredAccessibility is not (Accessibility.Private or Accessibility.Protected
                        or Accessibility.ProtectedAndInternal) => true,
                    IPropertySymbol { GetMethod: { } get } property => get.DeclaredAccessibility != Accessibility.Private && IsMutableCollection(property.Type),
                    IFieldSymbol { IsConst: false } field when field.DeclaredAccessibility != Accessibility.Private => !field.IsReadOnly || IsMutableCollection(field.Type),
                    _ => false,
                };
                if (open)
                {
                    var location = SymbolEqualityComparer.Default.Equals(t, type) ? member.Locations.FirstOrDefault() : type.Locations.FirstOrDefault();
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.AggregateState, location, member.Name, type.Name));
                }
            }
        }
    }

    private static readonly ImmutableHashSet<string> MutableCollections = ImmutableHashSet.Create(
        "System.Collections.Generic.List<T>", "System.Collections.Generic.Dictionary<TKey, TValue>", "System.Collections.Generic.HashSet<T>",
        "System.Collections.Generic.SortedSet<T>", "System.Collections.Generic.SortedDictionary<TKey, TValue>",
        "System.Collections.Generic.SortedList<TKey, TValue>", "System.Collections.Generic.Queue<T>", "System.Collections.Generic.Stack<T>",
        "System.Collections.Generic.LinkedList<T>", "System.Collections.Generic.ICollection<T>", "System.Collections.Generic.IList<T>",
        "System.Collections.Generic.ISet<T>", "System.Collections.Generic.IDictionary<TKey, TValue>",
        "System.Collections.ObjectModel.Collection<T>", "System.Collections.ObjectModel.ObservableCollection<T>");

    /// <summary>An array, a known mutable collection, or anything deriving one or implementing a writable collection interface.</summary>
    private static bool IsMutableCollection(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol)
        {
            return true;
        }
        if (type is not INamedTypeSymbol named || named.ContainingNamespace?.ToDisplayString() == "System.Collections.Immutable")
        {
            return false;
        }
        for (var t = named; t is not null; t = t.BaseType)
        {
            if (MutableCollections.Contains(t.OriginalDefinition.ToDisplayString()))
            {
                return true;
            }
        }
        return named.TypeKind != TypeKind.Interface && !named.OriginalDefinition.ToDisplayString().StartsWith("System.Collections.ObjectModel.ReadOnly", StringComparison.Ordinal)
            && named.AllInterfaces.Any(i => MutableCollections.Contains(i.OriginalDefinition.ToDisplayString()));
    }
}
