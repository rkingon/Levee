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

    [Theory]
    [InlineData("[System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"SendJsonAsync\")] static extern object Send(Dispatcher d);", true)]
    [InlineData("[System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"Run\")] static extern void Run([System.Runtime.CompilerServices.UnsafeAccessorType(\"Levee.Worker, Levee\")] object w);", true)]
    [InlineData("[System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"SendJsonAsync\")] static extern System.Threading.Tasks.Task<Result<System.Text.Json.JsonElement>> SendJson(Dispatcher dispatcher, string name, System.Text.Json.JsonElement input, Principal principal, System.Threading.CancellationToken ct);", true)]
    [InlineData("[System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Constructor)] static extern OperationContext Make(Principal p, OperationDescriptor d, WorkScope s, System.DateTimeOffset now);", true)]
    [InlineData("[System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = \"_items\")] static extern ref int[] Items(System.Collections.Generic.List<int> list);", false)]
    [InlineData("[System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"Log\")] static extern void Log(System.Collections.Generic.List<int> list, Principal who);", false)]
    public async Task An_unsafe_accessor_into_Levee_is_reflection_by_another_name(string accessor, bool refused)
    {
        var output = await Probe.CompileAsync(new Probe.Settings("Host", Exe: true),
            $"using Levee;\npublic static class Program {{ public static void Main() {{ }} {accessor} }}", "App.Host",
            GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration);

        Assert.Equal(refused, output.Ids.Contains("LEVEE020"));
    }

    private const string Wiring = """
        using Microsoft.Extensions.DependencyInjection;
        public static class Wiring
        {
            public static IServiceCollection AddZipCodes(this IServiceCollection services) => services;
            public static void AddMaps(this IServiceCollection services) { }
            public static int CountDescriptors(this IServiceCollection services) => services.Count;
        }
        """;

    [Theory]
    [InlineData("services.AddZipCodes();", false)]
    [InlineData("services.AddMaps();", false)]
    [InlineData("services.AddZipCodes().AddMaps();", false)]
    [InlineData("services.AddZipCodes().Clear();", false)]
    [InlineData("return services.AddZipCodes();", false)]
    [InlineData("var count = services.CountDescriptors();", true)]
    [InlineData("services.CountDescriptors();", true)]
    [InlineData("var held = services.AddZipCodes();", true)]
    [InlineData("System.Console.WriteLine(services.AddZipCodes());", true)]
    public async Task Composition_calls_an_adapters_registration_to_register_and_nothing_else(string call, bool refused)
    {
        var adapter = await Compile("Adapter", Wiring);
        Assert.Empty(adapter.Errors);

        var composition = await Compile("Composition", $$"""
            using Microsoft.Extensions.DependencyInjection;
            public static class Root
            {
                public static IServiceCollection AddApp(this IServiceCollection services)
                {
                    {{call}}
                    return services;
                }
            }
            """, "App", adapter.Reference());

        Assert.DoesNotContain(composition.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused, composition.Ids.Contains("LEVEE015"));
    }

    [Theory]
    [InlineData("\"select pg_notify('levee_signals', json_build_object('kind', 'notes.changed', 'scope', 'b')::text)\"", true)]
    [InlineData("$\"select pg_notify('levee_signals', {payload})\"", true)]
    [InlineData("\"NOTIFY levee_signals, 'x'\"", true)]
    [InlineData("\"select pg_notify('app_feed', 'x')\"", false)]
    [InlineData("\"select current_setting('levee.scope', true)\"", false)]
    public async Task A_change_signal_is_raised_through_IChangeSignals_not_SQL(string sql, bool refused)
    {
        var output = await Compile("Adapter", $"public static class Feed {{ public static string Sql(string payload) => {sql}; }}");

        Assert.Equal(refused ? ["LEVEE004"] : [], output.Ids.Distinct());
        Assert.Equal(refused, output.Errors.Any(e => e.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("IChangeSignals.RaiseAsync", StringComparison.Ordinal)));
    }

    private const string NodaTime = """
        namespace NodaTime
        {
            public readonly struct Instant;
            public interface IClock { Instant GetCurrentInstant(); }
            public sealed class SystemClock : IClock { public static SystemClock Instance { get; } = new(); public Instant GetCurrentInstant() => default; }
        }

        """;

    [Theory]
    [InlineData("Application", "public static object Now() => NodaTime.SystemClock.Instance.GetCurrentInstant();", true)]
    [InlineData("Application", "public static object Now() => Microsoft.VisualBasic.DateAndTime.Now;", true)]
    [InlineData("Application", "public static object Now() => Microsoft.VisualBasic.DateAndTime.Timer;", true)]
    [InlineData("Composition", "public static object Now() => Microsoft.VisualBasic.DateAndTime.Today;", true)]
    [InlineData("Composition", "public static NodaTime.IClock Clock() => NodaTime.SystemClock.Instance;", false)]
    [InlineData("Application", "public static object Now(NodaTime.IClock clock) => clock.GetCurrentInstant();", false)]
    public async Task NodaTime_and_Visual_Basic_clocks_are_the_wall_clock(string role, string member, bool refused)
    {
        var output = await Compile(role, NodaTime + $"internal static class Clocks {{ {member} }}");

        Assert.DoesNotContain(output.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused, output.Ids.Contains("LEVEE003"));
    }

    [Theory]
    [InlineData("Application", "typeof(System.IServiceProvider).GetMethod(nameof(System.IServiceProvider.GetService))!.Invoke(services, [typeof(int)])", true)]
    [InlineData("Application", "services.GetType().GetMethod(\"GetService\")", true)]
    [InlineData("Adapter", "typeof(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions).GetMethods()", true)]
    [InlineData("Application", "System.Type.GetType(\"System.IServiceProvider\")!.GetMethods()", true)]
    [InlineData("Composition", "typeof(System.IServiceProvider).GetMethod(nameof(System.IServiceProvider.GetService))", false)]
    [InlineData("Application", "typeof(string).GetMethods()", false)]
    public async Task Services_are_not_located_by_reflecting_on_the_container(string role, string lookup, bool refused)
    {
        var output = await Compile(role, $"internal static class Find {{ public static object? Locate(System.IServiceProvider services) => {lookup}; }}");

        Assert.DoesNotContain(output.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused, output.Ids.Contains("LEVEE005"));
    }

    [Theory]
    [InlineData("System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(typeof(Program).Assembly.GetTypes(), t => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<OperationAttribute>(t)))", true)]
    [InlineData("System.Attribute.GetCustomAttribute(typeof(Program), typeof(OperationAttribute))", true)]
    [InlineData("d.SendAsync((dynamic)op, p, System.Threading.CancellationToken.None)", true)]
    [InlineData("d.SendAsync((IOperation<int>)op, p, default)", false)]
    [InlineData("System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.ObsoleteAttribute>(typeof(Program))", false)]
    [InlineData("Tagged.Kind", false)]
    public async Task Operations_are_found_by_name_only_by_Levees_projections(string expression, bool refused)
    {
        var output = await Probe.CompileAsync(new Probe.Settings("Host", Exe: true), $$"""
            using Levee;
            [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class KindAttribute(System.Type kind) : System.Attribute { public System.Type Kind { get; } = kind; }
            [Kind(typeof(OperationAttribute))] public static class Tagged { public static System.Type Kind => typeof(int); }
            public static class Program
            {
                public static void Main() { }
                public static object? Find(Dispatcher d, object op, Principal p) => {{expression}};
            }
            """, "App.Host", GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration);

        Assert.DoesNotContain(output.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused, output.Ids.Contains("LEVEE020"));
    }

    private const string Recount = """
        using Levee;
        using System.Threading;
        using System.Threading.Tasks;
        public sealed record RecountInput(string Tenant);
        [Operation("notes.tally", "system", Platform = true)] public sealed record TallyAllNotes : IOperation<int>;

        """;

    [Theory]
    [InlineData("internal sealed class RecountProcess(Dispatcher dispatcher) : IProcess<RecountInput> { public Task RunAsync(RecountInput input, ProcessContext context, CancellationToken ct) => Task.CompletedTask; }", true)]
    [InlineData("internal sealed class RecountProcess(System.Lazy<Dispatcher> dispatcher) : IProcess<RecountInput> { public Task RunAsync(RecountInput input, ProcessContext context, CancellationToken ct) => Task.CompletedTask; }", true)]
    [InlineData("internal sealed class RecountProcess : IProcess<RecountInput> { public Task RunAsync(RecountInput input, ProcessContext context, CancellationToken ct) => context.SendAsync(new TallyAllNotes(), ct); }", false)]
    public async Task A_process_sends_as_its_starter_through_its_context(string process, bool refused)
    {
        var output = await Compile("Application", Recount + process);

        Assert.Equal(refused, output.Ids.Contains("LEVEE021"));
    }

    [Theory]
    [InlineData("Application", "public static Principal As() => new SystemPrincipal(\"recount\");", true)]
    [InlineData("Adapter", "public static Principal As() => new JobPrincipal(\"recount\", true);", true)]
    [InlineData("Contracts", "public static Principal As() => new AnonymousPrincipal();", true)]
    [InlineData("Domain", "public static Principal As(SystemPrincipal p) => p with { Subject = \"other\" };", true)]
    [InlineData("Application", "public static Principal As(OperationContext context) => context.Principal;", false)]
    [InlineData("Composition", "public static Principal As() => new SystemPrincipal(\"recount\");", false)]
    [InlineData("Infrastructure", "public static string As() => \"recount\";", false)]
    public async Task Who_an_operation_runs_as_is_decided_where_the_app_composes(string role, string member, bool refused)
    {
        var output = await Compile(role, $"using Levee;\ninternal static class Acting {{ {member} }}");

        Assert.DoesNotContain(output.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused, output.Ids.Contains("LEVEE021"));
    }

    [Theory]
    [InlineData("ValueTask.FromResult<Error?>(request.Principal is SystemPrincipal ? null : Error.Forbidden(\"access.denied\", \"No.\"))", true)]
    [InlineData("ValueTask.FromResult<Error?>(request.Scope.Key == \"acme\" ? null : Error.Forbidden(\"access.denied\", \"No.\"))", false)]
    [InlineData("ValueTask.FromResult<Error?>(request is { Scope.Platform: true } ? null : Error.Forbidden(\"access.denied\", \"No.\"))", false)]
    [InlineData("ValueTask.FromResult(Decide(request)); private static Error? Decide(AccessRequest request) => request.Scope.Key is null ? Error.Forbidden(\"access.denied\", \"No.\") : null", false)]
    [InlineData("ValueTask.FromResult(Elsewhere.Decide(request))", true)]
    public async Task An_access_policy_reads_the_scope_it_authorizes(string body, bool refused)
    {
        var output = await Compile("Composition", $$"""
            using Levee;
            using System.Threading;
            using System.Threading.Tasks;
            internal static class Elsewhere { public static Error? Decide(AccessRequest request) => request.Scope.Platform ? null : Error.Forbidden("access.denied", "No."); }
            [Refuses("access.denied")]
            internal sealed class Access : IAccessPolicy
            {
                public ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct) => {{body}};
            }
            """);

        Assert.Equal(refused ? ["LEVEE113"] : [], output.Ids.Distinct());
    }

    [Fact]
    public async Task A_tests_policy_is_held_to_no_scope()
    {
        var output = await Probe.CompileAsync("Test", """
            using Levee;
            internal sealed class LetAll : IAccessPolicy
            {
                public System.Threading.Tasks.ValueTask<Error?> AuthorizeAsync(AccessRequest request, System.Threading.CancellationToken ct) => default;
            }
            """, "App.Tests", GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration);

        Assert.DoesNotContain("LEVEE113", output.Ids);
    }
}
