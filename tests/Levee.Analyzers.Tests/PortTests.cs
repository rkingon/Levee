using Microsoft.CodeAnalysis;

namespace Levee.Analyzers.Tests;

public sealed class PortTests
{
    private static readonly MetadataReference Levee = MetadataReference.CreateFromFile(typeof(IOperation).Assembly.Location);
    private static readonly MetadataReference Di = MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location);
    private static readonly MetadataReference Configuration = MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Configuration.IConfiguration).Assembly.Location);

    private const string Usings = "using System;\nusing Levee;\n";

    private static Task<Probe.Output> Compile(string role, string source, string assembly, params MetadataReference[] more) =>
        Probe.CompileAsync(role, Usings + source, assembly, [Levee, Di, Configuration, .. more]);

    /// <summary>An Application declaring <paramref name="ports"/>, adapters over it, and a Host over both.</summary>
    private static async Task<Probe.Output> Host(string ports, params string[] adapters)
    {
        var application = await Compile("Application", "namespace App;\n" + ports, "App.Application");
        var references = new List<MetadataReference> { application.Reference() };
        for (var i = 0; i < adapters.Length; i++)
        {
            var adapter = await Compile("Adapter", "using App;\nnamespace App.Adapters;\n" + adapters[i], $"App.Adapters{i}", application.Reference());
            Assert.Empty(adapter.Errors);
            references.Add(adapter.Reference());
        }
        return await Compile("Host", "public static class Program;", "App.Host", [.. references]);
    }

    [Fact]
    public async Task A_port_with_one_adapter_is_bound_to_it()
    {
        var host = await Host("public interface INotes : IRepository<Note>; public sealed class Note : IAggregateRoot;",
            "internal sealed class EfNotes : INotes;");

        Assert.Empty(host.Errors);
        Assert.Contains("global::Levee.Generated.LeveeRegistration_App_Adapters0.Register(services, configuration);", host.Generated("Levee.Composition.g.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_port_chosen_by_a_setting_is_bound_at_startup()
    {
        var host = await Host("""[Port(SelectedBy = "Providers:Prices")] public interface IPrices;""",
            """[Adapter("s3")] internal sealed class S3Prices : IPrices;""",
            """[Adapter("embedded")] internal sealed class EmbeddedPrices : IPrices;""");

        Assert.Empty(host.Errors);
        Assert.Contains("""global::Levee.PortSelection.Select(configuration, "Providers:Prices", "App.IPrices", new[] { "embedded", "s3" })""",
            host.Generated("Levee.Composition.g.cs"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public interface IPrices;", "LEVEE107")]
    [InlineData("[Port] public interface IPrices;", "LEVEE107", "internal sealed class Unrelated;")]
    [InlineData("[Port] public interface IPrices;", "LEVEE108", "internal sealed class A : IPrices;", "internal sealed class B : IPrices;")]
    [InlineData("[Port] public interface IPrices;", "LEVEE108", """[Adapter("a")] internal sealed class A : IPrices;""")]
    [InlineData("""[Port(SelectedBy = "P")] public interface IPrices;""", "LEVEE108", "internal sealed class A : IPrices;")]
    [InlineData("""[Port(SelectedBy = "P")] public interface IPrices;""", "LEVEE108", """[Adapter("a")] internal sealed class A : IPrices;""", """[Adapter("a")] internal sealed class B : IPrices;""")]
    public async Task A_host_binds_every_port_one_way(string ports, string? expected, params string[] adapters)
    {
        var host = await Host(ports.StartsWith("public interface", StringComparison.Ordinal) ? "[Port] " + ports : ports, adapters);

        Assert.Contains(expected, host.Ids);
    }

    [Fact]
    public async Task A_catch_that_classifies_a_ports_failure_does_no_IO()
    {
        var output = await Compile("Application", """
            public static class Search
            {
                public static string Classify(Func<string> port)
                {
                    try { return port(); }
                    catch (System.Net.Http.HttpRequestException) { return "unavailable"; }
                }
            }
            """, "App");

        Assert.DoesNotContain("LEVEE012", output.Ids);
    }

    [Theory]
    [InlineData("[Port(Many = true)] public interface IChannels;", null, "internal sealed class A : IChannels;", "internal sealed class B : IChannels;")]
    [InlineData("[Port(Many = true)] public interface IChannels;", null)]
    [InlineData("""[Port(Many = true, SelectedBy = "X")] public interface IChannels;""", "LEVEE108", "internal sealed class A : IChannels;")]
    [InlineData("[Port(Many = true)] public interface IChannels;", null, """[Adapter("a")] internal sealed class A : IChannels;""")]
    [InlineData("[Port(Many = true)] public interface IChannels;", "LEVEE108", """[Adapter("a")] internal sealed class A : IChannels;""", "internal sealed class B : IChannels;")]
    public async Task A_set_is_served_by_every_adapter_and_chosen_by_none(string ports, string? expected, params string[] adapters)
    {
        var host = await Host(ports, adapters);

        Assert.Equal(expected is null ? [] : [expected], host.Ids);
    }

    [Fact]
    public async Task An_adapter_lives_as_long_as_it_says()
    {
        var application = await Compile("Application", "namespace App;\n[Port] public interface ISignals;", "App.Application");
        var adapters = await Compile("Adapter", "using App;\n[Adapter(Lifetime = AdapterLifetime.Singleton)] internal sealed class Signals : ISignals;", "App.Adapters",
            application.Reference());

        Assert.Empty(adapters.Errors);
        var registration = adapters.Generated("Levee.Assembly.g.cs");
        Assert.Contains("AddSingleton<global::Signals>(services);", registration, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<global::App.ISignals>(services, static provider =>", registration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_binds_only_what_it_references()
    {
        var application = await Compile("Application", "namespace App;\n[Port] public interface IPrices;", "App.Application");
        var test = await Compile("Test", "public static class Tests;", "App.Tests", application.Reference());

        Assert.Empty(test.Errors);
    }

    [Fact]
    public async Task A_named_adapter_adapts_a_port()
    {
        var output = await Compile("Adapter", """[Adapter("x")] internal sealed class Nothing;""", "App.Adapters");

        Assert.Equal(["LEVEE109"], output.Ids);
    }

    [Theory]
    [InlineData("Application", false)]
    [InlineData("Domain", true)]
    [InlineData("Contracts", true)]
    [InlineData("Adapter", true)]
    public async Task Ports_are_declared_in_Application(string role, bool refused)
    {
        var output = await Compile(role, "[Port] public interface IPrices;", "App");

        Assert.Equal(refused, output.Ids.Contains("LEVEE010"));
    }

    [Fact]
    public async Task A_port_is_implemented_only_in_an_Adapter_project()
    {
        var output = await Compile("Application", "[Port] public interface IPrices; internal sealed class Prices : IPrices;", "App");

        Assert.Contains("LEVEE011", output.Ids);
    }

    [Fact]
    public async Task An_adapter_implements_nothing_of_Applications_but_ports()
    {
        var application = await Compile("Application", "namespace App;\npublic interface IHelper; [Port] public interface IPrices;", "App.Application");
        var sneaky = await Compile("Adapter", "using App;\ninternal sealed class Sneaky : IHelper;", "App.Adapters", application.Reference());
        var fine = await Compile("Adapter", "using App;\ninternal sealed class Prices : IPrices;", "App.Adapters", application.Reference());

        Assert.Contains("LEVEE011", sneaky.Ids);
        Assert.Empty(fine.Errors);
    }

    [Theory]
    [InlineData("Application", "new System.Net.Http.HttpClient()", true)]
    [InlineData("Domain", "System.IO.File.ReadAllText(\"x\")", true)]
    [InlineData("Contracts", "Environment.GetEnvironmentVariable(\"X\")", true)]
    [InlineData("Application", "Console.Out", true)]
    [InlineData("Application", "System.Diagnostics.Process.GetCurrentProcess()", true)]
    [InlineData("Application", "new System.IO.MemoryStream()", false)]
    [InlineData("Application", "System.Net.WebUtility.HtmlEncode(\"x\")", false)]
    [InlineData("Application", "Environment.NewLine", false)]
    [InlineData("Adapter", "new System.Net.Http.HttpClient()", false)]
    [InlineData("Kernel", "System.IO.File.Exists(\"x\")", true)]
    [InlineData("Application", "typeof(System.Net.Http.HttpRequestException)", false)]
    [InlineData("Application", "System.Net.Sockets.AddressFamily.InterNetwork", false)]
    public async Task The_core_does_IO_only_through_ports(string role, string expression, bool refused)
    {
        var output = await Compile(role, $"public static class Probe {{ public static object Go() => {expression}; }}", "App");

        Assert.Equal(refused, output.Ids.Contains("LEVEE012"));
    }

    [Theory]
    [InlineData("App.Contracts", "Contracts", false)]
    [InlineData("App.Application", "Application", true)]
    [InlineData("App.Domain", "Domain", true)]
    public async Task The_host_reaches_the_application_only_through_the_dispatcher(string assembly, string role, bool refused)
    {
        var inner = await Compile(role, "namespace Inner;\npublic static class Thing { public static int Value => 1; }", assembly);
        var host = await Compile("Host", "public static class Program { public static int Go() => Inner.Thing.Value; }", "App.Host", inner.Reference());

        Assert.Equal(refused, host.Ids.Contains("LEVEE013"));
    }
}
