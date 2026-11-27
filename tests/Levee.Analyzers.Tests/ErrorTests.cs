namespace Levee.Analyzers.Tests;

public sealed class ErrorTests
{
    private static string App(string errors, string fails) => $$"""
        using Levee;
        using System.Threading;
        using System.Threading.Tasks;
        namespace App;
        public static class NoteErrors { public const string Banned = "notes.banned"; }
        [Operation("notes.create", "member", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.Conflicts{{errors}})]
        public sealed record CreateNote(string Text) : IOperation<int>;
        internal sealed class CreateNoteHandler : IOperationHandler<CreateNote, int>
        {
            public Task<Result<int>> HandleAsync(CreateNote operation, OperationContext context, CancellationToken ct) =>
                Task.FromResult<Result<int>>({{fails}});
        }
        """;

    [Theory]
    [InlineData("", """Error.Validation("notes.too_long", "Too long.")""", "LEVEE110")]
    [InlineData("""Errors = ["notes.empty"]""", """Error.Validation("notes.too_long", "Too long.")""", "LEVEE110")]
    [InlineData("", """Error.Conflict(NoteErrors.Banned, "Banned.")""", "LEVEE110")]
    [InlineData("", """Error.Of(ErrorKind.Forbidden, "notes.locked", "Locked.")""", "LEVEE110")]
    [InlineData("", """Error.Validation(System.DateTime.MinValue.Year > 1 ? "notes.a" : "notes.b", "Either.")""", "LEVEE110")]
    [InlineData("""Errors = ["notes.too_long"]""", """Error.Validation("notes.too_long", "Too long.")""", null)]
    [InlineData("""Errors = [NoteErrors.Banned]""", """Error.Conflict(NoteErrors.Banned, "Banned.")""", null)]
    [InlineData("", """Error.Unavailable(LeveeErrors.NotImplemented, "Later.")""", null)]
    [InlineData("", """Error.Conflict("conflict", "Twice.")""", null)]
    public async Task A_handler_fails_only_with_what_its_operation_declares(string errors, string fails, string? expected)
    {
        var output = await Probe.CompileAsync("Test", App(errors.Length == 0 ? "" : ", " + errors, fails), "App.Tests", GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration);

        Assert.Equal(expected is null ? [] : new[] { expected }, output.Ids.Distinct());
    }

    [Theory]
    [InlineData("""[Operation("notes.list", "member", Platform = true, Errors = ["Notes Missing"])] public sealed record ListNotes : IOperation<int>;""")]
    [InlineData("""[Operation("notes.watch", "member", Platform = true, Errors = ["notes.gone"])] public sealed record WatchNotes : ISubscription<int>;""")]
    public async Task Declared_errors_are_codes_and_subscriptions_declare_none(string declaration)
    {
        var output = await Probe.CompileAsync("Contracts", $"using Levee;\nnamespace App;\n{declaration}", "App", GeneratorTests.Levee);

        Assert.Contains("LEVEE104", output.Ids);
    }

    [Theory]
    [InlineData("""[Refuses("access.tenant")]""", "access.tenant", null)]
    [InlineData("", "access.tenant", "LEVEE111")]
    [InlineData("""[Refuses("access.other")]""", "access.tenant", "LEVEE111")]
    [InlineData("""[Refuses("Access Tenant")]""", "conflict", "LEVEE104")]
    public async Task An_access_policy_refuses_only_with_what_it_declares(string attribute, string code, string? expected)
    {
        var output = await Probe.CompileAsync("Composition", $$"""
            using Levee;
            using System.Threading;
            using System.Threading.Tasks;
            namespace App;
            {{attribute}}
            internal sealed class Access : IAccessPolicy
            {
                public ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct) =>
                    ValueTask.FromResult<Error?>(request.Scope.Key is null ? null : Error.Forbidden("{{code}}", "No."));
            }
            """, "App.Composition", GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration);

        Assert.Equal(expected is null ? [] : new[] { expected }, output.Ids.Distinct());
    }
}
