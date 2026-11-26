using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Levee.Analyzers.Tests;

/// <summary>The build output is the instructions an agent reads: every rule says the fix and where the long form is.</summary>
public sealed class MessageTests
{
    private static readonly DiagnosticDescriptor[] Rules = [.. typeof(OperationGenerator).Assembly.GetType("Levee.Analyzers.Descriptors")!
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => f.GetValue(null)).OfType<DiagnosticDescriptor>()];

    // Whose fix depends on what was found, supplied at the call site.
    private static readonly string[] FixFromCallSite = ["LEVEE008", "LEVEE011", "LEVEE104", "LEVEE108"];

    public static TheoryData<string> Ids() => [.. Rules.Select(r => r.Id).Distinct()];

    [Theory]
    [MemberData(nameof(Ids))]
    public void Every_rule_names_its_fix_and_its_explanation(string id)
    {
        // A rule may say different things for different findings: each message is held to it.
        foreach (var message in Rules.Where(r => r.Id == id).Select(r => r.MessageFormat.ToString(CultureInfo.InvariantCulture)))
        {
            Assert.EndsWith($"(dotnet levee explain {id})", message, StringComparison.Ordinal);
            Assert.True(FixFromCallSite.Contains(id) || message.Contains("Fix: ", StringComparison.Ordinal), message);
        }
    }

    [Fact]
    public void Every_rule_is_explained()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Levee.slnx")))
        {
            dir = dir.Parent;
        }
        var docs = File.ReadAllText(Path.Combine(dir.FullName, "docs", "rules.md"));

        Assert.All(Rules, r => Assert.Contains($"## {r.Id}: {r.Title}", docs, StringComparison.Ordinal));
    }
}
