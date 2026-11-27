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
    /// <param name="Suppressed">The reason it was silenced, when it was: debt a migration may carry, never a green check.</param>
    public sealed record Finding(string Rule, string Project, string? File, int? Line, string Message, string? Suppressed = null);

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
        // The generator's rules (LEVEE101-111) run in this pass too: a project they fail is a finding, not a broken app, though
        // the projects that reference it can't compile past it.
        var findings = new ConcurrentBag<Finding>(compiled ? [] : Parse(log));
        // Partway through a migration some projects don't compile yet: the rest are still worth taking stock of.
        var broken = compiled ? [] : Broken(log);
        if (!compiled && findings.IsEmpty && broken.Count == 0)
        {
            throw new CliException($"The app doesn't compile, analyzers aside; fix that first:{Environment.NewLine}{log}");
        }

        // The solution's projects, the ones it compiled: a template or a sample beside them isn't the app.
        var projects = ProjectsOf(solution);
        // The compiler's error log is the one record of what a suppression silenced: the build output omits it.
        var logs = Directory.CreateTempSubdirectory("levee-check-");
        try
        {
            await Parallel.ForEachAsync(projects, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, async (project, _) =>
            {
                var (_, text) = await DotnetAsync("build", project, "--no-dependencies", "--no-restore", "-nologo", "-v:q", $"-p:LeveeCheckLogs={logs.FullName}");
                foreach (var finding in Parse(text))
                {
                    findings.Add(finding);
                }
            });
            foreach (var errorLog in logs.EnumerateFiles("*.sarif"))
            {
                foreach (var finding in Suppressed(errorLog.FullName, errorLog.Name[..errorLog.Name.LastIndexOf('~')]))
                {
                    findings.Add(finding);
                }
            }
        }
        finally
        {
            logs.Delete(recursive: true);
        }

        var distinct = findings.Distinct().OrderBy(f => f.Rule, StringComparer.Ordinal).ThenBy(f => f.Project, StringComparer.Ordinal)
            .ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ToList();
        if (args.Flag("json"))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(distinct, LeveeJson.Options));
            return distinct.Count == 0 && broken.Count == 0 ? 0 : 1;
        }
        if (broken.Count > 0)
        {
            await output.WriteLineAsync($"{broken.Count} of {projects.Count} projects don't compile, analyzers aside, so their findings and those of projects that reference them aren't all here: "
                + string.Join(", ", broken));
        }
        if (distinct.Count == 0 && broken.Count == 0)
        {
            await output.WriteLineAsync($"No LEVEE findings in {projects.Count} projects.");
            return 0;
        }
        if (distinct.Count == 0)
        {
            await output.WriteLineAsync($"No LEVEE findings in the {projects.Count - broken.Count} projects that compile.");
            await output.WriteLineAsync($"Not on Levee yet: {broken.Count} project{(broken.Count == 1 ? " doesn't" : "s don't")} compile. The app is on Levee when this check is green.");
            return 1;
        }
        var open = distinct.Where(f => f.Suppressed is null).ToList();
        var debt = distinct.Where(f => f.Suppressed is not null).ToList();
        await WriteAsync(open, root, output);
        if (debt.Count > 0)
        {
            await output.WriteLineAsync($"Suppressed ({debt.Count}): each still breaks its rule. Fix: change the code to follow it; "
                + "where Levee has no way to express what the code needs, that's a gap in Levee to report, not a suppression to keep.");
            await WriteAsync(debt, root, output);
        }
        await output.WriteLineAsync($"{open.Count} findings and {debt.Count} suppressed in {distinct.Select(f => f.Project).Distinct().Count()} of {projects.Count} projects.");
        // An agent reads the last line as the verdict: a suppression is a rule still broken, so it's never "done".
        await output.WriteLineAsync($"Not on Levee yet: {open.Count + debt.Count} rule{(open.Count + debt.Count == 1 ? " is" : "s are")} still broken. The app is on Levee when this check is green.");
        if (!compiled && broken.Count == 0)
        {
            await output.WriteLineAsync("A project failed the generator's rules, so the projects that reference it couldn't compile past it: their own findings show once those are fixed.");
        }
        return 1;
    }

    private static async Task WriteAsync(List<Finding> findings, string root, TextWriter output)
    {
        foreach (var rule in findings.GroupBy(f => f.Rule))
        {
            var hint = $" (dotnet levee explain {rule.Key})";
            await output.WriteLineAsync($"{rule.Key} ({rule.Count()}){hint}");
            foreach (var f in rule)
            {
                var where = f.File is null ? "" : $"{Path.GetRelativePath(root, f.File)}{(f.Line is { } line ? $":{line}" : "")}: ";
                var message = f.Message.EndsWith(hint, StringComparison.Ordinal) ? f.Message[..^hint.Length] : f.Message;
                await output.WriteLineAsync(f.Suppressed is { } reason
                    ? $"  {f.Project}  {where}{message}{Environment.NewLine}      suppressed: {reason}"
                    : $"  {f.Project}  {where}{message}");
            }
        }
    }

    /// <summary>The LEVEE diagnostics a project's code silenced, from its SARIF 2.1 error log.</summary>
    internal static IEnumerable<Finding> Suppressed(string log, string project)
    {
        using var sarif = JsonDocument.Parse(File.ReadAllText(log));
        var found = new List<Finding>();
        foreach (var run in sarif.RootElement.GetProperty("runs").EnumerateArray())
        {
            if (!run.TryGetProperty("results", out var results))
            {
                continue;
            }
            foreach (var result in results.EnumerateArray())
            {
                var rule = result.GetProperty("ruleId").GetString()!;
                if (!rule.StartsWith("LEVEE", StringComparison.Ordinal)
                    || !result.TryGetProperty("suppressions", out var suppressions) || suppressions.GetArrayLength() == 0)
                {
                    continue;
                }
                string? file = null;
                int? line = null;
                if (result.TryGetProperty("locations", out var locations) && locations.GetArrayLength() > 0
                    && locations[0].TryGetProperty("physicalLocation", out var physical))
                {
                    file = physical.TryGetProperty("artifactLocation", out var artifact) && artifact.TryGetProperty("uri", out var uri)
                        ? new Uri(uri.GetString()!).LocalPath : null;
                    line = physical.TryGetProperty("region", out var region) && region.TryGetProperty("startLine", out var start) ? start.GetInt32() : null;
                }
                var suppression = suppressions[0];
                var reason = suppression.TryGetProperty("justification", out var justification) ? justification.GetString() : PragmaReason(file, line, rule);
                found.Add(new Finding(rule, project, file, line, result.GetProperty("message").GetProperty("text").GetString()!, reason ?? "(no reason)"));
            }
        }
        return found;
    }

    /// <summary>The reason beside the nearest <c>#pragma warning disable</c> of the rule above the line.</summary>
    private static string? PragmaReason(string? file, int? line, string rule)
    {
        if (file is null || line is not { } at || !File.Exists(file))
        {
            return null;
        }
        var lines = File.ReadAllLines(file);
        for (var i = Math.Min(at, lines.Length) - 1; i >= 0; i--)
        {
            var text = lines[i].Trim();
            if (text.StartsWith("#pragma warning disable", StringComparison.Ordinal) && text.Contains(rule, StringComparison.Ordinal))
            {
                var comment = text.IndexOf("//", StringComparison.Ordinal);
                return comment < 0 ? null : text[(comment + 2)..].Trim();
            }
        }
        return null;
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
                HelpLink().Replace(m.Groups["message"].Value, "").Trim()))
            .Distinct();

    // The compiler adds each rule's help link to its message; the rule's explain hint already names where the long form is.
    [GeneratedRegex(@"\s*\(https?://[^\s)]+\)\s*$")]
    private static partial Regex HelpLink();

    /// <summary>The projects a build failed with errors other than Levee's.</summary>
    internal static List<string> Broken(string buildOutput) =>
        [.. buildOutput.Split('\n')
            .Select(line => OtherError().Match(line.Trim()))
            .Where(m => m.Success)
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups["project"].Value))
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)];

    [GeneratedRegex(@"\berror (?!LEVEE)[A-Z]+\d+:.*\[(?<project>[^\]]+\.csproj)\]$")]
    private static partial Regex OtherError();

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
