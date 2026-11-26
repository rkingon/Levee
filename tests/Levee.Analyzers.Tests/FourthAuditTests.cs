using Microsoft.CodeAnalysis;

namespace Levee.Analyzers.Tests;

/// <summary>What the fourth independent audit found: design-time contexts, EF's accessor, setting names outside reads, shared bases.</summary>
public sealed class FourthAuditTests
{
    private static Task<Probe.Output> Compile(string role, string source, string assembly = "App", params MetadataReference[] more) =>
        Probe.CompileAsync(new Probe.Settings(role, Exe: role == "Tool"), "using System;\nusing Levee;\n" + source, assembly, [GeneratorTests.Levee, .. more]);

    private const string Ef = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContextOptions { }
            public class DbContext { public DbContext(DbContextOptions o) { } }
            namespace Design { public interface IDesignTimeDbContextFactory<T> { T CreateDbContext(string[] args); } }
            namespace Infrastructure { public static class AccessorExtensions { public static T GetService<T>(this DbContext db) => default!; } }
        }
        public sealed class AppDb(Microsoft.EntityFrameworkCore.DbContextOptions o) : Microsoft.EntityFrameworkCore.DbContext(o);
        public sealed class Design : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AppDb> { public AppDb CreateDbContext(string[] args) => new(new()); }
        public static class Db { public static int ExecuteSqlRaw(string sql, params object[] args) => 0; }
        namespace Npgsql { public sealed class NpgsqlParameter(string name, object value); }
        """;

    [Theory]
    [InlineData("Adapter", "_ = new Design().CreateDbContext([]);", "LEVEE018")]
    [InlineData("Tool", "_ = new Design().CreateDbContext([]);", null)]
    [InlineData("Adapter", "AppDb db = null!; _ = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<object>(db);", "LEVEE005")]
    public async Task A_context_or_its_services_by_another_door(string role, string statement, string? expected)
    {
        var output = await Compile(role, Ef + $"public static class Seed {{ public static void Go() {{ {statement} }} public static void Main() {{ }} }}");

        Assert.Equal(expected is null ? [] : new[] { expected }, output.Ids.Where(i => i is "LEVEE005" or "LEVEE018").Distinct());
    }

    [Theory]
    [InlineData("""static readonly string Setting = "levee.scope"; static void Go() => Db.ExecuteSqlRaw("select set_config({0}, 'x', true)", Setting);""", true)]
    [InlineData("""static string Setting => "levee.scope"; static void Go() => Db.ExecuteSqlRaw("select set_config({0}, 'x', true)", Setting);""", true)]
    [InlineData("""static void Go() => Db.ExecuteSqlRaw("select set_config(@p, 'x', true)", new Npgsql.NpgsqlParameter("p", "levee.scope"));""", true)]
    [InlineData("""static void Go() => Db.ExecuteSqlRaw(string.Format("set {0} = 'b'", "levee.scope"));""", true)]
    [InlineData("""static void Go() => Db.ExecuteSqlRaw("update pg_settings set setting = 'b' where name = 'x'");""", true)]
    [InlineData("""const string Setting = "levee.scope"; static void Go() => Db.ExecuteSqlRaw("select current_setting({0}, true)", Setting);""", false)]
    [InlineData("""static void Go() { var key = "levee.scope"; Db.ExecuteSqlRaw("select current_setting({0}, true)", key); }""", false)]
    public async Task A_setting_name_is_allowed_only_where_it_is_read(string members, bool refused)
    {
        var output = await Compile("Adapter", Ef + $"public static class Store {{ {members} }}");

        Assert.Equal(refused, output.Ids.Contains("LEVEE004"));
    }

    [Fact]
    public async Task A_collection_of_the_apps_own_is_typed_by_its_items()
    {
        var output = await Compile("Contracts", """
            [Operation("notes.create", "member", Platform = true)] public sealed record Note(Bag Items) : IOperation<int>;
            public sealed class Bag : System.Collections.Generic.IEnumerable<object>
            {
                public System.Collections.Generic.IEnumerator<object> GetEnumerator() => null!;
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!;
            }
            """);

        Assert.Contains("LEVEE104", output.Ids);
    }

    [Theory]
    [InlineData("public abstract class Desk { public abstract void Next(); }", true)]
    [InlineData("public abstract class ValueObject { protected abstract System.Collections.Generic.IEnumerable<object> Parts(); }", false)]
    [InlineData("public abstract record Id(Guid Value);", false)]
    public async Task A_shared_abstract_base_is_a_way_in(string kernel, bool refused)
    {
        var shared = await Compile("Kernel", $"namespace App.Kernel;\n{kernel}", "App.Kernel");
        var derived = kernel.Contains("class Desk", StringComparison.Ordinal) ? "public override void Next() { }"
            : kernel.Contains("ValueObject", StringComparison.Ordinal) ? "protected override System.Collections.Generic.IEnumerable<object> Parts() => [];" : "";
        var baseName = kernel.Contains("Desk", StringComparison.Ordinal) ? "App.Kernel.Desk" : kernel.Contains("ValueObject", StringComparison.Ordinal) ? "App.Kernel.ValueObject" : "App.Kernel.Id(Guid.Empty)";
        var declaration = baseName.EndsWith(')')
            ? $"public sealed record Thing() : {baseName};"
            : $"public sealed class Thing : {baseName} {{ {derived} }}";
        var output = await Compile("Application", $"namespace App;\n{declaration}", "App.Application", shared.Reference());

        Assert.True(refused == output.Ids.Contains("LEVEE016"), string.Join("\n", output.Errors.Select(e => e.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));
    }
}
