namespace Levee.Analyzers.Tests;

public sealed class GeneratorTests
{
    private const string Contracts = """
        using Levee;
        namespace App.Contracts;
        public sealed record NoteView(string Text);
        [Operation("notes.create", "member", Effects = Effects.Write, Idempotency = Idempotency.None, Platform = true)]
        public sealed record CreateNote(string Text) : IOperation<NoteView>;
        """;

    private const string Application = """
        using System.Threading;
        using System.Threading.Tasks;
        using Levee;
        using App.Contracts;
        namespace App.Application;
        internal sealed class CreateNoteHandler : IOperationHandler<CreateNote, NoteView>
        {
            public Task<Result<NoteView>> HandleAsync(CreateNote operation, OperationContext context, CancellationToken ct) =>
                Task.FromResult<Result<NoteView>>(new NoteView(operation.Text));
        }
        """;

    private const string Host = """
        using Microsoft.Extensions.DependencyInjection;
        using Levee.Generated;
        namespace App.Host;
        public static class Program
        {
            public static IServiceCollection Compose(IServiceCollection services) => services.AddLeveeOperations();
        }
        """;

    internal static readonly Microsoft.CodeAnalysis.MetadataReference Di =
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location);

    internal static readonly Microsoft.CodeAnalysis.MetadataReference Configuration =
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Configuration.IConfiguration).Assembly.Location);

    internal static readonly Microsoft.CodeAnalysis.MetadataReference Levee =
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(IOperation).Assembly.Location);

    [Fact]
    public async Task A_host_catalogs_operations_and_handlers_from_the_assemblies_it_references()
    {
        var contracts = await Probe.CompileAsync("Contracts", Contracts, "App.Contracts", Levee);
        var application = await Probe.CompileAsync("Application", Application, "App.Application", Levee, Di, contracts.Reference());
        var host = await Probe.CompileAsync("Host", Host, "App.Host", Levee, Di, Configuration, contracts.Reference(), application.Reference());

        Assert.Empty(contracts.Errors);
        Assert.Empty(application.Errors);
        Assert.Empty(host.Errors);
        var composition = host.Generated("Levee.Composition.g.cs");
        Assert.Contains("""OperationDescriptor.Create<global::App.Contracts.CreateNote, global::App.Contracts.NoteView>("notes.create", "", "member", (global::Levee.Effects)2, (global::Levee.Idempotency)3, true, false)""", composition, StringComparison.Ordinal);
        Assert.Contains("global::Levee.Generated.LeveeRegistration_App_Application.Register(services, configuration);", composition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_composition_project_is_the_root_every_host_shares()
    {
        var contracts = await Probe.CompileAsync("Contracts", Contracts, "App.Contracts", Levee);
        var application = await Probe.CompileAsync("Application", Application, "App.Application", Levee, Di, contracts.Reference());
        var composition = await Probe.CompileAsync("Composition", "public static class Wiring;", "App.Composition", Levee, Di, Configuration,
            contracts.Reference(), application.Reference());
        var host = await Probe.CompileAsync("Host", Host, "App.Host", Levee, Di, Configuration, contracts.Reference(), application.Reference(),
            composition.Reference());

        Assert.Empty(composition.Errors);
        Assert.Contains("public static class LeveeComposition", composition.Generated("Levee.Composition.g.cs"), StringComparison.Ordinal);
        Assert.Empty(host.Errors);
        Assert.DoesNotContain(host.Compilation.SyntaxTrees, t => t.FilePath.EndsWith("Levee.Composition.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Internal_operations_and_the_apps_metadata_are_cataloged()
    {
        var output = await Probe.CompileAsync("Test", """
            using Levee;
            namespace App;
            public enum Reach { Inside, Outside }
            [OperationMetadata] public sealed class TurnAttribute(Reach reach) : System.Attribute { public Reach Reach { get; } = reach; public bool Pinned { get; set; } public string[] Tags { get; set; } = []; }
            [Operation("auth.sign_in", "anyone", Platform = true, Internal = true)]
            [Turn(Reach.Outside, Pinned = true, Tags = ["a"])]
            public sealed record SignIn : IOperation<int>;
            internal sealed class SignInHandler : IOperationHandler<SignIn, int>
            {
                public System.Threading.Tasks.Task<Result<int>> HandleAsync(SignIn operation, OperationContext context, System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.FromResult<Result<int>>(1);
            }
            """, "App.Tests", Levee, Di, Configuration);

        Assert.Empty(output.Errors);
        Assert.Contains("""(global::Levee.Idempotency)0, true, true, metadata: new global::System.Attribute[] { new global::App.TurnAttribute((global::App.Reach)1) { Pinned = true, Tags = new string[] { "a" } } })""",
            output.Generated("Levee.Composition.g.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_operation_without_a_handler_fails_the_host()
    {
        var contracts = await Probe.CompileAsync("Contracts", Contracts, "App.Contracts", Levee);
        var application = await Probe.CompileAsync("Application", "namespace App.Application;\npublic static class Nothing;", "App.Application", Levee, contracts.Reference());
        var host = await Probe.CompileAsync("Host", Host, "App.Host", Levee, Di, Configuration, contracts.Reference(), application.Reference());

        Assert.Contains("LEVEE101", host.Ids);
    }

    [Fact]
    public async Task Two_handlers_for_one_operation_fail_the_host()
    {
        var contracts = await Probe.CompileAsync("Contracts", Contracts, "App.Contracts", Levee);
        var one = await Probe.CompileAsync("Application", Application, "App.One", Levee, Di, contracts.Reference());
        var two = await Probe.CompileAsync("Application", Application, "App.Two", Levee, Di, contracts.Reference());
        var host = await Probe.CompileAsync("Host", Host, "App.Host", Levee, Di, Configuration, contracts.Reference(), one.Reference(), two.Reference());

        Assert.Contains("LEVEE102", host.Ids);
    }

    [Fact]
    public async Task Two_operations_with_one_name_fail_the_host()
    {
        var source = Contracts + """

            [Operation("notes.create", "member", Platform = true)]
            public sealed record CreateAnother : IOperation<NoteView>;
            """;
        var host = await Probe.CompileAsync("Test", source, "App.Tests", Levee, Di, Configuration);

        Assert.Contains("LEVEE103", host.Ids);
    }

    [Theory]
    [InlineData("""[Operation("notes.list", "member")] public sealed record ListNotes;""")]
    [InlineData("""public sealed record ListNotes : IOperation<int>;""")]
    [InlineData("""[Operation("Notes List", "member")] public sealed record ListNotes : IOperation<int>;""")]
    [InlineData("""[Operation("notes.list", " ")] public sealed record ListNotes : IOperation<int>;""")]
    [InlineData("""[Operation("notes.list", "member")] internal sealed record ListNotes : IOperation<int>;""")]
    public async Task A_malformed_operation_fails_where_it_is_declared(string declaration)
    {
        var output = await Probe.CompileAsync("Contracts", $"using Levee;\nnamespace App;\n{declaration}", "App", Levee);

        Assert.Contains("LEVEE104", output.Ids);
    }

    [Fact]
    public async Task An_operation_with_effects_says_whether_it_can_be_repeated()
    {
        var output = await Probe.CompileAsync("Contracts", """
            using Levee;
            namespace App;
            [Operation("notes.create", "member", Effects = Effects.Write, Platform = true)]
            public sealed record CreateNote : IOperation<int>;
            """, "App", Levee);

        Assert.Equal(["LEVEE105"], output.Ids);
    }

    [Theory]
    [InlineData("""[Operation("notes.list", "member")] public sealed record ListNotes : IOperation<int>;""", true)]
    [InlineData("""[Operation("notes.list", "member", Platform = true)] public sealed record ListNotes(string Tenant) : IOperation<int>, IScopedOperation { public string ScopeKey => Tenant; }""", true)]
    [InlineData("""[Operation("notes.list", "member")] public sealed record ListNotes(string Tenant) : IOperation<int>, IScopedOperation { public string ScopeKey => Tenant; }""", false)]
    [InlineData("""[Operation("notes.list", "member", Platform = true)] public sealed record ListNotes : IOperation<int>;""", false)]
    [InlineData("""[Operation("notes.list", "member", CallerScoped = true)] public sealed record ListNotes : IOperation<int>;""", false)]
    [InlineData("""[Operation("notes.list", "member", CallerScoped = true, Platform = true)] public sealed record ListNotes : IOperation<int>;""", true)]
    [InlineData("""[Operation("notes.list", "member", CallerScoped = true)] public sealed record ListNotes(string Tenant) : IOperation<int>, IScopedOperation { public string ScopeKey => Tenant; }""", true)]
    public async Task An_operation_is_scoped_or_declared_platform_wide(string declaration, bool refused)
    {
        var output = await Probe.CompileAsync("Contracts", $"using Levee;\nnamespace App;\n{declaration}", "App", Levee);

        Assert.Equal(refused ? ["LEVEE106"] : [], output.Ids);
    }

    [Fact]
    public async Task A_contracts_project_catalogs_what_its_clients_may_call()
    {
        static string Declares(string area) => $$"""
            using Levee;
            namespace App.{{area}};
            [Operation("{{area.ToLowerInvariant()}}.list", "member", Summary = "Lists.", Platform = true)]
            public sealed record List{{area}} : IOperation<int>;
            [Operation("{{area.ToLowerInvariant()}}.sign_in", "anyone", Platform = true, Internal = true)]
            public sealed record SignIn{{area}} : IOperation<int>;
            """;
        var notes = await Probe.CompileAsync("Contracts", Declares("Notes"), "App.Notes.Contracts", Levee);
        var billing = await Probe.CompileAsync("Contracts", Declares("Billing"), "App.Billing.Contracts", Levee);
        var client = await Probe.CompileAsync("Client", """
            namespace App.Cli;
            public static class Calls
            {
                public static global::Levee.OperationCatalog All => new(System.Linq.Enumerable.Concat(App.Notes.Contracts.LeveeCatalog.Catalog.All, App.Billing.Contracts.LeveeCatalog.Catalog.All));
            }
            """, "App.Cli", Levee, notes.Reference(), billing.Reference());

        Assert.Empty(notes.Errors);
        var generated = notes.Generated("Levee.Contracts.g.cs");
        Assert.Contains("namespace App.Notes.Contracts", generated, StringComparison.Ordinal);
        Assert.Contains("\"notes.list\"", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.sign_in", generated, StringComparison.Ordinal);
        Assert.Empty(client.Errors);
    }
}
