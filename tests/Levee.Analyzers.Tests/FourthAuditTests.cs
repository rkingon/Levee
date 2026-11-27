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
    [InlineData("Adapter", "AppDb db = null!; _ = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<object>(db);", "LEVEE018")]
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
    [InlineData("public abstract class Desk { public abstract void Next(); }", "public override void Next() { }", true)]
    [InlineData("public abstract class Desk { public void Next() => NextCore(); protected abstract void NextCore(); }", "protected override void NextCore() { }", true)]
    [InlineData("public class Desk { public virtual void Next() { } }", "public override void Next() { }", true)]
    [InlineData("public abstract class ValueObject { protected abstract System.Collections.Generic.IEnumerable<object> GetEqualityComponents(); }", "protected override System.Collections.Generic.IEnumerable<object> GetEqualityComponents() => [];", false)]
    public async Task A_shared_base_with_anything_to_override_is_a_way_in(string kernel, string member, bool refused)
    {
        var shared = await Compile("Kernel", $"namespace App.Kernel;\n{kernel}", "App.Kernel");
        var name = kernel.Contains("ValueObject", StringComparison.Ordinal) ? "ValueObject" : "Desk";
        var output = await Compile("Application", $"namespace App;\npublic sealed class Thing : App.Kernel.{name} {{ {member} }}", "App.Application", shared.Reference());

        Assert.True(refused == output.Ids.Contains("LEVEE016"), string.Join("\n", output.Errors.Select(e => e.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task A_records_own_value_semantics_are_no_way_in()
    {
        var shared = await Compile("Kernel", "namespace App.Kernel;\npublic abstract record Id(Guid Value);", "App.Kernel");
        var output = await Compile("Domain", "namespace App;\npublic sealed record OrderId(Guid Value) : App.Kernel.Id(Value);", "App.Domain", shared.Reference());

        Assert.DoesNotContain("LEVEE016", output.Ids);
    }
}

/// <summary>What the fifth independent audit found.</summary>
public sealed class FifthAuditTests
{
    private static Task<Probe.Output> Compile(string role, string source) =>
        Probe.CompileAsync(new Probe.Settings(role, Exe: role is "Tool" or "Host"), "using System;\nusing Levee;\n" + source, "App", GeneratorTests.Levee);

    [Theory]
    [InlineData("Host", true)]
    [InlineData("Composition", true)]
    [InlineData("Tool", false)]
    public async Task EFs_accessor_is_a_connection_of_its_own_outside_a_tool(string role, bool refused)
    {
        var output = await Compile(role, """
            namespace Microsoft.EntityFrameworkCore
            {
                public class DbContext { }
                namespace Infrastructure { public static class AccessorExtensions { public static T GetService<T>(this DbContext db) => default!; } }
            }
            public static class Seed { public static void Go(Microsoft.EntityFrameworkCore.DbContext db) { _ = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<object>(db); } public static void Main() { } }
            """);

        Assert.Equal(refused, output.Ids.Contains("LEVEE018"));
    }

    [Theory]
    [InlineData("public sealed record Watch(string Topic, [property: Secret] string Key) : ISubscription<int>;", true)]
    [InlineData("public sealed record Watch(Auth Auth) : ISubscription<int>;\npublic sealed record Auth([property: Secret] string Key);", true)]
    [InlineData("public sealed record Watch(string Topic) : ISubscription<int>;", false)]
    public async Task A_subscriptions_input_holds_no_secret(string declaration, bool refused)
    {
        var output = await Compile("Contracts", $"[Operation(\"topics.watch\", \"member\", Platform = true)] {declaration}");

        Assert.Equal(refused, output.Ids.Contains("LEVEE104"));
    }

    [Fact]
    public async Task A_collection_of_the_apps_own_with_no_item_type_is_untyped()
    {
        var output = await Compile("Contracts", """
            [Operation("notes.create", "member", Platform = true)] public sealed record Note(Bag Items) : IOperation<int>;
            public sealed class Bag : System.Collections.IEnumerable { public System.Collections.IEnumerator GetEnumerator() => null!; }
            """);

        Assert.Contains("LEVEE104", output.Ids);
    }
}

/// <summary>What the sixth independent audit found.</summary>
public sealed class SixthAuditTests
{
    private static Task<Probe.Output> Compile(string role, string source, string assembly = "App", params MetadataReference[] more) =>
        Probe.CompileAsync(new Probe.Settings(role, Exe: role is "Tool" or "Host"), "using System;\nusing Levee;\n" + source, assembly, [GeneratorTests.Levee, .. more]);

    private const string Data = """
        namespace Microsoft.AspNetCore.Routing { public interface IEndpointRouteBuilder { } }
        public static class Routes
        {
            public static void MapGet(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, string pattern, Delegate handler) { }
            public static void MapValues(this object source, Delegate handler) { }
        }
        namespace System.Data.Common { public abstract class DbConnection { } public abstract class DbDataSource { } }
        namespace Npgsql
        {
            public sealed class NpgsqlConnection : System.Data.Common.DbConnection { }
            public sealed class NpgsqlDataSource : System.Data.Common.DbDataSource { }
        }
        """;

    [Theory]
    [InlineData("Adapter", "public sealed class Side(Npgsql.NpgsqlConnection connection) { }", true)]
    [InlineData("Adapter", "public sealed class Side(Lazy<Npgsql.NpgsqlConnection> connection) { }", true)]
    [InlineData("Host", "public sealed class Side(Npgsql.NpgsqlDataSource source) { }", true)]
    [InlineData("Host", "public static class Endpoints { public static void Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app) => app.MapGet(\"/x\", (Npgsql.NpgsqlConnection c) => 0); }", true)]
    [InlineData("Host", "public static class Endpoints { public static void Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app) => app.MapGet(\"/x\", Count); static int Count(Npgsql.NpgsqlConnection c) => 0; }", true)]
    [InlineData("Adapter", "public static class Values { public static void Go(object source) => source.MapValues((System.Data.Common.DbConnection c) => 0); }", false)]
    [InlineData("Adapter", "public static class Helper { public static int Run(Func<System.Data.Common.DbConnection, int> work) => work(null!); public static int Go() => Run(c => 0); }", false)]
    [InlineData("Host", "public static class Side { public static object? Go(IServiceProvider sp) => sp.GetService(typeof(Npgsql.NpgsqlConnection)); }", true)]
    [InlineData("Host", "public static class Side { public static object? Go(IServiceProvider sp) => sp.GetService(typeof(Npgsql.NpgsqlDataSource)); }", false)]
    [InlineData("Adapter", "public sealed record Tx(System.Data.Common.DbConnection Connection);", false)]
    [InlineData("Adapter", "public sealed class Ledger { private System.Data.Common.DbConnection? Connection => null; }", false)]
    [InlineData("Tool", "public sealed class Side(Npgsql.NpgsqlConnection connection) { }", false)]
    public async Task A_connection_the_container_hands_over_is_one_of_its_own(string role, string declaration, bool refused)
    {
        var output = await Compile(role, Data + declaration + "\npublic static class Entry { public static void Main() { } }");

        Assert.True(refused == output.Ids.Contains("LEVEE018"), string.Join("\n", output.Errors.Select(e => e.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task A_shared_bases_indexer_is_a_way_in()
    {
        var shared = await Compile("Kernel", "namespace App.Kernel;\npublic abstract class Lookup { public abstract string this[string key] { get; } }", "App.Kernel");
        var output = await Compile("Application", "namespace App;\npublic sealed class Thing : App.Kernel.Lookup { public override string this[string key] => key; }", "App.Application", shared.Reference());

        Assert.Contains("LEVEE016", output.Ids);
    }
}
