using Microsoft.CodeAnalysis;

namespace Levee.Analyzers.Tests;

/// <summary>What the first independent audit found: wrong code that compiled, and right code refused.</summary>
public sealed class AuditTests
{
    private static readonly MetadataReference Levee = GeneratorTests.Levee;

    private static Task<Probe.Output> Compile(string role, string source, string assembly = "App", params MetadataReference[] more) =>
        Probe.CompileAsync(role, "using System;\nusing Levee;\n" + source, assembly, [Levee, .. more]);

    private const string Data = """
        namespace System.Data.Common
        {
            public abstract class DbConnection { }
            public abstract class DbDataSource { public DbConnection OpenConnection() => null!; public object CreateCommand(string? commandText = null) => null!; }
        }
        namespace Npgsql { public sealed class NpgsqlConnection : System.Data.Common.DbConnection { public NpgsqlConnection(string s) { } } }
        namespace System.Transactions { public sealed class TransactionScope : System.IDisposable { public void Dispose() { } } }
        public static class Db { public static int ExecuteSqlRaw(string sql) => 0; }
        """;

    [Theory]
    [InlineData("Adapter", """Db.ExecuteSqlRaw("select set_config('levee.scope', 'globex', true)");""", "LEVEE004")]
    [InlineData("Adapter", """var key = "levee.platform"; Db.ExecuteSqlRaw("select set_config(" + key + ", 'on', true)");""", "LEVEE004")]
    [InlineData("Adapter", """using var scope = new System.Transactions.TransactionScope();""", "LEVEE004")]
    [InlineData("Adapter", """_ = new Npgsql.NpgsqlConnection("Host=db");""", "LEVEE018")]
    [InlineData("Adapter", """System.Data.Common.DbDataSource source = null!; source.OpenConnection();""", "LEVEE018")]
    [InlineData("Tool", """_ = new Npgsql.NpgsqlConnection("Host=db"); Db.ExecuteSqlRaw("select set_config('levee.scope', 'x', true)");""", null)]
    [InlineData("Adapter", """Db.ExecuteSqlRaw("select current_setting('app.locale', true)");""", null)]
    public async Task The_scope_and_the_transaction_are_the_unit_of_works_alone(string role, string statement, string? expected)
    {
        var output = await Probe.CompileAsync(new Probe.Settings(role, Exe: role == "Tool"),
            "using System;\nusing Levee;\n" + Data + $"public static class Store {{ public static void Go() {{ {statement} }} public static void Main() {{ }} }}", "App", Levee);

        Assert.Equal(expected is null ? [] : new[] { expected }, output.Ids.Where(i => i is "LEVEE004" or "LEVEE018").Distinct());
    }

    [Theory]
    [InlineData("Application", "Guid.CreateVersion7()", "LEVEE003")]
    [InlineData("Application", "Guid.CreateVersion7(DateTimeOffset.UnixEpoch)", null)]
    [InlineData("Application", "new System.IO.StreamReader(\"rates.json\")", "LEVEE012")]
    [InlineData("Application", "new System.IO.StreamReader(new System.IO.MemoryStream())", null)]
    [InlineData("Application", "System.Xml.Linq.XDocument.Load(\"rates.xml\")", "LEVEE012")]
    [InlineData("Application", "System.IO.Path.GetTempFileName()", "LEVEE012")]
    [InlineData("Application", "Environment.ExpandEnvironmentVariables(\"%HOME%\")", "LEVEE012")]
    [InlineData("Contracts", "System.Net.Http.HttpMethod.Get", null)]
    [InlineData("Contracts", "new System.Net.Http.Headers.MediaTypeHeaderValue(\"application/json\")", null)]
    public async Task Clock_and_IO_have_no_side_doors(string role, string expression, string? expected)
    {
        var output = await Probe.CompileAsync(role, $"using System;\nusing Levee;\npublic static class Probe {{ public static object Go() => {expression}; }}", "App",
            Levee, MetadataReference.CreateFromFile(typeof(System.Xml.Linq.XDocument).Assembly.Location));

        Assert.Equal(expected is null ? [] : new[] { expected }, output.Ids.Distinct());
    }

    [Fact]
    public async Task A_member_named_without_its_type_is_its_types_IO()
    {
        var output = await Probe.CompileAsync("Application", "using static System.IO.File;\npublic static class Reader { public static string Go() => ReadAllText(\"x\"); }", "App", Levee);

        Assert.Contains("LEVEE012", output.Ids);
    }

    [Theory]
    [InlineData("Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<object>(services)", "LEVEE005")]
    [InlineData("((Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider)services).GetKeyedService(typeof(object), \"a\")", "LEVEE005")]
    public async Task Service_location_has_no_side_doors(string expression, string expected)
    {
        var output = await Compile("Adapter", $"public static class Locator {{ public static object? Go(IServiceProvider services) => {expression}; }}", "App", GeneratorTests.Di);

        Assert.Contains(expected, output.Ids);
    }

    [Fact]
    public async Task Every_analyzer_at_once_set_below_an_error_is_refused()
    {
        var output = await Probe.CompileAsync(new Probe.Settings("Domain", Config: new Dictionary<string, string> { ["dotnet_analyzer_diagnostic.severity"] = "none" }),
            "using Levee;\npublic static class Mine;", "App", Levee);

        Assert.Contains("LEVEE006", output.Ids);
    }

    [Fact]
    public async Task A_file_that_calls_itself_generated_is_held_to_the_rules()
    {
        var output = await Probe.CompileAsync("Application", "// <auto-generated/>\nusing Levee;\npublic static class Clock { public static object Now() => System.DateTime.UtcNow; }", "App", Levee);

        Assert.Contains("LEVEE003", output.Ids);
    }

    [Theory]
    [InlineData("Domain", "public class Base { public int State { get; set; } }\npublic sealed class Order : Base, IAggregateRoot;", "LEVEE007")]
    [InlineData("Domain", "public sealed class Lines : System.Collections.Generic.List<int>;\npublic sealed class Order : IAggregateRoot { public Lines Lines { get; } = []; }", "LEVEE007")]
    [InlineData("Domain", "public sealed class Order : IAggregateRoot { public System.Collections.Immutable.ImmutableList<int> Lines { get; private set; } = []; }", null)]
    [InlineData("Application", "public sealed class Order : IAggregateRoot;", "LEVEE019")]
    public async Task Aggregates_are_guarded_through_their_bases_and_live_in_Domain(string role, string source, string? expected)
    {
        var output = await Compile(role, source, "App", MetadataReference.CreateFromFile(typeof(System.Collections.Immutable.ImmutableList).Assembly.Location));

        Assert.Equal(expected is null ? [] : new[] { expected }, output.Ids.Distinct());
    }

    [Fact]
    public async Task A_port_through_an_interface_of_its_own_is_still_a_port()
    {
        var output = await Compile("Application", "[Port] public interface IPrices;\npublic interface ILocalPrices : IPrices;\ninternal sealed class Prices : ILocalPrices;");

        Assert.Contains("LEVEE011", output.Ids);
    }

    [Theory]
    [InlineData("public sealed record ListNotes : IOperation<int>;", false)]
    [InlineData("public record ListNotes : IOperation<int>;", true)]
    public async Task Operations_are_sealed(string declaration, bool refused)
    {
        var output = await Compile("Contracts", $"[Operation(\"notes.list\", \"member\", Platform = true)] {declaration}");

        Assert.Equal(refused, output.Ids.Contains("LEVEE104"));
    }

    [Fact]
    public async Task A_tool_built_as_a_process_is_one()
    {
        var output = await Probe.CompileAsync(new Probe.Settings("Tool", Exe: true), "using Levee;\npublic static class Program { public static void Main() { } }", "App.Tool", Levee);

        Assert.DoesNotContain("LEVEE017", output.Ids);
    }
}
