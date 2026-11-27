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

    /// <summary>
    /// An entry of <c>--json</c>: a finding, or a project whose findings aren't all known (<c>Incomplete</c>, its rule null),
    /// so a reader counting entries never takes an unfinished check for a green one.
    /// </summary>
    internal sealed record Entry(string? Rule, string Project, string? File, int? Line, string Message, string? Suppressed, bool Incomplete);

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
        var root = RealPath(args.Count > 0 ? Path.GetFullPath(args.Positional(0, "the app"), workingDirectory) : Files.AppRoot(workingDirectory));
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
        var unfinished = new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        try
        {
            await Parallel.ForEachAsync(projects, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, async (project, _) =>
            {
                var (analyzed, text) = await DotnetAsync("build", project, "--no-dependencies", "--no-restore", "-nologo", "-v:q", $"-p:LeveeCheckLogs={logs.FullName}");
                foreach (var finding in Parse(text))
                {
                    findings.Add(finding);
                }
                if (Unfinished(analyzed, text) is { } errors)
                {
                    unfinished[Path.GetFileNameWithoutExtension(project)] = errors;
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
        // A project that doesn't compile fails its analyzed build too: it's named once, as broken.
        var incomplete = unfinished.Where(p => !broken.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        if (args.Flag("json"))
        {
            List<Entry> entries = [
                .. distinct.Select(f => new Entry(f.Rule, f.Project, f.File, f.Line, f.Message, f.Suppressed, Incomplete: false)),
                .. broken.Select(p => new Entry(null, p, null, null, "Doesn't compile, analyzers aside, so its findings and those of the projects that reference it aren't all here.", null, Incomplete: true)),
                .. incomplete.Select(p => new Entry(null, p.Key, null, null, Unanalyzed(p.Value), null, Incomplete: true)),
            ];
            await output.WriteLineAsync(JsonSerializer.Serialize(entries, LeveeJson.Options));
            return entries.Count == 0 ? 0 : 1;
        }
        if (broken.Count > 0)
        {
            await output.WriteLineAsync($"{broken.Count} of {projects.Count} projects don't compile, analyzers aside, so their findings and those of projects that reference them aren't all here: "
                + string.Join(", ", broken));
        }
        if (incomplete.Count > 0)
        {
            await output.WriteLineAsync($"{incomplete.Count} of {projects.Count} projects' analyzed builds failed on more than Levee's rules, so their analysis is incomplete "
                + "and their findings aren't all here. Fix: clear every error but Levee's (dotnet build the project lists them), then check again.");
            foreach (var (project, errors) in incomplete)
            {
                await output.WriteLineAsync($"  {project}");
                foreach (var error in errors.Take(5))
                {
                    await output.WriteLineAsync($"      {error.Replace(root + Path.DirectorySeparatorChar, "", StringComparison.Ordinal)}");
                }
                if (errors.Count is 0 or > 5)
                {
                    await output.WriteLineAsync(errors.Count == 0 ? "      (its build failed with no error in its output)" : $"      and {errors.Count - 5} more");
                }
            }
        }
        if (distinct.Count == 0 && broken.Count == 0 && incomplete.Count == 0)
        {
            await output.WriteLineAsync($"No LEVEE findings in {projects.Count} projects.");
            return 0;
        }
        if (distinct.Count == 0)
        {
            await output.WriteLineAsync($"No LEVEE findings in the {projects.Count - broken.Count - incomplete.Count} projects analyzed in full.");
        }
        else
        {
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
        }
        if (!compiled && broken.Count == 0)
        {
            await output.WriteLineAsync("A project failed the generator's rules, so the projects that reference it couldn't compile past it: their own findings show once those are fixed.");
        }
        // An agent reads the last line as the verdict: a suppression is a rule still broken, so it's never "done".
        List<string> why = [];
        if (distinct.Count > 0)
        {
            why.Add($"{distinct.Count} rule{(distinct.Count == 1 ? " is" : "s are")} still broken");
        }
        if (broken.Count > 0)
        {
            why.Add($"{broken.Count} project{(broken.Count == 1 ? " doesn't" : "s don't")} compile");
        }
        if (incomplete.Count > 0)
        {
            why.Add($"analysis of {string.Join(", ", incomplete.Select(p => p.Key))} is incomplete");
        }
        await output.WriteLineAsync($"Not on Levee yet: {string.Join("; ", why)}. The app is on Levee when this check is green.");
        return 1;
    }

    private static string Unanalyzed(IReadOnlyList<string> errors) =>
        "Its analyzed build failed on more than Levee's rules, so its findings aren't all here: "
        + (errors.Count == 0 ? "its build failed with no error in its output." : string.Join(Environment.NewLine, errors));

    /// <summary>
    /// The directory with every link on its way resolved: the compiler applies an .editorconfig's sections by the sources' real
    /// paths, so a build reached through a link (macOS's /tmp and /var) runs without them and passes what fails in the shell.
    /// </summary>
    internal static string RealPath(string directory)
    {
        var full = Path.GetFullPath(directory);
        var real = Path.GetPathRoot(full)!;
        foreach (var part in full[real.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            real = Path.Combine(real, part);
            if (new DirectoryInfo(real).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                real = RealPath(target.FullName);
            }
        }
        return real;
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

    /// <summary>
    /// Why a project's analyzed build failed, when it wasn't only Levee's rules: its other errors, each once however many of its
    /// target frameworks report it. Null when every rule ran: a build that failed on LEVEE errors alone is a complete analysis.
    /// </summary>
    internal static IReadOnlyList<string>? Unfinished(bool succeeded, string buildOutput)
    {
        if (succeeded)
        {
            return null;
        }
        List<string> errors = [.. buildOutput.Split('\n')
            .Select(line => line.Trim())
            .Where(line => AnyError().IsMatch(line) && !Diagnostic().IsMatch(line))
            .Select(line => ProjectSuffix().Replace(line, ""))
            .Distinct()];
        return errors.Count > 0 || !Parse(buildOutput).Any() ? errors : null;
    }

    // A multi-target project's diagnostics name it with the target they came from: [App.csproj::TargetFramework=net10.0].
    private const string Project = @"\s*\[(?<project>[^\]]+?\.csproj)(?:::[^\]]*)?\]$";

    [GeneratedRegex(@"\berror (?!LEVEE)[A-Z]+\d+:.*?" + Project)]
    private static partial Regex OtherError();

    [GeneratedRegex(@"^(?:(?<file>[^:(]+(?::[^:(]+)?)(?:\((?<line>\d+),\d+\))?\s*:\s*)?error (?<rule>LEVEE\d+): (?<message>.*?)" + Project)]
    private static partial Regex Diagnostic();

    // MSBuild's canonical error, with or without a code: "file(1,2): error CA1822:", "CSC : error CS0006:", "error MSB1009:".
    [GeneratedRegex(@"(?:^|\)\s*:\s*|\s:\s)error(?:\s+[A-Za-z]+\d+)?\s*:")]
    private static partial Regex AnyError();

    [GeneratedRegex(Project)]
    private static partial Regex ProjectSuffix();

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
