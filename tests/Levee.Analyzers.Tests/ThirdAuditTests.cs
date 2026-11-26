using Microsoft.CodeAnalysis;

namespace Levee.Analyzers.Tests;

/// <summary>What the third independent audit found: untyped inputs, contexts and connections, and scope SQL the rules missed.</summary>
public sealed class ThirdAuditTests
{
    private static Task<Probe.Output> Compile(string role, string source, params MetadataReference[] more) =>
        Probe.CompileAsync(new Probe.Settings(role, Exe: role == "Tool"), "using System;\nusing Levee;\n" + source, "App", [GeneratorTests.Levee, .. more]);

    private const string Ef = """
        namespace System.Data.Common { public abstract class DbConnection { } }
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContextOptions { }
            public class DbContext { public DbContext(DbContextOptions o) { } public int SaveChanges() => 0; public Infrastructure.DatabaseFacade Database => null!; }
            namespace Infrastructure { public class DatabaseFacade { } }
            public static class RelationalDatabaseFacadeExtensions { public static System.Data.Common.DbConnection GetDbConnection(this Infrastructure.DatabaseFacade d) => null!; }
        }
        public sealed class AppDb(Microsoft.EntityFrameworkCore.DbContextOptions o) : Microsoft.EntityFrameworkCore.DbContext(o);
        public static class Db { public static int ExecuteSqlRaw(string sql, params object[] args) => 0; }
        """;

    [Theory]
    [InlineData("public sealed record Note(System.Collections.ArrayList Items) : IOperation<int>;", true)]
    [InlineData("public sealed record Note(System.Collections.Hashtable Items) : IOperation<int>;", true)]
    [InlineData("public sealed record Note(System.Collections.IEnumerable Items) : IOperation<int>;", true)]
    [InlineData("public sealed record Note(Array Items) : IOperation<int>;", true)]
    [InlineData("public sealed record Note(System.Dynamic.ExpandoObject Bag) : IOperation<int>;", true)]
    [InlineData("public sealed record Note(string Text) : IOperation<int> { [System.Text.Json.Serialization.JsonInclude] public object? Extra; }", true)]
    [InlineData("public sealed record Note(Contact Who) : IOperation<int>;\n[System.Text.Json.Serialization.JsonDerivedType(typeof(Other))] public record Contact;\npublic sealed record Other(object Raw) : Contact;", true)]
    [InlineData("public sealed record Note(Child Who) : IOperation<int>;\npublic record Parent(object Raw);\npublic sealed record Child(object Raw, int N) : Parent(Raw);", true)]
    [InlineData("public sealed record Note(System.Collections.Generic.Dictionary<DateOnly, string> ByDay, int[] Counts) : IOperation<int>;", false)]
    public async Task Untyped_containers_and_included_fields_are_refused(string declaration, bool refused)
    {
        var output = await Compile("Contracts", $"[Operation(\"notes.create\", \"member\", Platform = true)] {declaration}");

        Assert.True(refused == output.Ids.Contains("LEVEE104"), string.Join("\n", output.Errors.Select(e => e.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Theory]
    [InlineData("Composition", "_ = new AppDb(new());", true)]
    [InlineData("Host", "_ = new AppDb(new());", true)]
    [InlineData("Tool", "_ = new AppDb(new());", false)]
    [InlineData("Adapter", "AppDb db = null!; _ = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(db.Database);", true)]
    [InlineData("Tool", "AppDb db = null!; _ = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(db.Database);", false)]
    public async Task Contexts_and_connections_of_its_own_are_a_tools(string role, string statement, bool refused)
    {
        var output = await Compile(role, Ef + $"public static class Seed {{ public static void Go() {{ {statement} }} public static void Main() {{ }} }}");

        Assert.True(refused == output.Ids.Contains("LEVEE018"), string.Join("\n", output.Errors.Select(e => e.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Theory]
    [InlineData("AppDb db = null!; Func<int> save = db.SaveChanges;", "LEVEE004")]
    [InlineData("Func<IDisposable> run = Levee.EntityFrameworkCore.LeveeOwner.Run;", "LEVEE018")]
    [InlineData("Func<Guid> id = Guid.CreateVersion7;", "LEVEE003")]
    public async Task A_method_group_is_judged_as_its_call(string statement, string expected)
    {
        var owner = await Probe.CompileAsync(new Probe.Settings(""), "namespace Levee.EntityFrameworkCore; public static class LeveeOwner { public static System.IDisposable Run() => null!; }",
            "Levee.EntityFrameworkCore");

        var output = await Compile("Adapter", Ef + $"public static class Later {{ public static void Go() {{ {statement} }} }}", owner.Reference());

        Assert.Contains(expected, output.Ids);
    }

    [Theory]
    [InlineData("""const string Setting = "levee.scope"; Db.ExecuteSqlRaw("select current_setting({0}, true)", Setting);""", false)]
    [InlineData("""Db.ExecuteSqlRaw("select current_setting({0}, true)", "levee.scope");""", false)]
    [InlineData("""Db.ExecuteSqlRaw("select set_config({0}, 'x', true)", "levee.scope");""", true)]
    [InlineData("""const string Setting = "levee.scope"; var t = "x"; Db.ExecuteSqlRaw($"select set_config('{Setting}', '{t}', true)");""", true)]
    [InlineData("""Db.ExecuteSqlRaw("set /* why not */ levee.scope = 'x'");""", true)]
    [InlineData("""Db.ExecuteSqlRaw("set -- a comment\n local levee.scope = 'x'");""", true)]
    [InlineData("""var which = "scope"; Db.ExecuteSqlRaw($"set local levee.{which} = 'x'");""", true)]
    [InlineData("""var which = "scope"; Db.ExecuteSqlRaw("set local levee." + which + " = 'x'");""", true)]
    public async Task Scope_settings_are_judged_by_what_the_SQL_does(string statement, bool refused)
    {
        var output = await Compile("Adapter", Ef + $"public static class Store {{ public static void Go() {{ {statement} }} }}");

        Assert.Equal(refused, output.Ids.Contains("LEVEE004"));
    }
}
