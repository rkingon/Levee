using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Levee.Tests;

/// <summary>The contract's wire rules on every path an operation is sent or streamed by, not only one.</summary>
public sealed class WirePathTests
{
    private static readonly Member Ada = new("ada", "acme");
    private readonly Store _store = new();
    private readonly Dispatcher _dispatcher;

    public WirePathTests() => _dispatcher = DispatcherTests.Services(_store, new Outcomes()).GetRequiredService<Dispatcher>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_keyed_request_sent_again_as_it_was_is_its_repeat_whatever_its_converters_write()
    {
        var input = JsonSerializer.SerializeToElement(new { tenant = "acme", stamp = 7 });

        var first = await _dispatcher.SendFromClientAsync("probe.stamp_input", input, Ada, "key-1", Ct);
        var again = await _dispatcher.SendFromClientAsync("probe.stamp_input", input, Ada, "key-1", Ct);

        Assert.True(again.IsSuccess, again.Error?.Message);
        Assert.Equal(first.Value.GetRawText(), again.Value.GetRawText());
        Assert.Single(_store.Audited);
    }

    [Fact]
    public async Task A_typed_input_is_taken_once_for_its_key_its_audit_and_its_handler()
    {
        var first = await _dispatcher.SendAsync(new Sum("acme", new Once([1, 2])), Ada, "key-1", Ct);
        var other = await _dispatcher.SendAsync(new Sum("acme", new Once([4, 5])), Ada, "key-1", Ct);

        Assert.Equal(3, first.Value);
        Assert.Equal(LeveeErrors.IdempotencyMismatch, other.Error!.Code);
    }

    [Fact]
    public async Task A_typed_subscription_takes_its_input_once()
    {
        var opened = await _dispatcher.SubscribeAsync(new RunningSum(new Once([7])), Ada, Ct);
        var read = new List<int>();

        await foreach (var sum in opened.Value.WithCancellation(Ct))
        {
            read.Add(sum);
        }

        Assert.Equal([7], read);
    }

    [Fact]
    public async Task A_typed_subscription_refused_for_its_input_is_told()
    {
        var outcomes = new Outcomes();
        var dispatcher = DispatcherTests.Services(new Store(), outcomes).GetRequiredService<Dispatcher>();

        var opened = await dispatcher.SubscribeAsync(new RunningSum(null!), Ada, Ct);

        Assert.Equal(LeveeErrors.InvalidInput, opened.Error!.Code);
        Assert.Contains(outcomes.Seen, o => o.Operation.Name == "probe.running_sum");
    }

    [Fact]
    public async Task An_input_whose_converter_throws_a_cancellation_is_refused_and_told()
    {
        var outcomes = new Outcomes();
        var dispatcher = DispatcherTests.Services(new Store(), outcomes).GetRequiredService<Dispatcher>();

        var sent = await dispatcher.SendAsync(new Halting(new Halt(1)), Ada, Ct);

        Assert.Equal(LeveeErrors.InvalidInput, sent.Error!.Code);
        Assert.Contains(outcomes.Seen, o => o.Operation.Name == "probe.halting");
    }

    [Fact]
    public async Task A_stream_item_whose_converter_throws_a_cancellation_ends_the_stream_with_an_error()
    {
        var opened = await _dispatcher.SubscribeJsonAsync("probe.halts", JsonDocument.Parse("{}").RootElement, Ada, Ct);

        var failed = await Assert.ThrowsAsync<OperationFailedException>(async () =>
        {
            await foreach (var _ in opened.Value.WithCancellation(Ct))
            {
            }
        });

        Assert.Equal(ErrorKind.Internal, failed.Error.Kind);
    }

    [Fact]
    public async Task A_stream_item_its_contract_cant_read_back_ends_the_stream_as_the_handlers_bug()
    {
        var opened = await _dispatcher.SubscribeJsonAsync("probe.empty_views", JsonDocument.Parse("{}").RootElement, Ada, Ct);

        var failed = await Assert.ThrowsAsync<OperationFailedException>(async () =>
        {
            await foreach (var _ in opened.Value.WithCancellation(Ct))
            {
            }
        });

        Assert.Equal(ErrorKind.Internal, failed.Error.Kind);
    }

    [Fact]
    public async Task A_member_sent_twice_in_two_cases_is_refused()
    {
        var input = JsonDocument.Parse("""{"tenant":"acme","stamp":1,"STAMP":2}""").RootElement;

        var sent = await _dispatcher.SendFromClientAsync("probe.stamp_input", input, Ada, "key-1", Ct);

        Assert.Equal(LeveeErrors.InvalidInput, sent.Error!.Code);
    }

    [Fact]
    public async Task A_result_sent_as_JSON_that_its_contract_cant_read_back_commits_nothing()
    {
        var said = await _dispatcher.SendJsonAsync("probe.say_empty", JsonSerializer.SerializeToElement(new { tenant = "acme" }), Ada, Ct);

        Assert.Equal(ErrorKind.Internal, said.Error!.Kind);
        Assert.Equal(0, _store.Commits);
    }

    [Fact]
    public async Task A_typed_subscriber_never_gets_a_null_its_contract_doesnt_allow()
    {
        var opened = await _dispatcher.SubscribeAsync(new Words(), Ada, Ct);
        var read = new List<string>();

        var failed = await Assert.ThrowsAsync<OperationFailedException>(async () =>
        {
            await foreach (var word in opened.Value.WithCancellation(Ct))
            {
                read.Add(word);
            }
        });

        Assert.Equal(["one"], read);
        Assert.Equal(ErrorKind.Internal, failed.Error.Kind);
    }
}

[Operation("probe.stamp_input", "member", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record StampInput(string Tenant, Stamp Stamp) : IOperation<int>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class StampInputHandler : IOperationHandler<StampInput, int>
{
    public Task<Result<int>> HandleAsync(StampInput operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<int>>(operation.Stamp.Value);
}

/// <summary>A sequence that yields its numbers to the first that reads it, and nothing after.</summary>
public sealed class Once(int[] numbers) : IEnumerable<int>
{
    private bool _read;

    public IEnumerator<int> GetEnumerator()
    {
        var first = !_read;
        _read = true;
        return ((IEnumerable<int>)(first ? numbers : [])).GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

[Operation("probe.sum", "member", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record Sum(string Tenant, IEnumerable<int> Numbers) : IOperation<int>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class SumHandler : IOperationHandler<Sum, int>
{
    public Task<Result<int>> HandleAsync(Sum operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<int>>(operation.Numbers.Sum());
}

[Operation("probe.running_sum", "member", Platform = true)]
public sealed record RunningSum(IEnumerable<int> Numbers) : ISubscription<int>;

internal sealed class RunningSumHandler : ISubscriptionHandler<RunningSum, int>
{
    public async IAsyncEnumerable<int> SubscribeAsync(RunningSum subscription, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return subscription.Numbers.Sum();
    }
}

/// <summary>A value whose converter throws a cancellation of its own as it's written: its bug, not the caller's cancel.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(HaltJson))]
[ValueObject(typeof(int))]
public sealed record Halt(int Value);

public sealed class HaltJson : System.Text.Json.Serialization.JsonConverter<Halt>
{
    public override Halt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, Halt value, JsonSerializerOptions options) => throw new OperationCanceledException("the converter's own");
}

[Operation("probe.empty_views", "member", Platform = true)]
public sealed record EmptyViews : ISubscription<RequiredView>;

internal sealed class EmptyViewsHandler : ISubscriptionHandler<EmptyViews, RequiredView>
{
    public async IAsyncEnumerable<RequiredView> SubscribeAsync(EmptyViews subscription, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return new RequiredView(new RequiredText(""));
    }
}

[Operation("probe.halts", "member", Platform = true)]
public sealed record Halts : ISubscription<Halt>;

internal sealed class HaltsHandler : ISubscriptionHandler<Halts, Halt>
{
    public async IAsyncEnumerable<Halt> SubscribeAsync(Halts subscription, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return new Halt(1);
    }
}

[Operation("probe.halting", "member", Platform = true)]
public sealed record Halting(Halt Halt) : IOperation<Unit>;

internal sealed class HaltingHandler : IOperationHandler<Halting, Unit>
{
    public Task<Result<Unit>> HandleAsync(Halting operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

[Operation("probe.say_empty", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record SayEmpty(string Tenant) : IOperation<RequiredView>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class SayEmptyHandler : IOperationHandler<SayEmpty, RequiredView>
{
    public Task<Result<RequiredView>> HandleAsync(SayEmpty operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<RequiredView>>(new RequiredView(new RequiredText("")));
}

[Operation("probe.words", "member", Platform = true)]
public sealed record Words : ISubscription<string>;

internal sealed class WordsHandler : ISubscriptionHandler<Words, string>
{
    public async IAsyncEnumerable<string> SubscribeAsync(Words subscription, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return "one";
        yield return null!;
    }
}

[Operation("probe.maybe_said", "member", Platform = true)]
public sealed record MaybeSaid : IOperation<string?>;

internal sealed class MaybeSaidHandler : IOperationHandler<MaybeSaid, string?>
{
    public Task<Result<string?>> HandleAsync(MaybeSaid operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<string?>>((string?)null);
}
