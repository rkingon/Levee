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
            if (role == Role.None || Roles.IsTest(role) || start.Compilation.GetTypeByMetadataName(Known.AggregateRoot) is not { } root)
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
            foreach (var member in Members(t).Where(Open))
            {
                var location = SymbolEqualityComparer.Default.Equals(t, type) ? member.Locations.FirstOrDefault() : type.Locations.FirstOrDefault();
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AggregateState, location, member.Name, type.Name));
            }
        }
        // A part the root hands out (a line, a value on its row) is its state too: open there, it's open from outside. A struct
        // handed out is a copy, so only what it holds by reference (a mutable collection) is shared.
        foreach (var part in Parts(type, root, context.Compilation))
        {
            var byValue = part.TypeKind == TypeKind.Struct;
            foreach (var member in Reachable(part).Where(Outside).Where(m => byValue ? Shared(m) : Open(m)))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AggregateState, member.Locations.FirstOrDefault(),
                    $"{part.Name}.{member.Name}, a part {type.Name} hands out,", type.Name));
            }
        }
    }

    /// <summary>What a caller holding <paramref name="type"/> reaches: its own members, its bases', and an interface's, its own and those it extends.</summary>
    private static IEnumerable<ISymbol> Reachable(INamedTypeSymbol type)
    {
        IEnumerable<INamedTypeSymbol> owners = type.TypeKind == TypeKind.Interface ? [type, .. type.AllInterfaces] : Bases(type);
        return owners.SelectMany(Members);
    }

    private static IEnumerable<INamedTypeSymbol> Bases(INamedTypeSymbol type)
    {
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object && t.SpecialType != SpecialType.System_ValueType; t = t.BaseType)
        {
            yield return t;
        }
    }

    /// <summary>
    /// What a caller outside the part reaches: a public or internal member (and of a property, its public or internal
    /// accessors). A protected one is the part's own and its subclasses'.
    /// </summary>
    private static bool Outside(ISymbol member) => member.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal;

    private static bool Exposed(IMethodSymbol? accessor) => accessor?.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal;

    /// <summary>A member that hands out a mutable collection: shared, even from a copy.</summary>
    private static bool Shared(ISymbol member) => member switch
    {
        IMethodSymbol method => method.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal && IsMutableCollection(method.ReturnType),
        IPropertySymbol { GetMethod: { } get } property => get.DeclaredAccessibility != Accessibility.Private && IsMutableCollection(property.Type),
        IFieldSymbol { IsConst: false } field => field.DeclaredAccessibility != Accessibility.Private && IsMutableCollection(field.Type),
        _ => false,
    };

    private static IEnumerable<ISymbol> Members(INamedTypeSymbol type) =>
        type.GetMembers().Where(m => m is IPropertySymbol or IFieldSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary } && !m.IsImplicitlyDeclared && !m.IsStatic);

    private static bool Open(ISymbol member) => member switch
    {
        // A method that hands out a mutable collection hands out the state inside it.
        IMethodSymbol method => method.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal && IsMutableCollection(method.ReturnType),
        IPropertySymbol { SetMethod: { IsInitOnly: false } set } when set.DeclaredAccessibility is not (Accessibility.Private or Accessibility.Protected
            or Accessibility.ProtectedAndInternal) => true,
        IPropertySymbol { GetMethod: { } get } property => get.DeclaredAccessibility != Accessibility.Private && IsMutableCollection(property.Type),
        IFieldSymbol { IsConst: false } field when field.DeclaredAccessibility != Accessibility.Private => !field.IsReadOnly || IsMutableCollection(field.Type),
        _ => false,
    };

    /// <summary>
    /// The types of this project a root hands out, at any depth, through what it shows (a property's type, or the items of a
    /// collection it shows, whether it declares it or inherits it): its parts, classes, structs or the interfaces a caller
    /// holds them by. Another root is an aggregate of its own; a type of another project, its own to guard.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> Parts(INamedTypeSymbol type, INamedTypeSymbol root, Compilation compilation)
    {
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { type };
        var pending = new Queue<INamedTypeSymbol>([type]);
        while (pending.Count > 0)
        {
            var holder = pending.Dequeue();
            // A property a caller can read, or a field: what they hold, a struct's copy included, may refer to a part.
            var shown = Reachable(holder).Where(Outside).Select(m => m switch
            {
                IPropertySymbol property when Exposed(property.GetMethod) => property.Type,
                IFieldSymbol field => field.Type,
                _ => null,
            }).OfType<ITypeSymbol>();
            foreach (var reached in shown)
            {
                var value = reached is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : reached;
                var held = value is INamedTypeSymbol { SpecialType: not SpecialType.System_String } named
                    && named.AllInterfaces.Concat(named.TypeKind == TypeKind.Interface ? [named] : []).FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) is { } items
                    ? items.TypeArguments[0]
                    : value;
                if (held is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct or TypeKind.Interface } part
                    && SymbolEqualityComparer.Default.Equals(part.ContainingAssembly, compilation.Assembly)
                    && !part.AllInterfaces.Contains(root, SymbolEqualityComparer.Default) && seen.Add(part))
                {
                    pending.Enqueue(part);
                    yield return part;
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
