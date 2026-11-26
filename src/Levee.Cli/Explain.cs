using System.Text.RegularExpressions;

namespace Levee.Cli;

/// <summary>A rule's long form, from docs/rules.md shipped inside the tool, so it matches the version the app pins.</summary>
internal static partial class Explain
{
    public static void Run(Args a, TextWriter output)
    {
        var rules = Rules();
        if (a.Count == 0)
        {
            foreach (var (id, text) in rules)
            {
                output.WriteLine(text.Split('\n')[0].TrimStart('#', ' '));
            }
            return;
        }
        var wanted = a.Positional(0, "a rule id").ToUpperInvariant();
        wanted = wanted.StartsWith("LEVEE", StringComparison.Ordinal) ? wanted : "LEVEE" + wanted;
        output.WriteLine(rules.TryGetValue(wanted, out var section)
            ? section
            : throw new CliException($"There's no rule {wanted}. dotnet levee explain lists them."));
    }

    public static IReadOnlyDictionary<string, string> Rules()
    {
        using var stream = typeof(Explain).Assembly.GetManifestResourceStream("Levee.Cli.rules.md")
            ?? throw new InvalidOperationException("rules.md isn't embedded in the tool.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        return Section().Matches(text).ToDictionary(m => m.Groups["id"].Value, m => m.Value.TrimEnd(), StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^## (?<id>LEVEE\d{3}):.*?(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Section();
}
