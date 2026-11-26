using System.Collections.Immutable;
using Levee.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Levee.Analyzers.Tests;

/// <summary>Compiles source as a project of a role would: Levee's generator, then its analyzers.</summary>
internal static class Probe
{
    /// <summary>The shared framework only, so the package allowlist (LEVEE008) sees what a real project would.</summary>
    private static readonly MetadataReference[] Framework = [.. Directory
        .GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll")
        .Where(p => !Path.GetFileName(p).StartsWith("Microsoft.DiaSymReader", StringComparison.Ordinal))
        .Select(p => MetadataReference.CreateFromFile(p))];

    /// <summary>What Levee's package brings with it, as NuGet would: a project that references Levee sees these too.</summary>
    private static readonly MetadataReference[] LeveeDependencies =
    [
        MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Configuration.IConfiguration).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Primitives.IChangeToken).Assembly.Location),
    ];

    /// <summary>Every analyzer Levee ships, found rather than listed, so a new one can't be left out of the tests.</summary>
    private static readonly DiagnosticAnalyzer[] Analyzers = [.. typeof(OperationGenerator).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t) && t.IsDefined(typeof(DiagnosticAnalyzerAttribute), inherit: false))
        .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!)];

    public sealed record Output(Compilation Compilation, ImmutableArray<Diagnostic> Errors)
    {
        public IEnumerable<string> Ids => Errors.Select(d => d.Id);

        public string Generated(string hint) =>
            Compilation.SyntaxTrees.Single(t => t.FilePath.EndsWith(hint, StringComparison.Ordinal)).ToString();

        public MetadataReference Reference()
        {
            using var image = new MemoryStream();
            var emitted = Compilation.Emit(image);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            return MetadataReference.CreateFromImage(image.ToArray());
        }
    }

    public static Task<Output> CompileAsync(string role, string source, string assembly = "Probe", params MetadataReference[] references) =>
        CompileAsync(new Settings(role), source, assembly, references);

    /// <param name="Properties">MSBuild properties the analyzers see, beyond the role.</param>
    /// <param name="NoWarn">What the project's NoWarn suppresses.</param>
    /// <param name="Documented">GenerateDocumentationFile: doc comments are checked, and a public member without one is CS1591.</param>
    /// <param name="Config">Analyzer config keys, as an .editorconfig or .globalconfig sets them.</param>
    /// <param name="Exe">An executable, not a library.</param>
    public sealed record Settings(string Role, IReadOnlyDictionary<string, string>? Properties = null, IReadOnlyList<string>? NoWarn = null,
        bool Documented = false, IReadOnlyDictionary<string, string>? Config = null, bool Exe = false);

    public static async Task<Output> CompileAsync(Settings settings, string source, string assembly = "Probe", params MetadataReference[] references)
    {
        var compilationOptions = new CSharpCompilationOptions(settings.Exe ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable)
            .WithSpecificDiagnosticOptions((settings.NoWarn ?? []).Select(id => KeyValuePair.Create(id, ReportDiagnostic.Suppress)));
        var parse = CSharpParseOptions.Default.WithDocumentationMode(settings.Documented ? DocumentationMode.Diagnose : DocumentationMode.Parse);
        var compilation = CSharpCompilation.Create(assembly, [CSharpSyntaxTree.ParseText(source, parse, path: $"{assembly}.cs")],
            [.. Framework, .. references, .. references.Any(r => r.Display?.EndsWith("Levee.dll", StringComparison.Ordinal) == true) ? LeveeDependencies : []],
            compilationOptions);
        var properties = new Dictionary<string, string>(settings.Properties ?? new Dictionary<string, string>());
        if (settings.Role.Length > 0)
        {
            properties["LeveeRole"] = settings.Role;
        }
        var options = new BuildOptions(properties, settings.Config ?? new Dictionary<string, string>());
        CSharpGeneratorDriver.Create([new OperationGenerator().AsSourceGenerator()], optionsProvider: options, parseOptions: parse)
            .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var generatorErrors);
        var analyzers = Analyzers;
        var analyzed = await generated.WithAnalyzers([.. analyzers], new AnalyzerOptions([], options)).GetAllDiagnosticsAsync();
        return new Output(generated, [.. generatorErrors.Concat(analyzed).Where(d => d.Severity == DiagnosticSeverity.Error)]);
    }

    private sealed class BuildOptions(IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> config) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(properties, config);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class Options(IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> config) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = "";
            return key.StartsWith("build_property.", StringComparison.Ordinal)
                ? properties.TryGetValue(key["build_property.".Length..], out value!)
                : config.TryGetValue(key, out value!);
        }
    }
}
