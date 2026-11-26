using Microsoft.CodeAnalysis;

namespace Levee.Analyzers.Tests;

/// <summary>What the first consumer trial found, held to.</summary>
public sealed class ConsumerTests
{
    private static readonly MetadataReference Levee = MetadataReference.CreateFromFile(typeof(IOperation).Assembly.Location);
    private static readonly MetadataReference Di = MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location);
    private static readonly MetadataReference Configuration = MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Configuration.IConfiguration).Assembly.Location);

    private static Task<Probe.Output> Compile(string role, string source, string assembly, params MetadataReference[] more) =>
        Probe.CompileAsync(role, "using System;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing System.Collections.Generic;\nusing Levee;\n" + source,
            assembly, [Levee, Di, Configuration, .. more]);

    [Fact]
    public async Task A_result_may_be_nullable()
    {
        var output = await Compile("Test", """
            namespace App;
            public sealed record View(string Text);
            [Operation("views.find", "member", Platform = true)] public sealed record Find : IOperation<View?>;
            [Operation("views.all", "member", Platform = true)] public sealed record All : IOperation<IReadOnlyList<View>?>;
            internal sealed class FindHandler : IOperationHandler<Find, View?>
            {
                public Task<Result<View?>> HandleAsync(Find operation, OperationContext context, CancellationToken ct) => Task.FromResult(Result<View?>.Success(null));
            }
            internal sealed class AllHandler : IOperationHandler<All, IReadOnlyList<View>?>
            {
                public Task<Result<IReadOnlyList<View>?>> HandleAsync(All operation, OperationContext context, CancellationToken ct) => Task.FromResult(Result<IReadOnlyList<View>?>.Success(null));
            }
            """, "App.Tests");

        Assert.Empty(output.Errors);
    }

    [Fact]
    public async Task A_host_that_only_calls_the_api_composes_nothing()
    {
        var contracts = await Compile("Contracts", """
            namespace App;
            [Operation("notes.list", "member", Platform = true)] public sealed record ListNotes : IOperation<int>;
            """, "App.Contracts");
        var host = await Compile("Host", "public static class Program;", "App.Web", contracts.Reference());

        Assert.Empty(host.Errors);
        Assert.DoesNotContain(host.Compilation.SyntaxTrees, t => t.FilePath.EndsWith("Levee.Composition.g.cs", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""[Operation("activity.watch", "member", Platform = true)]""", false)]
    [InlineData("""[Operation("activity.watch", "member", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.None)]""", true)]
    public async Task A_subscription_only_reads(string attribute, bool refused)
    {
        var output = await Compile("Test", $$"""
            namespace App;
            public sealed record Signal(string Kind);
            {{attribute}} public sealed record Watch : ISubscription<Signal>;
            internal sealed class WatchHandler : ISubscriptionHandler<Watch, Signal>
            {
                public async IAsyncEnumerable<Signal> SubscribeAsync(Watch subscription, SubscriptionContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
                {
                    await Task.Yield();
                    yield return new Signal("turn");
                }
            }
            """, "App.Tests");

        Assert.Equal(refused, output.Ids.Contains("LEVEE104"));
        if (!refused)
        {
            Assert.Empty(output.Errors);
            Assert.Contains("CreateSubscription<global::App.Watch, global::App.Signal>", output.Generated("Levee.Composition.g.cs"), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("public sealed class Live { }", true)]
    [InlineData("public static class Wiring { public static int Count => 1; }", true)]
    [InlineData("public static class Wiring { public static void Run() { } }", true)]
    [InlineData("public static class Wiring { public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddApp(this Microsoft.Extensions.DependencyInjection.IServiceCollection s) => s; }", false)]
    [InlineData("internal sealed class Live { }", false)]
    public async Task Composition_exposes_only_registration(string declaration, bool refused)
    {
        var output = await Compile("Composition", declaration, "App.Composition");

        Assert.Equal(refused, output.Ids.Contains("LEVEE014"));
    }

    [Theory]
    [InlineData("Application", "factory.BeginAsync(new WorkScope(null, false), CancellationToken.None)", true)]
    [InlineData("Host", "factory.BeginAsync(new WorkScope(null, false), CancellationToken.None)", true)]
    [InlineData("Test", "factory.BeginAsync(new WorkScope(null, false), CancellationToken.None)", false)]
    public async Task Only_the_dispatcher_opens_a_unit_of_work(string role, string call, bool refused)
    {
        var output = await Compile(role, $"public static class Bridge {{ public static Task<IUnitOfWork> Open(IUnitOfWorkFactory factory) => {call}; }}", "App");

        Assert.Equal(refused, output.Ids.Contains("LEVEE004"));
    }

    [Fact]
    public async Task Only_the_dispatcher_commits()
    {
        var output = await Compile("Adapter", "public static class Bridge { public static Task Commit(IUnitOfWork unit) => unit.CommitAsync(CancellationToken.None); }", "App");

        Assert.Contains("LEVEE004", output.Ids);
    }

    [Fact]
    public async Task Hosting_is_compositions()
    {
        var hosting = await Probe.CompileAsync(new Probe.Settings(""), "namespace Microsoft.Extensions.Hosting { public interface IHostApplicationLifetime; }",
            "Microsoft.Extensions.Hosting.Abstractions");
        var output = await Compile("Application", "public static class Loop;", "App", hosting.Reference());

        var hit = Assert.Single(output.Errors, e => e.Id == "LEVEE008");
        Assert.Contains("hosting is composition's", hit.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}

/// <summary>What the second consumer trial found, held to.</summary>
public sealed class SecondRoundTests
{
    private static readonly MetadataReference Levee = MetadataReference.CreateFromFile(typeof(IOperation).Assembly.Location);
    private static readonly MetadataReference Di = MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location);
    private static readonly MetadataReference Configuration = MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Configuration.IConfiguration).Assembly.Location);

    private static Task<Probe.Output> Compile(string role, string source, string assembly, params MetadataReference[] more) =>
        Probe.CompileAsync(role, "using System;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing Levee;\n" + source,
            assembly, [Levee, Di, Configuration, .. more]);

    private const string Operation = """
        namespace App;
        [Operation("notes.list", "member", Platform = true)] public sealed record ListNotes : IOperation<int>;
        internal sealed class ListNotesHandler : IOperationHandler<ListNotes, int>
        {
            public Task<Result<int>> HandleAsync(ListNotes operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<int>>(1);
        }
        """;

    [Fact]
    public async Task A_test_adds_its_own_operations_on_top_of_the_apps_composition()
    {
        var application = await Compile("Application", Operation, "App.Application");
        var composition = await Compile("Composition", "public static class Nothing;", "App.Composition", application.Reference());
        var test = await Compile("Test", """
            namespace App.Tests;
            [Operation("test.pay", "member", Platform = true, Effects = Effects.External, Idempotency = Idempotency.Conflicts)] public sealed record Pay : IOperation<int>;
            internal sealed class PayHandler : IOperationHandler<Pay, int>
            {
                public Task<Result<int>> HandleAsync(Pay operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<int>>(1);
            }
            """, "App.Tests", application.Reference(), composition.Reference());

        Assert.Empty(test.Errors);
        var part = test.Generated("Levee.TestComposition.g.cs");
        Assert.Contains("\"test.pay\"", part, StringComparison.Ordinal);
        Assert.DoesNotContain("\"notes.list\"", part, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_routed_set_of_singletons_is_a_singleton()
    {
        var application = await Compile("Application", "namespace App;\n[Port(Many = true)] public interface ILauncher;", "App.Application");
        var adapters = await Compile("Adapter", """
            using App;
            [Adapter("docker", Lifetime = AdapterLifetime.Singleton)] internal sealed class Docker : ILauncher;
            [Adapter("ecs", Lifetime = AdapterLifetime.Singleton)] internal sealed class Ecs : ILauncher;
            """, "App.Adapters", application.Reference());
        var host = await Compile("Host", "public static class Program;", "App.Host", application.Reference(), adapters.Reference());

        Assert.Empty(host.Errors);
        Assert.Contains("AddSingleton<global::Levee.IAdapters<global::App.ILauncher>>", host.Generated("Levee.Composition.g.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Composition_hands_out_none_of_the_applications_types()
    {
        var application = await Compile("Application", "namespace App;\npublic sealed class TicketSteps;", "App.Application");
        var output = await Compile("Composition", """
            public static class Wiring
            {
                public static App.TicketSteps Steps(this IServiceProvider services) => new();
            }
            """, "App.Composition", application.Reference());

        Assert.Contains("LEVEE014", output.Ids);
    }

    [Fact]
    public async Task A_host_reaches_nothing_of_the_applications_through_var()
    {
        var application = await Compile("Application", "namespace App;\npublic sealed class TicketSteps { public void Report() { } }", "App.Application");
        var kernel = await Compile("Kernel", "public static class Smuggle { public static object Steps() => new(); }", "App.Kernel");
        var composition = await Compile("Composition", """
            public static class Wiring
            {
                public static App.TicketSteps Steps(this IServiceProvider services) => new();
            }
            """, "App.Composition", application.Reference());
        var host = await Compile("Host", """
            public static class Program
            {
                public static void Run(IServiceProvider services)
                {
                    var steps = services.Steps();
                    steps.Report();
                }
            }
            """, "App.Host", application.Reference(), composition.Reference(), kernel.Reference());

        Assert.Contains("LEVEE013", host.Ids);
    }

    [Theory]
    [InlineData("App.Rules.Decide()", true)]
    [InlineData("new App.Settings(3)", false)]
    [InlineData("(System.Func<int>)App.Rules.Decide", true)]
    public async Task Composition_wires_the_application_and_never_drives_it(string expression, bool refused)
    {
        var application = await Compile("Application", "namespace App;\npublic static class Rules { public static int Decide() => 1; }\npublic sealed record Settings(int Limit);",
            "App.Application");
        var output = await Compile("Composition", $"internal static class Wiring {{ public static object Go() => {expression}; }}", "App.Composition", application.Reference());

        Assert.Equal(refused, output.Ids.Contains("LEVEE015"));
    }

    [Theory]
    [InlineData("App.Kernel.IWorkDesk", true)]
    [InlineData("App.IOwnDesk", false)]
    public async Task Application_implements_nothing_of_Kernels_that_would_hand_it_out(string contract, bool refused)
    {
        var kernel = await Compile("Kernel", "namespace App.Kernel;\npublic interface IWorkDesk { int Next(); }", "App.Kernel");
        var application = await Compile("Application", $$"""
            namespace App;
            public interface IOwnDesk { int Next(); }
            internal sealed class JobBroker : {{contract}} { public int Next() => 1; }
            """, "App.Application", kernel.Reference());

        Assert.Equal(refused, application.Ids.Contains("LEVEE016"));
    }

    [Fact]
    public async Task A_host_reaches_no_application_method_through_a_delegate()
    {
        var application = await Compile("Application", "namespace App;\npublic static class Rules { public static int Decide() => 1; }", "App.Application");
        var host = await Compile("Host", """
            public static class Program
            {
                public static int Run() { System.Func<int> f = App.Rules.Decide; return f(); }
            }
            """, "App.Host", application.Reference());

        Assert.Contains("LEVEE013", host.Ids);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("[Adapter]", true)]
    public async Task A_test_composes_only_the_fakes_it_marks_as_adapters(string marked, bool composed)
    {
        var application = await Compile("Application", "using Levee;\nnamespace App;\n[Port] public interface IEmbedder { int Embed(); }", "App.Application");
        var test = await Compile("Test", $"using Levee;\nnamespace App.Tests;\n{marked} internal sealed class FakeEmbedder : App.IEmbedder {{ public int Embed() => 1; }}",
            "App.Tests", application.Reference());

        Assert.Equal(composed, test.Generated("Levee.Assembly.g.cs").Contains("FakeEmbedder", StringComparison.Ordinal));
    }
}
