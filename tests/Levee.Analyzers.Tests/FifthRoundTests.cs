using Microsoft.CodeAnalysis;

namespace Levee.Analyzers.Tests;

/// <summary>What the second and third consumer trials found: false positives, holes, and generated code a strict build refuses.</summary>
public sealed class FifthRoundTests
{
    private static readonly MetadataReference Levee = GeneratorTests.Levee;

    private static Task<Probe.Output> Compile(string role, string source, string assembly = "App", params MetadataReference[] more) =>
        Probe.CompileAsync(role, "using System;\nusing Levee;\n" + source, assembly, [Levee, GeneratorTests.Di, GeneratorTests.Configuration, .. more]);

    [Fact]
    public async Task A_doc_comment_naming_an_IO_type_does_no_IO()
    {
        var output = await Probe.CompileAsync(new Probe.Settings("Application", Documented: true), """
            using Levee;
            namespace App;
            /// <summary>Reads like <see cref="System.IO.File"/>, through a port.</summary>
            public static class Reader;
            """, "App", Levee);

        Assert.DoesNotContain("LEVEE012", output.Ids);
    }

    [Theory]
    [InlineData("Microsoft.Extensions.Caching.Memory", true)]
    [InlineData("Microsoft.Extensions.Options", true)]
    [InlineData("Microsoft.*", true)]
    [InlineData("Microsoft.EntityFrameworkCore.Relational", true)]
    [InlineData("Npgsql", true)]
    [InlineData("Levee.EntityFrameworkCore", true)]
    [InlineData("NodaTime;Vogen*", false)]
    public async Task The_allow_list_admits_pure_libraries_never_frameworks(string allowed, bool refused)
    {
        var output = await Probe.CompileAsync(new Probe.Settings("Application", new Dictionary<string, string> { ["LeveeAllowedReferences"] = allowed }),
            "using Levee;\npublic static class Mine;", "App", Levee);

        Assert.Equal(refused, output.Ids.Contains("LEVEE008"));
    }

    [Fact]
    public async Task ObjectPool_is_pure()
    {
        var pool = await Probe.CompileAsync(new Probe.Settings(""), "public static class Pool;", "Microsoft.Extensions.ObjectPool");
        var output = await Compile("Domain", "public static class Mine { public static object Go() => typeof(Pool); }", "App", pool.Reference());

        Assert.Empty(output.Ids);
    }

    private const string Sql = """
        public static class Db { public static int ExecuteSqlRaw(string sql) => 0; }
        public sealed class Command { public Command(string cmdText) => CommandText = cmdText; public string CommandText { get; set; } }
        """;

    [Theory]
    [InlineData("Adapter", """Db.ExecuteSqlRaw("commit; begin");""", true)]
    [InlineData("Adapter", """Db.ExecuteSqlRaw($"update notes set at = {1}; COMMIT");""", true)]
    [InlineData("Adapter", """new Command("rollback");""", true)]
    [InlineData("Adapter", """new Command("select 1").CommandText = "start transaction";""", true)]
    [InlineData("Adapter", """Db.ExecuteSqlRaw("update notes set state = case when done then 1 else 0 end");""", false)]
    [InlineData("Adapter", """Db.ExecuteSqlRaw("do $$ begin perform 1; end $$");""", false)]
    [InlineData("Adapter", """Db.ExecuteSqlRaw("select 'commit'; -- commit");""", false)]
    [InlineData("Adapter", """Db.ExecuteSqlRaw("set local search_path to app");""", false)]
    [InlineData("Test", """Db.ExecuteSqlRaw("commit");""", false)]
    public async Task Raw_SQL_controls_no_transaction(string role, string statement, bool refused)
    {
        var output = await Compile(role, Sql + $"public static class Store {{ public static void Go() {{ {statement} }} }}");

        Assert.Equal(refused, output.Ids.Contains("LEVEE004"));
    }

    [Fact]
    public async Task Generated_code_is_documented_for_a_strict_build()
    {
        var contracts = await Probe.CompileAsync(new Probe.Settings("Contracts", Documented: true), """
            using Levee;
            namespace App;
            /// <summary>Lists.</summary>
            [Operation("notes.list", "member", Summary = "Lists.", Platform = true)]
            public sealed record ListNotes : IOperation<int>;
            """, "App.Contracts", Levee);
        var application = await Probe.CompileAsync(new Probe.Settings("Application", Documented: true), """
            using Levee;
            namespace App.Application;
            internal sealed class ListNotesHandler : IOperationHandler<App.ListNotes, int>
            {
                public System.Threading.Tasks.Task<Result<int>> HandleAsync(App.ListNotes operation, OperationContext context, System.Threading.CancellationToken ct) =>
                    System.Threading.Tasks.Task.FromResult<Result<int>>(1);
            }
            """, "App.Application", Levee, GeneratorTests.Di, contracts.Reference());
        var composition = await Probe.CompileAsync(new Probe.Settings("Composition", Documented: true), "namespace App.Composition;\n/// <summary>Wiring.</summary>\npublic static class Wiring;",
            "App.Composition", Levee, GeneratorTests.Di, GeneratorTests.Configuration, contracts.Reference(), application.Reference());

        foreach (var output in new[] { contracts, application, composition })
        {
            Assert.DoesNotContain(output.Compilation.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Id is "CS1591" or "CS1573");
        }
    }

    [Fact]
    public async Task An_operation_carrying_a_shared_marker_is_data_not_a_way_in()
    {
        var kernel = await Compile("Kernel", "namespace App.Kernel;\npublic interface ITenantScoped { string TenantId { get; } }", "App.Kernel");
        var application = await Compile("Application", """
            namespace App.Engine;
            [Operation("engine.read", "runtime", Internal = true)]
            public sealed record ReadCredentials(string TenantId) : IOperation<int>, App.Kernel.ITenantScoped, IScopedOperation
            {
                string IScopedOperation.ScopeKey => TenantId;
            }
            """, "App.Application", kernel.Reference());

        Assert.DoesNotContain("LEVEE016", application.Ids);
    }

    [Theory]
    [InlineData("Projection", "Contracts", false)]
    [InlineData("Projection", "Application", true)]
    [InlineData("Projection", "Adapter", true)]
    [InlineData("Host", "Projection", false)]
    [InlineData("Composition", "Projection", false)]
    [InlineData("Application", "Projection", true)]
    public async Task A_projection_sees_the_contract_and_only_hosts_see_it(string role, string references, bool refused)
    {
        var referenced = await Compile(references, "namespace Referenced;\npublic static class Thing;", "App.Referenced");
        var output = await Compile(role, "public static class Mine;", "App.Mine", referenced.Reference());

        Assert.Equal(refused, output.Ids.Contains("LEVEE002"));
    }

    [Theory]
    [InlineData("Adapter", "Infrastructure", false)]
    [InlineData("Infrastructure", "Kernel", false)]
    [InlineData("Infrastructure", "Application", true)]
    [InlineData("Infrastructure", "Contracts", true)]
    [InlineData("Application", "Infrastructure", true)]
    [InlineData("Tool", "Adapter", false)]
    [InlineData("Tool", "Composition", false)]
    [InlineData("Host", "Tool", true)]
    [InlineData("Composition", "Tool", true)]
    public async Task Infrastructure_serves_adapters_and_nothing_references_a_tool(string role, string references, bool refused)
    {
        var referenced = await Compile(references, "namespace Referenced;\npublic static class Thing;", "App.Referenced");
        var output = await Compile(role, "public static class Mine;", "App.Mine", referenced.Reference());

        Assert.Equal(refused, output.Ids.Contains("LEVEE002"));
    }

    [Fact]
    public async Task A_tool_is_a_process_that_owns_its_transaction()
    {
        const string Owner = """
            public static class Db { public static int ExecuteSqlRaw(string sql) => 0; }
            public static class Bootstrap { public static void Run() => Db.ExecuteSqlRaw("begin; create role app; commit"); }
            """;
        var library = await Compile("Tool", Owner, "App.Bootstrap");
        var process = await Probe.CompileAsync(new Probe.Settings("Tool"), "using Levee;\n" + Owner + "\npublic static class Program { public static void Main() => Bootstrap.Run(); }",
            "App.Bootstrap", Levee);
        var adapter = await Compile("Adapter", Owner, "App.Adapters");

        Assert.Contains("LEVEE017", library.Ids);
        Assert.DoesNotContain("LEVEE004", library.Ids);
        Assert.DoesNotContain("LEVEE004", process.Ids);
        Assert.Contains("LEVEE004", adapter.Ids);
    }

    [Theory]
    [InlineData("App.Kernel.IMigrator", true)]
    [InlineData("App.IOwnThing", false)]
    public async Task An_adapter_implements_ports_not_shared_interfaces(string contract, bool refused)
    {
        var kernel = await Compile("Kernel", "namespace App.Kernel;\npublic interface IMigrator { void Migrate(); }", "App.Kernel");
        var adapter = await Compile("Adapter", $$"""
            namespace App;
            internal interface IOwnThing { void Migrate(); }
            internal sealed class EfMigrator : {{contract}} { public void Migrate() { } }
            """, "App.Adapters", kernel.Reference());

        Assert.Equal(refused, adapter.Ids.Contains("LEVEE011"));
    }

    [Theory]
    [InlineData("Contracts", false)]
    [InlineData("Application", false)]
    [InlineData("Adapter", true)]
    [InlineData("Domain", true)]
    public async Task Operations_are_declared_in_contracts_or_the_application(string role, bool refused)
    {
        var output = await Compile(role, """[Operation("notes.list", "member", Platform = true)] public sealed record ListNotes : IOperation<int>;""");

        Assert.Equal(refused, output.Ids.Contains("LEVEE104"));
    }
}
