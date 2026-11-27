using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Levee.Analyzers.Tests;

/// <summary>What the alpha.2 false-negative pass got past the build, held to.</summary>
public sealed class FalseNegativeTests
{
    private static Task<Probe.Output> Compile(string role, string code, string assembly = "App", params MetadataReference[] more) =>
        Probe.CompileAsync(role, code, assembly + "." + role, [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration, .. more]);

    private static string Message(Probe.Output output, string id) =>
        output.Errors.First(e => e.Id == id).GetMessage(CultureInfo.InvariantCulture);

    [Theory]
    // Platform alone: refuses the platform's scope, then grants any member any tenant.
    [InlineData("""
        request.Operation.Access switch
        {
            "member" when request.Scope.Platform => Error.Forbidden("access.denied", "Members act in a tenant."),
            "member" => request.Principal is Member ? null : Error.Forbidden("access.denied", "Sign in."),
            _ => Error.Forbidden("access.denied", "No."),
        }
        """, true)]
    [InlineData("request is { Scope.Platform: false } ? null : Error.Forbidden(\"access.denied\", \"No.\")", true)]
    [InlineData("request.Scope is { Key: _ } ? null : Error.Forbidden(\"access.denied\", \"No.\")", true)]
    [InlineData("request.Scope is (_, false) ? null : Error.Forbidden(\"access.denied\", \"No.\")", true)]
    [InlineData("request.Principal is Member m && request.Scope.Key == m.Tenant ? null : Error.Forbidden(\"access.denied\", \"No.\")", false)]
    [InlineData("request is { Principal: Member m, Scope.Key: var key } && key == m.Tenant ? null : Error.Forbidden(\"access.denied\", \"No.\")", false)]
    [InlineData("request.Principal is Member m && request.Scope is (var key, _) && key == m.Tenant ? null : Error.Forbidden(\"access.denied\", \"No.\")", false)]
    [InlineData("Mine(request.Scope, request.Principal) ? null : Error.Forbidden(\"access.denied\", \"No.\")", false,
        "private static bool Mine(WorkScope scope, Principal principal) { var (key, _) = scope; return principal is Member m && key == m.Tenant; }")]
    [InlineData("Mine(request.Scope, request.Principal) ? null : Error.Forbidden(\"access.denied\", \"No.\")", true,
        "private static bool Mine(WorkScope scope, Principal principal) { var (_, readOnly) = scope; return principal is Member && !readOnly; }")]
    // A policy that grants nothing reaches no scope.
    [InlineData("Error.Forbidden(\"access.denied\", \"Closed.\")", false)]
    [InlineData("request.Principal is Member ? Error.Forbidden(\"access.denied\", \"Not yet.\") : Error.Unauthenticated(\"Sign in.\")", false)]
    [InlineData("request.Operation.Access switch { \"member\" => Error.Forbidden(\"access.denied\", \"Not yet.\"), _ => throw new System.InvalidOperationException() }", false)]
    [InlineData("request.Principal is Member ? null : Error.Unauthenticated(\"Sign in.\")", true)]
    public async Task An_access_policy_reads_the_key_of_the_scope_it_authorizes(string decision, bool refused, string helper = "")
    {
        var output = await Compile("Host", $$"""
            using Levee;
            using System.Threading;
            using System.Threading.Tasks;
            internal sealed record Member(string Subject, string Tenant) : Principal(Subject);
            [Refuses("access.denied")]
            internal sealed class Access : IAccessPolicy
            {
                public ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct) => ValueTask.FromResult<Error?>({{decision}});
                {{helper}}
            }
            public static class Program { public static void Main() { } }
            """);

        Assert.DoesNotContain(output.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused ? ["LEVEE113"] : [], output.Ids.Distinct());
        if (refused)
        {
            Assert.Contains("request.Scope.Key == m.Tenant", Message(output, "LEVEE113"), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("typeof(Program).GetCustomAttributes(false).OfType<OperationAttribute>().Select(a => a.Name).ToList()", true)]
    [InlineData("typeof(Program).GetCustomAttributes(false).FirstOrDefault(a => a is OperationAttribute)", true)]
    [InlineData("typeof(Program).GetCustomAttributes(false) is [Levee.OperationAttribute op, ..] ? op.Name : null", true)]
    [InlineData("(OperationAttribute?)System.Attribute.GetCustomAttribute(typeof(Program), typeof(System.Attribute))", true)]
    [InlineData("typeof(OperationAttribute)", true)]
    [InlineData("typeof(Program).GetCustomAttributes(false).OfType<Named>().ToList()", true, "using Named = Levee.OperationAttribute;")]
    [InlineData("typeof(Program).GetCustomAttributes(false).OfType<System.ObsoleteAttribute>().ToList()", false)]
    [InlineData("Tagged.Kind", false)]
    public async Task OperationAttribute_is_named_only_as_an_attribute(string expression, bool refused, string header = "")
    {
        var output = await Probe.CompileAsync(new Probe.Settings("Host", Exe: true), $$"""
            {{header}}
            using System.Linq;
            using Levee;
            [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class KindAttribute(System.Type kind) : System.Attribute { public System.Type Kind { get; } = kind; }
            [Kind(typeof(OperationAttribute))] public static class Tagged { public static System.Type Kind => typeof(int); }
            /// <summary>Not a <see cref="OperationAttribute"/>.</summary>
            public static class Program
            {
                public static void Main() { }
                public static object? Find() => {{expression}};
            }
            """, "App.Host", GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration);

        Assert.DoesNotContain(output.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused ? ["LEVEE020"] : [], output.Ids.Distinct());
        Assert.Equal(refused ? 1 : 0, output.Errors.Length);
    }

    private const string Tally = """
        using Levee;
        using System.Threading;
        using System.Threading.Tasks;
        public sealed record RecountInput(string Tenant);
        [Operation("notes.tally", "system", Platform = true)] public sealed record TallyAllNotes : IOperation<int>;

        """;

    [Theory]
    [InlineData("Application", """
        internal sealed class Escalator(Dispatcher dispatcher, SystemPrincipal system)
        {
            public Task<Result<int>> TallyAsync(CancellationToken ct) => dispatcher.SendAsync(new TallyAllNotes(), system, ct);
        }
        internal sealed class RecountProcess(Escalator escalator) : IProcess<RecountInput>
        {
            public Task RunAsync(RecountInput input, ProcessContext context, CancellationToken ct) => escalator.TallyAsync(ct);
        }
        """, true)]
    [InlineData("Application", "internal sealed class Holder(System.Lazy<SystemPrincipal> system) { public object As => system.Value; }", true)]
    [InlineData("Application", "internal sealed class Holder { private Principal? _as; public void Set(Principal p) => _as = p; public object? As => _as; }", true)]
    [InlineData("Application", "internal sealed record Acting(Principal As);", true)]
    [InlineData("Adapter", "internal sealed class Holder { public Principal[] As { get; init; } = []; }", true)]
    [InlineData("Domain", "public sealed class Holder(System.Func<Principal> who) { public string Name => who().Subject; }", true)]
    [InlineData("Contracts", "public sealed record Envelope(string Text, Principal From);", true)]
    [InlineData("Application", """
        internal sealed class Escalator(Dispatcher dispatcher)
        {
            public Task<Result<int>> TallyAsync(Principal caller, CancellationToken ct) => dispatcher.SendAsync(new TallyAllNotes(), caller, ct);
        }
        """, false)]
    [InlineData("Kernel", "public sealed record Member(string Subject, string Tenant) : Principal(Subject);", false)]
    [InlineData("Composition", "internal sealed class Runtime(Dispatcher dispatcher, SystemPrincipal system) { public object Hold => (dispatcher, system); }", false)]
    public async Task Only_where_the_app_composes_is_a_principal_held(string role, string code, bool refused)
    {
        var output = await Compile(role, Tally + code);

        Assert.DoesNotContain(output.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused, output.Ids.Contains("LEVEE021"));
        if (refused)
        {
            var message = Message(output, "LEVEE021");
            Assert.Contains("OperationContext.Principal", message, StringComparison.Ordinal);
            Assert.DoesNotContain("handed to", message, StringComparison.Ordinal);
        }
    }
}
