using Microsoft.CodeAnalysis;

namespace Levee.Analyzers.Tests;

/// <summary>What the alpha.2 release audit found in the generator and analyzers, held to.</summary>
public sealed class Alpha2AuditTests
{
    private static Task<Probe.Output> Compile(string role, string code, string assembly = "App", params MetadataReference[] more) =>
        Probe.CompileAsync(role, code, assembly + "." + role, [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration, .. more]);

    [Theory]
    [InlineData("public Label Shown => new(Query);", true)]
    [InlineData("[JsonIgnore] public Label Derived => new(Query);", false)]
    [InlineData("[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Label? Sometimes { get; init; }", true)]
    [InlineData("[JsonInclude] public Label? Extra;", true)]
    [InlineData("[JsonInclude] internal Label? Hidden { get; init; }", true)]
    [InlineData("internal Label? Unwritten { get; init; }", false)]
    public async Task A_converter_is_checked_on_the_members_System_Text_Json_writes(string member, bool refused)
    {
        var output = await Compile("Contracts", $$"""
            using Levee;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            [JsonConverter(typeof(LabelConverter))] public sealed record Label(string Text);
            public sealed class LabelConverter : JsonConverter<Label>
            {
                public override Label Read(ref Utf8JsonReader r, System.Type t, JsonSerializerOptions o) => new(r.GetString()!);
                public override void Write(Utf8JsonWriter w, Label v, JsonSerializerOptions o) => w.WriteStringValue(v.Text);
            }
            [Operation("audit.search", "member", Platform = true)] public sealed record Search(string Query) : IOperation<int>
            {
                {{member}}
            }
            """);

        Assert.Equal(refused ? ["LEVEE104"] : [], output.Ids.Distinct());
    }

    [Theory]
    [InlineData("[Port(Many = true, Default = \"missing\")]", true)]
    [InlineData("[Port(Many = true)]", false)]
    public async Task A_set_port_has_no_fallback(string port, bool refused)
    {
        var application = await Compile("Application", $"using Levee;\n{port} public interface IChannels;");
        Assert.Empty(application.Errors);

        var host = await Compile("Host", "public static class Program;", "App", application.Reference());

        Assert.Equal(refused, host.Ids.Contains("LEVEE108"));
    }

    [Fact]
    public async Task A_closed_generic_helper_is_registered_as_constructed()
    {
        var output = await Compile("Application", """
            using Levee;
            using System.Threading;
            using System.Threading.Tasks;
            internal sealed class Formatter<T>;
            internal sealed class FormatNumber(Formatter<int> numbers, Formatter<string> labels) : IProcess<int>
            {
                public Task RunAsync(int input, ProcessContext context, CancellationToken ct) => Task.CompletedTask;
            }
            """);
        var registration = output.Generated("Levee.Assembly.g.cs");

        Assert.Empty(output.Errors);
        Assert.Contains("TryAddScoped<global::Formatter<int>>(services);", registration, StringComparison.Ordinal);
        Assert.Contains("TryAddScoped<global::Formatter<string>>(services);", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("Formatter<T>", registration, StringComparison.Ordinal);
    }

    private const string Processes = """
        using Levee;
        using System.Threading;
        using System.Threading.Tasks;
        internal abstract class Processor<T> : IProcess<T>
        {
            public Task RunAsync(T input, ProcessContext context, CancellationToken ct) => Task.CompletedTask;
        }
        """;

    [Theory]
    [InlineData("internal sealed class First : Processor<int>; internal sealed class Second : Processor<int>;", true)]
    [InlineData("internal sealed class First : Processor<int>; internal sealed class Second : Processor<long>;", false)]
    public async Task A_process_input_is_run_by_one_process(string processes, bool refused)
    {
        var application = await Compile("Application", Processes + processes);
        Assert.Empty(application.Errors);

        var host = await Compile("Host", "public static class Program;", "App", application.Reference());

        Assert.Equal(refused, host.Ids.Contains("LEVEE112"));
    }

    [Fact]
    public async Task A_tests_own_process_for_the_apps_input_is_a_second_one()
    {
        var application = await Compile("Application", Processes + "internal sealed class Real : Processor<int>;");
        var composition = await Compile("Composition", "internal static class Wiring;", "App", application.Reference());
        Assert.Empty(composition.Errors);

        var test = await Compile("Test", Processes + "internal sealed class Fake : Processor<int>; internal sealed class Own : Processor<long>;", "App",
            application.Reference(), composition.Reference());

        var error = Assert.Single(test.Errors, e => e.Id == "LEVEE112");
        Assert.Contains("Fake", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
