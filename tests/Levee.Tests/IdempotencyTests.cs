using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Levee.Tests;

public sealed class IdempotencyTests
{
    private static readonly Member Ada = new("ada", "acme");
    private readonly Store _store = new();
    private readonly Dispatcher _dispatcher;

    public IdempotencyTests() => _dispatcher = DispatcherTests.Services(_store, new Outcomes()).GetRequiredService<Dispatcher>();

    private static string Name() => $"n{Guid.NewGuid():N}";

    [Fact]
    public async Task A_write_sent_again_with_its_key_answers_the_first_result_without_running_again()
    {
        var name = Name();

        var first = await _dispatcher.SendAsync(new Count("acme", name), Ada, "key-1", CancellationToken.None);
        var again = await _dispatcher.SendAsync(new Count("acme", name), Ada, "key-1", CancellationToken.None);
        var other = await _dispatcher.SendAsync(new Count("acme", name), Ada, "key-2", CancellationToken.None);

        Assert.Equal((1, 1, 2), (first.Value, again.Value, other.Value));
        Assert.Equal(2, Counted.Runs[name]);
        Assert.Equal(2, _store.Audited.Count);
    }

    [Fact]
    public async Task A_key_sent_again_with_other_input_is_refused()
    {
        var name = Name();
        await _dispatcher.SendAsync(new Count("acme", name), Ada, "key-1", CancellationToken.None);

        var reused = await _dispatcher.SendAsync(new Count("acme", Name()), Ada, "key-1", CancellationToken.None);

        Assert.Equal((ErrorKind.Conflict, LeveeErrors.IdempotencyMismatch), (reused.Error!.Kind, reused.Error.Code));
    }

    [Fact]
    public async Task A_result_carrying_a_secret_is_never_shown_again()
    {
        var first = await _dispatcher.SendAsync(new IssueToken("acme", "ci"), Ada, "key-1", CancellationToken.None);
        var again = await _dispatcher.SendAsync(new IssueToken("acme", "ci"), Ada, "key-1", CancellationToken.None);

        Assert.Equal("token-ci", first.Value.Token);
        Assert.Equal((ErrorKind.Conflict, LeveeErrors.IdempotencyReplayed), (again.Error!.Kind, again.Error.Code));
    }

    [Fact]
    public async Task A_key_is_each_callers_own()
    {
        var name = Name();

        await _dispatcher.SendAsync(new Count("acme", name), Ada, "key-1", CancellationToken.None);
        var theirs = await _dispatcher.SendAsync(new Count("acme", name), new Member("bob", "acme"), "key-1", CancellationToken.None);

        Assert.Equal(2, theirs.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tab\there")]
    public async Task A_key_that_isnt_one_is_refused_before_anything_runs(string key)
    {
        var refused = await _dispatcher.SendAsync(new Count("acme", Name()), Ada, key, CancellationToken.None);

        Assert.Equal((ErrorKind.Validation, LeveeErrors.InvalidIdempotencyKey), (refused.Error!.Kind, refused.Error.Code));
        Assert.Empty(_store.Begun);
    }

    [Fact]
    public async Task A_read_ignores_its_key()
    {
        var said = await _dispatcher.SendAsync(new Echo("hi"), Ada, "", CancellationToken.None);

        Assert.Equal("hi", said.Value.Said);
    }
}

internal static class Counted
{
    public static ConcurrentDictionary<string, int> Runs { get; } = new();
}

[Operation("probe.count", "member", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record Count(string Tenant, string Name) : IOperation<int>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class CountHandler : IOperationHandler<Count, int>
{
    public Task<Result<int>> HandleAsync(Count operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<int>>(Counted.Runs.AddOrUpdate(operation.Name, 1, (_, n) => n + 1));
}

public sealed record IssuedToken(string Name, [property: Secret] string Token);

[Operation("probe.issue_token", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record IssueToken(string Tenant, string Name) : IOperation<IssuedToken>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class IssueTokenHandler : IOperationHandler<IssueToken, IssuedToken>
{
    public Task<Result<IssuedToken>> HandleAsync(IssueToken operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<IssuedToken>>(new IssuedToken(operation.Name, $"token-{operation.Name}"));
}
