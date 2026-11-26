using Microsoft.CodeAnalysis;

namespace Levee.Analyzers.Tests;

/// <summary>What the second independent audit found: spellings the first round's rules missed.</summary>
public sealed class SecondAuditTests
{
    private static readonly MetadataReference Levee = GeneratorTests.Levee;

    private static Task<Probe.Output> Compile(string role, string source, string assembly = "App", params MetadataReference[] more) =>
        Probe.CompileAsync(role, "using System;\nusing Levee;\n" + source, assembly, [Levee, .. more]);

    private const string Data = """
        namespace System.Data.Common
        {
            public abstract class DbConnection { public DbConnection CloneWith(string s) => this; }
            public abstract class DbProviderFactory { public DbConnection? CreateConnection() => null; }
        }
        namespace Npgsql { public sealed class NpgsqlConnection : System.Data.Common.DbConnection { public NpgsqlConnection() { } } }
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContextOptions { }
            public class DbContext { public DbContext(DbContextOptions o) { } }
            namespace Design { public interface IDesignTimeDbContextFactory<T> { T CreateDbContext(string[] args); } }
        }
        public sealed class AppDb(Microsoft.EntityFrameworkCore.DbContextOptions o) : Microsoft.EntityFrameworkCore.DbContext(o);
        public static class Db { public static int ExecuteSqlRaw(string sql) => 0; }
        """;

    [Theory]
    [InlineData("""Db.ExecuteSqlRaw("select set_config('levee." + "scope', 'x', true)");""", "LEVEE004")]
    [InlineData("""Db.ExecuteSqlRaw("select set_config('levee.plat' || 'form', 'on', true)");""", "LEVEE004")]
    [InlineData("""Db.ExecuteSqlRaw("set local \"levee\".\"scope\" = 'x'");""", "LEVEE004")]
    [InlineData("""Db.ExecuteSqlRaw("select set_config(concat('levee', '.scope'), 'x', true)");""", "LEVEE004")]
    [InlineData("""Db.ExecuteSqlRaw("select levee_audit_write(gen_random_uuid(), 'acme', 'ceo', 'x', '{}')");""", "LEVEE004")]
    [InlineData("""Db.ExecuteSqlRaw("set role postgres");""", "LEVEE004")]
    [InlineData("""Db.ExecuteSqlRaw("select current_setting('levee.scope', true)");""", null)]
    [InlineData("""throw new InvalidOperationException("levee.scope is missing; set it through an operation");""", null)]
    [InlineData("""_ = new AppDb(new());""", "LEVEE018")]
    [InlineData("""System.Data.Common.DbProviderFactory f = null!; f.CreateConnection();""", "LEVEE018")]
    [InlineData("""System.Data.Common.DbConnection c = null!; c.CloneWith("x");""", "LEVEE018")]
    [InlineData("""_ = Activator.CreateInstance<Npgsql.NpgsqlConnection>();""", "LEVEE018")]
    public async Task The_scope_the_audit_and_connections_have_no_other_spelling(string statement, string? expected)
    {
        var output = await Compile("Adapter", Data + $"public static class Store {{ public static void Go() {{ {statement} }} }}");

        Assert.Equal(expected is null ? [] : new[] { expected }, output.Ids.Where(i => i is "LEVEE004" or "LEVEE018").Distinct());
    }

    [Fact]
    public async Task EFs_design_time_factory_may_make_its_context()
    {
        var output = await Compile("Adapter", Data + """
            internal sealed class Design : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AppDb>
            {
                public AppDb CreateDbContext(string[] args) => new(new());
            }
            """);

        Assert.DoesNotContain("LEVEE018", output.Ids);
    }

    [Theory]
    [InlineData("System.IO.Compression.ZipFile.OpenRead(\"x.zip\")")]
    [InlineData("System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(\"x.pem\")")]
    [InlineData("new System.Xml.XmlTextReader(\"https://example.com/x.xml\")")]
    [InlineData("System.Reflection.Assembly.LoadFrom(\"x.dll\")")]
    public async Task The_core_does_no_IO_by_these_doors_either(string expression)
    {
        var output = await Compile("Application", $"public static class Probe {{ public static object Go() => {expression}; }}", "App",
            MetadataReference.CreateFromFile(typeof(System.IO.Compression.ZipFile).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.IO.Compression.ZipArchive).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Xml.XmlTextReader).Assembly.Location));

        Assert.Contains("LEVEE012", output.Ids);
    }

    [Fact]
    public async Task Native_code_in_the_core_is_IO()
    {
        var output = await Compile("Application", """public static partial class Native { [System.Runtime.InteropServices.DllImport("libc")] public static extern int getpid(); }""");

        Assert.Contains("LEVEE012", output.Ids);
    }

    [Fact]
    public async Task A_method_group_to_the_container_is_service_location()
    {
        var output = await Compile("Adapter", "public static class Locator { public static Func<Type, object?> Go(IServiceProvider services) => services.GetService; }");

        Assert.Contains("LEVEE005", output.Ids);
    }

    [Theory]
    [InlineData("Vendor.IHealthCheck", true)]
    [InlineData("IEquatable<Probe>", false)]
    public async Task Application_implements_no_packages_interface(string contract, bool refused)
    {
        var vendor = await Probe.CompileAsync(new Probe.Settings(""), "namespace Vendor; public interface IHealthCheck { void Check(); }", "Vendor.Abstractions");
        var output = await Compile("Application", $"public sealed class Probe : {contract} {{ public void Check() {{ }} public bool Equals(Probe? other) => true; }}", "App",
            vendor.Reference());

        Assert.Equal(refused, output.Ids.Contains("LEVEE016"));
    }

    [Theory]
    [InlineData("Domain", "public record struct Order : IAggregateRoot { public int State { get; set; } }", "LEVEE007")]
    [InlineData("Application", "public record struct Order : IAggregateRoot;", "LEVEE019")]
    [InlineData("Domain", "public sealed class Order : IAggregateRoot { private readonly System.Collections.Generic.List<int> _lines = []; public System.Collections.Generic.List<int> Lines() => _lines; }", "LEVEE007")]
    public async Task Aggregate_structs_and_handed_out_collections_are_guarded(string role, string source, string expected)
    {
        var output = await Compile(role, source);

        Assert.Contains(expected, output.Ids);
    }

    [Theory]
    [InlineData("Composition", """_ = OperationDescriptor.Create<Ping, int>("ping", "", "x", Effects.Read, Idempotency.Safe, platform: true);""", true)]
    [InlineData("Composition", """_ = new OperationCatalog([]);""", true)]
    [InlineData("Test", """_ = new OperationCatalog([]);""", false)]
    public async Task The_catalog_is_built_only_by_generated_code(string role, string statement, bool refused)
    {
        var output = await Compile(role, $$"""
            [Operation("ping", "x", Platform = true)] public sealed record Ping : IOperation<int>;
            internal sealed class PingHandler : IOperationHandler<Ping, int>
            {
                public System.Threading.Tasks.Task<Result<int>> HandleAsync(Ping operation, OperationContext context, System.Threading.CancellationToken ct) =>
                    System.Threading.Tasks.Task.FromResult<Result<int>>(1);
            }
            public static class Hand { public static void Go() { {{statement}} } }
            """, "App", GeneratorTests.Di, GeneratorTests.Configuration);

        Assert.Equal(refused, output.Ids.Contains("LEVEE020"));
    }

    [Theory]
    [InlineData("public sealed record Note(object Payload) : IOperation<int>;", true)]
    [InlineData("public sealed record Note(Inner Body) : IOperation<int>;\npublic sealed record Inner(System.Text.Json.JsonElement Raw);", true)]
    [InlineData("public sealed record Note(System.Collections.Generic.IReadOnlyList<Inner> Body) : IOperation<int>;\npublic sealed record Inner(string Text);", false)]
    public async Task An_inputs_members_are_typed_all_the_way_down(string declaration, bool refused)
    {
        var output = await Compile("Contracts", $"[Operation(\"notes.create\", \"member\", Platform = true)] {declaration}");

        Assert.True(refused == output.Ids.Contains("LEVEE104"), string.Join("\n", output.Errors.Select(e => e.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Theory]
    [InlineData("Adapter", true)]
    [InlineData("Tool", false)]
    public async Task Only_a_tool_steps_outside_the_guard(string role, bool refused)
    {
        var owner = await Probe.CompileAsync(new Probe.Settings(""), "namespace Levee.EntityFrameworkCore; public static class LeveeOwner { public static System.IDisposable Run() => null!; }",
            "Levee.EntityFrameworkCore");
        var output = await Probe.CompileAsync(new Probe.Settings(role, Exe: role == "Tool"),
            "using Levee;\npublic static class Seed { public static void Go() { using var _ = Levee.EntityFrameworkCore.LeveeOwner.Run(); } public static void Main() { } }",
            "App", Levee, owner.Reference());

        Assert.Equal(refused, output.Ids.Contains("LEVEE018"));
    }
}
