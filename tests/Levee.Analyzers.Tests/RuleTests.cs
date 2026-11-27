namespace Levee.Analyzers.Tests;

public sealed class RuleTests
{
    private static readonly Microsoft.CodeAnalysis.MetadataReference Levee =
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(IOperation).Assembly.Location);

    /// <summary>What a project referencing Levee gets with it.</summary>
    private static readonly Microsoft.CodeAnalysis.MetadataReference Di =
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location);

    private static readonly Microsoft.CodeAnalysis.MetadataReference Configuration =
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Configuration.IConfiguration).Assembly.Location);

    private static Task<Probe.Output> Compile(string role, string source, params Microsoft.CodeAnalysis.MetadataReference[] more) =>
        Probe.CompileAsync(role, "using System;\nusing Levee;\n" + source, "App", [Levee, Di, Configuration, .. more]);

    [Theory]
    [InlineData("")]
    [InlineData("Service")]
    public async Task A_project_using_Levee_declares_a_role(string role)
    {
        Assert.Equal(["LEVEE001"], (await Compile(role, "public static class Nothing;")).Ids);
    }

    [Theory]
    [InlineData("Domain", "Contracts", true)]
    [InlineData("Contracts", "Domain", true)]
    [InlineData("Application", "Adapter", true)]
    [InlineData("Client", "Application", true)]
    [InlineData("Host", "Test", true)]
    [InlineData("Adapter", "Adapter", true)]
    [InlineData("Kernel", "Contracts", true)]
    [InlineData("Domain", "Kernel", false)]
    [InlineData("Contracts", "Kernel", false)]
    [InlineData("Client", "Kernel", false)]
    [InlineData("Composition", "Adapter", false)]
    [InlineData("Composition", "Host", true)]
    [InlineData("Composition", "Composition", true)]
    [InlineData("Host", "Composition", false)]
    [InlineData("Adapter", "Composition", true)]
    [InlineData("Application", "Domain", false)]
    [InlineData("Adapter", "Application", false)]
    [InlineData("Host", "Adapter", false)]
    [InlineData("Test", "Host", false)]
    [InlineData("Test", "TestSupport", false)]
    [InlineData("TestSupport", "Adapter", false)]
    [InlineData("Host", "TestSupport", true)]
    [InlineData("Composition", "TestSupport", true)]
    [InlineData("Tool", "TestSupport", true)]
    public async Task A_role_references_only_what_it_may(string role, string referenced, bool refused)
    {
        var other = await Probe.CompileAsync(referenced, "public static class Other;", "Other", Levee, Di, Configuration);
        var output = await Compile(role, "public static class Mine;", other.Reference());

        Assert.Equal(refused, output.Ids.Contains("LEVEE002"));
    }

    [Theory]
    [InlineData("Domain", "", true)]
    [InlineData("Contracts", "", true)]
    [InlineData("Application", "", true)]
    [InlineData("Adapter", "", false)]
    [InlineData("Domain", "Vendor.Money", false)]
    [InlineData("Domain", "Vendor.*", false)]
    [InlineData("Domain", "Vendor.Other;Vendor.Moneys", true)]
    [InlineData("Domain", "Vendor.Other,Vendor.Money", false)]
    [InlineData("Kernel", "", true)]
    public async Task Inner_roles_reference_only_the_BCL_abstractions_and_what_they_allow(string role, string allowed, bool refused)
    {
        var vendor = await Probe.CompileAsync(new Probe.Settings(""), "public static class Money;", "Vendor.Money");
        var output = await Probe.CompileAsync(new Probe.Settings(role, new Dictionary<string, string> { ["LeveeAllowedReferences"] = allowed }),
            "using Levee;\npublic static class Mine;", "App", Levee, vendor.Reference());

        Assert.Equal(refused, output.Ids.Contains("LEVEE008"));
    }

    [Fact]
    public async Task Abstractions_and_Levee_are_always_allowed()
    {
        var output = await Compile("Domain", "public static class Mine;");

        Assert.Empty(output.Ids);
    }

    [Fact]
    public async Task Something_that_uses_Levee_without_its_analyzers_is_refused()
    {
        var unanalyzed = await Probe.CompileAsync(new Probe.Settings(""), "public sealed class Smuggled : Levee.IAggregateRoot;", "Smuggler", Levee);
        var output = await Compile("Host", "public static class Mine;", unanalyzed.Reference());

        Assert.Contains("LEVEE009", output.Ids);
    }

    [Theory]
    [InlineData("Application", "DateTime.UtcNow", true)]
    [InlineData("Domain", "DateTimeOffset.Now", true)]
    [InlineData("Adapter", "TimeProvider.System.GetUtcNow()", true)]
    [InlineData("Host", "TimeProvider.System.GetUtcNow()", false)]
    [InlineData("Composition", "TimeProvider.System.GetUtcNow()", false)]
    [InlineData("Kernel", "DateTime.UtcNow", true)]
    [InlineData("Host", "DateTime.Now", true)]
    [InlineData("Test", "DateTime.Now", false)]
    public async Task Time_comes_from_TimeProvider(string role, string expression, bool refused)
    {
        var output = await Compile(role, $"public static class Clock {{ public static object Now() => {expression}; }}");

        Assert.Equal(refused, output.Ids.Contains("LEVEE003"));
    }

    private const string Persistence = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContext { public int SaveChanges() => 0; public Infrastructure.DatabaseFacade Database => new(); }
            namespace Infrastructure { public class DatabaseFacade { public void BeginTransaction() { } public Storage.IDbContextTransaction? CurrentTransaction => null; } }
            namespace Storage { public interface IDbContextTransaction { void Commit(); } }
        }
        public sealed class AppDb : Microsoft.EntityFrameworkCore.DbContext;
        """;

    [Theory]
    [InlineData("Adapter", "db.SaveChanges();", true)]
    [InlineData("Application", "db.Database.BeginTransaction();", true)]
    [InlineData("Host", "db.SaveChanges();", true)]
    [InlineData("Application", "db.Database.CurrentTransaction!.Commit();", true)]
    [InlineData("Test", "db.SaveChanges();", false)]
    public async Task Only_the_dispatcher_saves_or_controls_transactions(string role, string statement, bool refused)
    {
        var output = await Compile(role, Persistence + $"public static class Store {{ public static void Go(AppDb db) {{ {statement} }} }}");

        Assert.Equal(refused, output.Ids.Contains("LEVEE004"));
    }

    [Theory]
    [InlineData("Application", true)]
    [InlineData("Adapter", true)]
    [InlineData("Host", false)]
    [InlineData("Composition", false)]
    [InlineData("Client", true)]
    public async Task Services_are_located_only_in_hosts(string role, bool refused)
    {
        var output = await Compile(role, "public static class Locator { public static object? Find(System.IServiceProvider s) => s.GetService(typeof(string)); }");

        Assert.Equal(refused, output.Ids.Contains("LEVEE005"));
    }

    private static Task<Probe.Output> Migrating(bool allowed, string source) =>
        Probe.CompileAsync(new Probe.Settings("Application", Properties: allowed ? new Dictionary<string, string> { ["LeveeAllowSuppressions"] = "true" } : null),
            "using System;\nusing Levee;\n" + source, "App", Levee, Di, Configuration);

    [Theory]
    [InlineData("""[System.Diagnostics.CodeAnalysis.SuppressMessage("Levee", "LEVEE003")]""", true, true)]
    [InlineData("""[System.Diagnostics.CodeAnalysis.SuppressMessage("Levee", "LEVEE003", Justification = " ")]""", true, true)]
    [InlineData("""[System.Diagnostics.CodeAnalysis.SuppressMessage("Levee", "LEVEE003", Justification = "The audit stamps wall time.")]""", true, false)]
    [InlineData("""[System.Diagnostics.CodeAnalysis.SuppressMessage("Levee", "LEVEE003", Justification = "The audit stamps wall time.")]""", false, true)]
    [InlineData("""[System.Diagnostics.CodeAnalysis.SuppressMessage("Levee", "LEVEE006", Justification = "Because.")]""", true, true)]
    public async Task A_suppression_carries_its_reason_and_builds_only_while_migrating(string attribute, bool allowed, bool refused)
    {
        var output = await Migrating(allowed, $"public static class Clock {{ {attribute} public static object Now() => System.DateTime.UtcNow; }}");

        Assert.Equal(refused, output.Ids.Contains("LEVEE006"));
        Assert.Equal(!attribute.Contains("LEVEE003", StringComparison.Ordinal), output.Ids.Contains("LEVEE003"));
    }

    [Theory]
    [InlineData("#pragma warning disable LEVEE003", true, true)]
    [InlineData("#pragma warning disable LEVEE003 // the audit stamps wall time", true, false)]
    [InlineData("#pragma warning disable LEVEE003 // the audit stamps wall time", false, true)]
    [InlineData("#pragma warning disable", true, true)]
    public async Task A_pragma_carries_its_reason_and_builds_only_while_migrating(string pragma, bool allowed, bool refused)
    {
        var output = await Migrating(allowed, $"public static class Clock {{\n{pragma}\npublic static object Now() => System.DateTime.UtcNow;\n}}");

        Assert.Equal(refused, output.Ids.Contains("LEVEE006"));
    }

    [Fact]
    public async Task NoWarn_is_refused()
    {
        var output = await Probe.CompileAsync(new Probe.Settings("Application", NoWarn: ["LEVEE003", "LEVEE006"]),
            "using System;\npublic static class Clock { public static object Now() => DateTime.UtcNow; }", "App", Levee);

        Assert.Contains("LEVEE006", output.Ids);
    }

    [Fact]
    public async Task A_global_suppression_is_refused()
    {
        var output = await Compile("Application",
            """[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Levee", "LEVEE003", Justification = "Everywhere.")]""" + "\npublic static class Nothing;");

        Assert.Contains("LEVEE006", output.Ids);
    }

    [Theory]
    [InlineData("public string Text { get; set; } = \"\";", true)]
    [InlineData("public string Text { get; internal set; } = \"\";", true)]
    [InlineData("public string Text = \"\";", true)]
    [InlineData("public string Text { get; private set; } = \"\";", false)]
    [InlineData("public string Text { get; init; } = \"\";", false)]
    [InlineData("public readonly string Text = \"\";", false)]
    [InlineData("public System.Collections.Generic.List<string> Tags { get; } = [];", true)]
    [InlineData("public readonly System.Collections.Generic.List<string> Tags = [];", true)]
    [InlineData("public string[] Tags { get; } = [];", true)]
    [InlineData("public System.Collections.Generic.IReadOnlyList<string> Tags { get; } = [];", false)]
    [InlineData("private readonly System.Collections.Generic.List<string> _tags = [];", false)]
    public async Task An_aggregate_changes_only_through_its_methods(string member, bool refused)
    {
        var output = await Compile("Domain", $"public sealed class Note : IAggregateRoot {{ {member} }}");

        Assert.Equal(refused, output.Ids.Contains("LEVEE007"));
    }
}
