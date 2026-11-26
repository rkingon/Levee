using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Levee.Cli;

/// <summary>
/// <c>dotnet levee check</c>: the app against Levee's rules, every project's findings at once. One project's errors stop the
/// build of everything after it, so the solution is compiled once without analyzers, then each project is analyzed alone.
/// </summary>
internal static partial class Check
{
    public sealed record Finding(string Rule, string Project, string? File, int? Line, string Message);

    public static List<string> ProjectsOf(string solution)
    {
        var dir = Path.GetDirectoryName(solution)!;
        var text = File.ReadAllText(solution);
        var paths = solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? System.Xml.Linq.XDocument.Parse(text).Descendants("Project").Select(p => (string?)p.Attribute("Path"))
            : SlnProject().Matches(text).Select(m => (string?)m.Groups["path"].Value);
        return [.. paths.OfType<string>()
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .Distinct()];
    }

    [GeneratedRegex(@"^Project\(""[^""]*""\)\s*=\s*""[^""]*"",\s*""(?<path>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex SlnProject();

    public static async Task<int> RunAsync(Args args, string workingDirectory, TextWriter output)
    {
        var root = args.Count > 0 ? Path.GetFullPath(args.Positional(0, "the app"), workingDirectory) : Files.AppRoot(workingDirectory);
        var solution = Directory.EnumerateFiles(root, "*.slnx").Concat(Directory.EnumerateFiles(root, "*.sln")).FirstOrDefault()
            ?? throw new CliException($"No solution in {root}.");

        // This pass alone builds without the rules; the token, in the property and the environment both, says it's check's.
        var token = Guid.NewGuid().ToString("N");
        var (compiled, log) = await DotnetAsync(new Dictionary<string, string> { ["LEVEE_CHECK_TOKEN"] = token },
            "build", solution, "-p:RunAnalyzers=false", $"-p:LeveeCheck={token}", "-nodeReuse:false", "-nologo", "-v:q");
        if (!compiled)
        {
            throw new CliException($"The app doesn't compile, analyzers aside; fix that first:{Environment.NewLine}{log}");
        }

        // The solution's projects, the ones it compiled: a template or a sample beside them isn't the app.
        var projects = ProjectsOf(solution);
        var findings = new ConcurrentBag<Finding>();
        await Parallel.ForEachAsync(projects, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, async (project, _) =>
        {
            var (_, text) = await DotnetAsync("build", project, "--no-dependencies", "--no-restore", "-nologo", "-v:q");
            foreach (var finding in Parse(text))
            {
                findings.Add(finding);
            }
        });

        var distinct = findings.Distinct().OrderBy(f => f.Rule, StringComparer.Ordinal).ThenBy(f => f.Project, StringComparer.Ordinal)
            .ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ToList();
        if (args.Flag("json"))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(distinct, LeveeJson.Options));
            return distinct.Count == 0 ? 0 : 1;
        }
        if (distinct.Count == 0)
        {
            await output.WriteLineAsync($"No LEVEE findings in {projects.Count} projects.");
            return 0;
        }
        foreach (var rule in distinct.GroupBy(f => f.Rule))
        {
            var hint = $" (dotnet levee explain {rule.Key})";
            await output.WriteLineAsync($"{rule.Key} ({rule.Count()}){hint}");
            foreach (var f in rule)
            {
                var where = f.File is null ? "" : $"{Path.GetRelativePath(root, f.File)}{(f.Line is { } line ? $":{line}" : "")}: ";
                var message = f.Message.EndsWith(hint, StringComparison.Ordinal) ? f.Message[..^hint.Length] : f.Message;
                await output.WriteLineAsync($"  {f.Project}  {where}{message}");
            }
        }
        await output.WriteLineAsync($"{distinct.Count} findings in {distinct.Select(f => f.Project).Distinct().Count()} of {projects.Count} projects.");
        return 1;
    }

    /// <summary>The LEVEE errors in a build's output, one per diagnostic however often the build repeats it.</summary>
    internal static IEnumerable<Finding> Parse(string buildOutput) =>
        buildOutput.Split('\n')
            .Select(line => Diagnostic().Match(line.Trim()))
            .Where(m => m.Success)
            .Select(m => new Finding(
                m.Groups["rule"].Value,
                Path.GetFileNameWithoutExtension(m.Groups["project"].Value),
                m.Groups["file"].Value is { Length: > 0 } file && !file.StartsWith("CSC", StringComparison.Ordinal) ? file : null,
                m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
                m.Groups["message"].Value.Trim()))
            .Distinct();

    [GeneratedRegex(@"^(?:(?<file>[^:(]+(?::[^:(]+)?)(?:\((?<line>\d+),\d+\))?\s*:\s*)?error (?<rule>LEVEE\d+): (?<message>.*?)\s*\[(?<project>[^\]]+\.csproj)\]$")]
    private static partial Regex Diagnostic();

    private static Task<(bool Ok, string Output)> DotnetAsync(params string[] arguments) =>
        DotnetAsync(new Dictionary<string, string>(), arguments);

    private static async Task<(bool Ok, string Output)> DotnetAsync(IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new CliException("Couldn't start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode == 0, await stdout + await stderr);
    }
}
