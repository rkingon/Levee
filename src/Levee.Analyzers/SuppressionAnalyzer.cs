using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Levee.Analyzers;

/// <summary>
/// LEVEE006: a Levee rule is silenced only beside the code, with a reason: <c>[SuppressMessage]</c> with a Justification,
/// or <c>#pragma warning disable LEVEE…</c> with a <c>//</c> reason. Never assembly-wide (an assembly attribute, NoWarn,
/// a ruleset, an .editorconfig or global config severity), never LEVEE006 itself, never a bare <c>#pragma warning disable</c>.
/// Not configurable, so nothing can silence it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SuppressionAnalyzer : DiagnosticAnalyzer
{
    private const string Prefix = "LEVEE";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.Suppression);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (Roles.UsesLevee(start.Compilation))
            {
                start.RegisterSyntaxNodeAction(OnAttribute, SyntaxKind.Attribute);
                start.RegisterSyntaxTreeAction(OnTree);
                start.RegisterCompilationEndAction(OnConfiguration);
            }
        });
    }

    private static void OnAttribute(SyntaxNodeAnalysisContext context)
    {
        var attribute = (AttributeSyntax)context.Node;
        if (attribute.ArgumentList is not { Arguments.Count: >= 2 } args
            || context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol is not IMethodSymbol
            {
                ContainingType.Name: "SuppressMessageAttribute" or "UnconditionalSuppressMessageAttribute",
            })
        {
            return;
        }
        string? Constant(ExpressionSyntax? e) =>
            e is not null && context.SemanticModel.GetConstantValue(e, context.CancellationToken) is { HasValue: true, Value: string s } ? s : null;

        var positional = args.Arguments.Where(a => a.NameEquals is null).ToList();
        var checkId = Constant(positional.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == "checkId")?.Expression
            ?? positional.Where(a => a.NameColon is null).Skip(1).FirstOrDefault()?.Expression);
        if (checkId is null || !checkId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return;
        }
        var id = checkId.Split(':')[0].Trim();

        // A SuppressMessage silences what is reported inside its own symbol, its own attribute included; a diagnostic with no
        // location reaches the build anyway.
        var global = attribute.Parent is AttributeListSyntax { Target.Identifier.Text: "assembly" or "module" };
        if (id == Descriptors.Suppression.Id || global)
        {
            var why = global ? "a Levee rule is never suppressed assembly-wide" : "LEVEE006 is never suppressed";
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Suppression, attribute.GetLocation(), id, why));
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Suppression, Location.None, id, $"{why} ({Where(attribute)})"));
            return;
        }
        var justification = args.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == "Justification")?.Expression;
        if (string.IsNullOrWhiteSpace(Constant(justification)))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Suppression, attribute.GetLocation(), id, "needs Justification = \"why\""));
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Suppression, Location.None, id, $"needs Justification = \"why\" ({Where(attribute)})"));
        }
    }

    private static void OnTree(SyntaxTreeAnalysisContext context)
    {
        foreach (var trivia in context.Tree.GetRoot(context.CancellationToken).DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.GetStructure() is not PragmaWarningDirectiveTriviaSyntax pragma || !pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword))
            {
                continue;
            }
            var ids = pragma.ErrorCodes.Count == 0
                ? [Prefix]
                : pragma.ErrorCodes.Select(c => c.ToString().Trim()).Where(c => c.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            if (ids.Length == 0)
            {
                continue;
            }
            if (pragma.ErrorCodes.Count == 0 || ids.Contains(Descriptors.Suppression.Id))
            {
                var why = pragma.ErrorCodes.Count == 0 ? "a bare #pragma warning disable silences Levee's rules too; name what it disables" : "LEVEE006 is never suppressed";
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.Suppression, pragma.GetLocation(), string.Join(", ", ids), why));
                continue;
            }
            var text = pragma.ToFullString();
            var comment = text.IndexOf("//", StringComparison.Ordinal);
            if (comment < 0 || string.IsNullOrWhiteSpace(text.Substring(comment + 2)))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.Suppression, pragma.GetLocation(), string.Join(", ", ids), "needs a // reason"));
            }
        }
    }

    /// <summary>Severities the build configuration sets on Levee's rules: any but error is a suppression with no reason beside the code.</summary>
    private static void OnConfiguration(CompilationAnalysisContext context)
    {
        var compilation = context.Compilation;
        var trees = compilation.Options.SyntaxTreeOptionsProvider;
        void Report(string id, string where) =>
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Suppression, Location.None, id, $"{where} weakens it for the whole project"));

        foreach (var id in Descriptors.Ids)
        {
            if (compilation.Options.SpecificDiagnosticOptions.TryGetValue(id, out var specific) && Weakens(specific))
            {
                Report(id, "NoWarn or a ruleset");
            }
            if (trees is null)
            {
                continue;
            }
            if (trees.TryGetGlobalDiagnosticValue(id, context.CancellationToken, out var global) && Weakens(global))
            {
                Report(id, "a global analyzer config");
            }
            if (compilation.SyntaxTrees.FirstOrDefault(t => trees.TryGetDiagnosticValue(t, id, context.CancellationToken, out var local) && Weakens(local)) is { } tree)
            {
                Report(id, $"an .editorconfig over {tree.FilePath}");
            }
        }

        const string Bulk = "dotnet_analyzer_diagnostic.category-Levee.severity";
        var options = context.Options.AnalyzerConfigOptionsProvider;
        if (options.GlobalOptions.TryGetValue(Bulk, out var bulk) && bulk.Trim() != "error"
            || compilation.SyntaxTrees.Any(t => options.GetOptions(t).TryGetValue(Bulk, out var b) && b.Trim() != "error"))
        {
            Report("LEVEE*", Bulk);
        }
    }

    private static bool Weakens(ReportDiagnostic severity) => severity is not (ReportDiagnostic.Default or ReportDiagnostic.Error);

    private static string Where(SyntaxNode node)
    {
        var span = node.GetLocation().GetMappedLineSpan();
        return $"{span.Path}:{span.StartLinePosition.Line + 1}";
    }
}
