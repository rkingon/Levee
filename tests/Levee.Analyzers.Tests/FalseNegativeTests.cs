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
}
