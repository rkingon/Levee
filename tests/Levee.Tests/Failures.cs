using System.Runtime.CompilerServices;
using Levee;
using Microsoft.Extensions.DependencyInjection;

namespace Levee.Tests;

[Operation("probe.crash", "member", Platform = true)]
public sealed record Crash : IOperation<Unit>;

internal sealed class CrashHandler : IOperationHandler<Crash, Unit>
{
    public Task<Result<Unit>> HandleAsync(Crash operation, OperationContext context, CancellationToken ct) =>
        throw new InvalidOperationException("the handler's own bug");
}

[Operation("probe.busy", "member", Platform = true, Errors = ["probe.busy"])]
public sealed record Busy : IOperation<Unit>;

internal sealed class BusyHandler : IOperationHandler<Busy, Unit>
{
    public Task<Result<Unit>> HandleAsync(Busy operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<Unit>>(Error.Unavailable("probe.busy", "Busy.", TimeSpan.FromSeconds(30)));
}

[Operation("probe.crashing_ticks", "member", Platform = true)]
public sealed record CrashingTicks : ISubscription<Tick>;

internal sealed class CrashingTicksHandler : ISubscriptionHandler<CrashingTicks, Tick>
{
    public async IAsyncEnumerable<Tick> SubscribeAsync(CrashingTicks subscription, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return new Tick(1);
        throw new InvalidOperationException("the stream's own bug");
    }
}

public sealed class FailureTests
{
    private static readonly Member Ada = new("ada", "acme");
    private readonly Store _store = new();
    private readonly Outcomes _outcomes = new();

    private Dispatcher Build() => DispatcherTests.Services(_store, _outcomes).GetRequiredService<Dispatcher>();

    [Fact]
    public async Task A_handler_that_throws_is_internal_rolled_back_traced_and_told()
    {
        var result = await Build().SendAsync(new Crash(), Ada, CancellationToken.None);

        Assert.Equal((ErrorKind.Internal, LeveeErrors.Internal, true), (result.Error!.Kind, result.Error.Code, result.Error.Transient));
        Assert.Matches("^[0-9a-f]{32}$", result.Error.Trace);
        Assert.Contains(result.Error.Trace!, result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bug", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal((0, 1), (_store.Commits, _store.Disposed));
        Assert.Equal(result.Error, Assert.Single(_outcomes.Seen).Error);
    }

    [Fact]
    public async Task A_cancelled_call_stays_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build().SendAsync(new Wait(), Ada, cancelled.Token));
    }

    [Fact]
    public async Task A_subscription_that_throws_mid_stream_ends_with_an_internal_error()
    {
        var opened = await Build().SubscribeAsync(new CrashingTicks(), Ada, CancellationToken.None);
        var seen = new List<Tick>();

        var failed = await Assert.ThrowsAsync<OperationFailedException>(async () =>
        {
            await foreach (var tick in opened.Value)
            {
                seen.Add(tick);
            }
        });

        Assert.Equal([new Tick(1)], seen);
        Assert.Equal(ErrorKind.Internal, failed.Error.Kind);
        Assert.IsType<InvalidOperationException>(failed.InnerException);
    }

    [Theory]
    [InlineData(false, "probe.odd")]
    [InlineData(true, "internal")]
    public async Task An_undeclared_code_is_logged_or_in_tests_refused(bool refuse, string code)
    {
        var dispatcher = DispatcherTests.Services(_store, _outcomes, more: s => s.AddSingleton(new LeveeOptions { RefuseUndeclaredErrors = refuse }))
            .GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendAsync(new Odd(), Ada, CancellationToken.None);

        Assert.Equal(code, result.Error!.Code);
        Assert.Contains(refuse ? "failed with 'probe.odd', which it doesn't declare" : "Odd.", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Operations_declare_what_they_fail_with_and_Levees_own_come_on_top()
    {
        var busy = Levee.Generated.LeveeComposition.Catalog.Find("probe.busy")!;

        Assert.Equal(["probe.busy"], busy.Errors);
        Assert.True(busy.Declares("probe.busy") && busy.Declares(LeveeErrors.Conflict));
        Assert.False(busy.Declares("probe.refused"));
    }
}

[Operation("probe.odd", "member", Platform = true)]
public sealed record Odd : IOperation<Unit>;

internal sealed class OddHandler : IOperationHandler<Odd, Unit>
{
    // Built at run time, as a code from a domain method or a port is: the build can't see it.
    public Task<Result<Unit>> HandleAsync(Odd operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<Unit>>(Error.Conflict(string.Join('.', "probe", "odd"), "Odd."));
}

[Operation("probe.wait", "member", Platform = true)]
public sealed record Wait : IOperation<Unit>;

internal sealed class WaitHandler : IOperationHandler<Wait, Unit>
{
    public async Task<Result<Unit>> HandleAsync(Wait operation, OperationContext context, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return Unit.Value;
    }
}

[Operation("probe.forever", "member", Platform = true)]
public sealed record Forever : ISubscription<Tick>;

internal sealed class ForeverHandler : ISubscriptionHandler<Forever, Tick>
{
    public async IAsyncEnumerable<Tick> SubscribeAsync(Forever subscription, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new Tick(1);
        await Task.Delay(Timeout.Infinite, ct);
    }
}

[Operation("probe.firehose", "member", Platform = true)]
public sealed record Firehose : ISubscription<Tick>;

internal sealed class FirehoseHandler : ISubscriptionHandler<Firehose, Tick>
{
    public async IAsyncEnumerable<Tick> SubscribeAsync(Firehose subscription, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            ct.ThrowIfCancellationRequested();
            // Large and fast: the channel is never empty while the client reads.
            yield return new Tick(i);
            if (i % 1000 == 0)
            {
                await Task.Yield();
            }
        }
    }
}

[Operation("probe.mine", "member", CallerScoped = true)]
public sealed record Mine : IOperation<string>;

internal sealed class MineHandler : IOperationHandler<Mine, string>
{
    public Task<Result<string>> HandleAsync(Mine operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<string>>(context.Scope.Key ?? "");
}

public sealed class CallerScopeTests
{
    private readonly Store _store = new();

    [Fact]
    public async Task A_caller_scoped_operation_runs_in_its_callers_scope()
    {
        var dispatcher = DispatcherTests.Services(_store, new Outcomes()).GetRequiredService<Dispatcher>();

        var mine = await dispatcher.SendAsync(new Mine(), new Key("key_1", "acme"), CancellationToken.None);
        var unscoped = await dispatcher.SendAsync(new Mine(), new SystemPrincipal("cron"), CancellationToken.None);

        Assert.Equal("acme", mine.Value);
        Assert.Equal(new WorkScope("acme", ReadOnly: true), Assert.Single(_store.Begun));
        Assert.Equal((ErrorKind.Forbidden, LeveeErrors.CallerUnscoped), (unscoped.Error!.Kind, unscoped.Error.Code));
    }

    [Fact]
    public void A_job_runs_in_the_scope_its_caller_carried() =>
        Assert.Equal("acme", ((IScopedPrincipal)new JobPrincipal("key_1", System: false, Scope: "acme")).ScopeKey);
}

public sealed class RefusalTests
{
    [Fact]
    public void The_catalog_lists_what_the_policy_and_admissions_refuse_with_once() =>
        Assert.Equal(["access.platform", "access.tenant", "access.unknown", "admission.closed"], Levee.Generated.LeveeComposition.Catalog.Refusals);
}

[Operation("probe.register", "member", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record Register(string Handle, [property: Personal] string Email) : IOperation<string>;

internal sealed class RegisterHandler : IOperationHandler<Register, string>
{
    public Task<Result<string>> HandleAsync(Register operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<string>>(operation.Email);
}

public sealed class PersonalTests
{
    [Fact]
    public async Task Personal_data_reaches_the_handler_but_not_the_audit_or_the_outcome()
    {
        var store = new Store();
        var outcomes = new Outcomes();
        var dispatcher = DispatcherTests.Services(store, outcomes).GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendAsync(new Register("ada", "ada@example.com"), new Member("ada", null, Admin: true), CancellationToken.None);

        Assert.Equal("ada@example.com", result.Value);
        Assert.Equal("""{"handle":"ada","email":"[personal]"}""", Assert.Single(store.Audited).Input);
        Assert.DoesNotContain("ada@example.com", Assert.Single(outcomes.Seen).Input, StringComparison.Ordinal);
    }
}

[Operation("probe.quiet", "member", Platform = true)]
public sealed record Quiet : ISubscription<Tick>;

internal sealed class QuietHandler : ISubscriptionHandler<Quiet, Tick>
{
    public async IAsyncEnumerable<Tick> SubscribeAsync(Quiet subscription, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }
}

public sealed record Contact([property: Personal] string Email, string Kind);

[Operation("probe.sign_up", "member", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record SignUp(string Handle, Contact Contact, IReadOnlyList<Contact> Others,
    [property: Secret, System.Text.Json.Serialization.JsonPropertyName("pw")] string Password) : IOperation<Unit>;

internal sealed class SignUpHandler : IOperationHandler<SignUp, Unit>
{
    public Task<Result<Unit>> HandleAsync(SignUp operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

/// <summary>Streams until its reader stops, and says when it did.</summary>
[Operation("probe.watched", "member", Platform = true)]
public sealed record Watched : ISubscription<Tick>;

internal sealed class WatchedHandler : ISubscriptionHandler<Watched, Tick>
{
    public static readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<Tick> SubscribeAsync(Watched subscription, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            for (var i = 1; ; i++)
            {
                yield return new Tick(i);
                await Task.Delay(10, ct);
            }
        }
        finally
        {
            Stopped.TrySetResult();
        }
    }
}

public sealed class AuditFixTests
{
    private static readonly Member Admin = new("ada", "acme", Admin: true);

    [Fact]
    public async Task Redaction_reaches_nested_records_lists_and_renamed_fields()
    {
        var store = new Store();
        var outcomes = new Outcomes();
        var dispatcher = DispatcherTests.Services(store, outcomes).GetRequiredService<Dispatcher>();

        await dispatcher.SendAsync(new SignUp("ada", new Contact("ada@example.com", "work"), [new Contact("bo@example.com", "home")], "hunter2"), Admin, CancellationToken.None);

        var input = Assert.Single(store.Audited).Input;
        Assert.DoesNotContain("example.com", input, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", input, StringComparison.Ordinal);
        Assert.Contains("\"pw\":\"[secret]\"", input, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"work\"", input, StringComparison.Ordinal);
        Assert.Equal("contact.email", LeveeJson.FindSecret(typeof(Nested)));
    }

    public sealed record Nested(Inner Contact);

    public sealed record Inner([property: Secret] string Email);

    [Fact]
    public async Task A_subscription_is_admitted_like_any_operation()
    {
        var dispatcher = DispatcherTests.Services(new Store(), new Outcomes(), more: s => s.AddScoped<IAdmission, RefuseEverything>()).GetRequiredService<Dispatcher>();

        var opened = await dispatcher.SubscribeAsync(new Ticks(2), Admin, CancellationToken.None);

        Assert.Equal("admission.closed", opened.Error!.Code);
    }

    [Fact]
    public async Task A_reader_that_stops_stops_the_handler()
    {
        var dispatcher = DispatcherTests.Services(new Store(), new Outcomes()).GetRequiredService<Dispatcher>();
        var opened = await dispatcher.SubscribeFromClientAsync("probe.watched", default, Admin, CancellationToken.None);
        using var reading = new CancellationTokenSource();

        await foreach (var _ in opened.Value.WithCancellation(reading.Token))
        {
            await reading.CancelAsync();
            break;
        }

        await WatchedHandler.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_cancelled_operation_is_still_told_to_the_outcome()
    {
        var outcomes = new Outcomes();
        var dispatcher = DispatcherTests.Services(new Store(), outcomes).GetRequiredService<Dispatcher>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.SendAsync(new Wait(), Admin, cancelled.Token));

        Assert.Equal(LeveeErrors.Cancelled, Assert.Single(outcomes.Seen).Error!.Code);
    }

    [Fact]
    public void A_default_result_is_no_success_and_no_one_makes_an_internal_error()
    {
        Result<int> unset = default;

        Assert.False(unset.IsSuccess);
        Assert.Equal((ErrorKind.Internal, LeveeErrors.Internal), (unset.Error.Kind, unset.Error.Code));
        Assert.Throws<ArgumentException>(() => Error.Of(ErrorKind.Internal, "internal", "Forged."));
        Assert.Equal(ErrorKind.Conflict, Error.Of(ErrorKind.Conflict, "notes.taken", "Taken.").Kind);
    }
}

[Operation("probe.cancel_self", "member", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record CancelSelf : IOperation<Unit>;

internal sealed class CancelSelfHandler : IOperationHandler<CancelSelf, Unit>
{
    public static CancellationTokenSource Caller { get; set; } = new();

    public async Task<Result<Unit>> HandleAsync(CancelSelf operation, OperationContext context, CancellationToken ct)
    {
        // The caller gives up as the work finishes: what's done is done.
        await Caller.CancelAsync();
        return Unit.Value;
    }
}

[System.Text.Json.Serialization.JsonDerivedType(typeof(EmailContact), "email")]
public abstract record ContactMethod;

public sealed record EmailContact([property: Personal] string Address) : ContactMethod;

[Operation("probe.reach", "member", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record ReachOut(ContactMethod Contact) : IOperation<Unit>;

internal sealed class ReachOutHandler : IOperationHandler<ReachOut, Unit>
{
    public Task<Result<Unit>> HandleAsync(ReachOut operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

public sealed class SecondAuditFixTests
{
    private static readonly Member Admin = new("ada", "acme", Admin: true);

    [Fact]
    public async Task An_operation_cancelled_after_it_commits_answers_its_result()
    {
        var store = new Store();
        var dispatcher = DispatcherTests.Services(store, new Outcomes()).GetRequiredService<Dispatcher>();
        CancelSelfHandler.Caller = new CancellationTokenSource();

        var result = await dispatcher.SendAsync(new CancelSelf(), Admin, CancelSelfHandler.Caller.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public async Task A_derived_type_is_redacted_as_itself()
    {
        var store = new Store();
        var dispatcher = DispatcherTests.Services(store, new Outcomes()).GetRequiredService<Dispatcher>();

        await dispatcher.SendAsync(new ReachOut(new EmailContact("ada@example.com")), Admin, CancellationToken.None);

        Assert.DoesNotContain("example.com", Assert.Single(store.Audited).Input, StringComparison.Ordinal);
    }
}

internal sealed class TimingOut : IOperationOutcome
{
    public Task RecordAsync(OperationOutcome outcome, CancellationToken ct) => throw new TaskCanceledException("The recorder's own HTTP call timed out.");
}

public sealed record Visitor([property: Personal] string Email);

[Operation("probe.visits", "member", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record LogVisits(Dictionary<DateOnly, Visitor> ByDay, Dictionary<Tier, Visitor> ByTier, IReadOnlyDictionary<string, Visitor> ByName) : IOperation<Unit>;

public enum Tier { Gold }

internal sealed class LogVisitsHandler : IOperationHandler<LogVisits, Unit>
{
    public Task<Result<Unit>> HandleAsync(LogVisits operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

public sealed class ThirdAuditFixTests
{
    private static readonly Member Admin = new("ada", "acme", Admin: true);

    [Fact]
    public async Task A_recorder_timing_out_after_the_commit_leaves_the_result_standing()
    {
        var store = new Store();
        var dispatcher = DispatcherTests.Services(store, new Outcomes(), more: s => s.AddSingleton<IOperationOutcome, TimingOut>()).GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendAsync(new CancelSelf(), Admin, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public async Task Personal_data_under_any_dictionary_key_is_redacted()
    {
        var store = new Store();
        var dispatcher = DispatcherTests.Services(store, new Outcomes()).GetRequiredService<Dispatcher>();

        await dispatcher.SendAsync(new LogVisits(
            new() { [new DateOnly(2026, 1, 1)] = new("ada@example.com") },
            new() { [Tier.Gold] = new("bob@example.com") },
            new Dictionary<string, Visitor> { ["Cy"] = new("cy@example.com") }), Admin, CancellationToken.None);

        var audited = Assert.Single(store.Audited).Input;
        Assert.DoesNotContain("example.com", audited, StringComparison.Ordinal);
        Assert.Contains("2026-01-01", audited, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expando_is_redacted_by_each_value_it_holds()
    {
        System.Dynamic.ExpandoObject bag = new();
        ((IDictionary<string, object?>)bag)["who"] = new Visitor("bob@example.com");

        Assert.DoesNotContain("example.com", LeveeJson.Redact(bag, typeof(System.Dynamic.ExpandoObject)), StringComparison.Ordinal);
    }
}

internal sealed class BrokenToBuild : IOperationOutcome
{
    public BrokenToBuild() => throw new InvalidOperationException("A dependency the recorder needs isn't registered.");

    public Task RecordAsync(OperationOutcome outcome, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class BrokenToDispose : IOperationOutcome, IAsyncDisposable
{
    public Task RecordAsync(OperationOutcome outcome, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => throw new InvalidOperationException("The recorder's flush failed.");
}

[System.Text.Json.Serialization.JsonDerivedType(typeof(Card), "card")]
public abstract record PaymentMethod
{
    [Secret] public abstract string Token { get; init; }
}

public sealed record Card(string Token, string Last4) : PaymentMethod;

public interface IReachable
{
    [Personal] string Email { get; }
}

public sealed record Reachable(string Email) : IReachable;

[Operation("probe.pay", "member", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record AddPayment(PaymentMethod Method, Reachable Owner) : IOperation<Unit>;

internal sealed class AddPaymentHandler : IOperationHandler<AddPayment, Unit>
{
    public Task<Result<Unit>> HandleAsync(AddPayment operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

public sealed class FourthAuditFixTests
{
    private static readonly Member Admin = new("ada", "acme", Admin: true);

    [Theory]
    [InlineData(typeof(BrokenToBuild))]
    [InlineData(typeof(BrokenToDispose))]
    public async Task A_recorder_broken_to_build_or_dispose_leaves_the_result_standing(Type recorder)
    {
        var store = new Store();
        var dispatcher = DispatcherTests.Services(store, new Outcomes(), more: s => s.AddScoped(typeof(IOperationOutcome), recorder)).GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendAsync(new CancelSelf(), Admin, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public async Task A_mark_on_an_abstract_or_interface_member_holds_for_its_implementation()
    {
        var store = new Store();
        var dispatcher = DispatcherTests.Services(store, new Outcomes()).GetRequiredService<Dispatcher>();

        await dispatcher.SendAsync(new AddPayment(new Card("tok_live_SECRET", "4242"), new Reachable("ada@example.com")), Admin, CancellationToken.None);

        var audited = Assert.Single(store.Audited).Input;
        Assert.DoesNotContain("tok_live_SECRET", audited, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", audited, StringComparison.Ordinal);
        Assert.Contains("4242", audited, StringComparison.Ordinal);
        Assert.Equal(true, LeveeJson.Schema(typeof(Card))["properties"]!["token"]!["writeOnly"]?.GetValue<bool>());
    }
}

[Refuses]
internal sealed class ThrowingAccess : IAccessPolicy
{
    public ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct) => throw new InvalidOperationException("The policy's own bug.");
}

internal sealed class Hanging : IOperationOutcome
{
    // Ignores its token, as a recorder blocked on a lock would.
    public Task RecordAsync(OperationOutcome outcome, CancellationToken ct) => Task.Delay(Timeout.Infinite, CancellationToken.None);
}

[Operation("probe.strict", "member", Platform = true)]
public sealed record Strict(string Text) : IOperation<Unit>
{
    public string Text { get; } = string.IsNullOrEmpty(Text) ? throw new ArgumentException("Text is required.", nameof(Text)) : Text;
}

internal sealed class StrictHandler : IOperationHandler<Strict, Unit>
{
    public Task<Result<Unit>> HandleAsync(Strict operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

[Operation("probe.computed", "member", Platform = true)]
public sealed record Computed(string Email) : IOperation<Unit>
{
    public string Domain => Email.Split('@')[1];
}

internal sealed class ComputedHandler : IOperationHandler<Computed, Unit>
{
    public Task<Result<Unit>> HandleAsync(Computed operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

public sealed class FifthAuditFixTests
{
    private static readonly Member Admin = new("ada", "acme", Admin: true);

    private static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task A_policy_that_throws_is_a_bug_when_a_subscription_opens_or_is_rechecked()
    {
        var dispatcher = DispatcherTests.Services(new Store(), new Outcomes(), more: s => s.AddSingleton<IAccessPolicy, ThrowingAccess>()).GetRequiredService<Dispatcher>();

        var opened = await dispatcher.SubscribeFromClientAsync("probe.ticks", Json("""{"count":1}"""), Admin, CancellationToken.None);
        var rechecked = await dispatcher.AuthorizeFromClientAsync("probe.ticks", Json("""{"count":1}"""), Admin, CancellationToken.None);

        Assert.Equal(ErrorKind.Internal, opened.Error!.Kind);
        Assert.Equal(ErrorKind.Internal, rechecked?.Kind);
    }

    [Fact]
    public async Task Input_its_own_type_refuses_is_invalid_input()
    {
        var dispatcher = DispatcherTests.Services(new Store(), new Outcomes()).GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendFromClientAsync("probe.strict", Json("""{"text":""}"""), Admin, CancellationToken.None);

        Assert.Equal(LeveeErrors.InvalidInput, result.Error!.Code);
    }

    [Fact]
    public async Task An_input_that_cant_be_serialized_still_answers_and_is_still_told()
    {
        var outcomes = new Outcomes();
        var dispatcher = DispatcherTests.Services(new Store(), outcomes).GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendAsync(new Computed("no-at-sign"), Admin, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(outcomes.Seen, o => o.Operation.Name == "probe.computed");
    }

    [Fact]
    public async Task A_recorder_that_hangs_is_left_behind()
    {
        var store = new Store();
        var dispatcher = DispatcherTests.Services(store, new Outcomes(), more: s => s
            .AddSingleton<IOperationOutcome, Hanging>()
            .AddSingleton(new LeveeOptions { OutcomeTimeout = TimeSpan.FromMilliseconds(100) })).GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendAsync(new CancelSelf(), Admin, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, store.Commits);
    }
}

public class ContactBase
{
    public string Email { get; init; } = "";
}

public sealed class ViaBase : ContactBase, IReachable;

public sealed class MarksTests
{
    [Fact]
    public void A_mark_on_an_interface_holds_for_a_base_class_property_that_implements_it()
    {
        Assert.DoesNotContain("example.com", LeveeJson.Redact(new ViaBase { Email = "ada@example.com" }, typeof(ViaBase)), StringComparison.Ordinal);
    }
}

public sealed class ValueObjectValidationException(string message) : Exception(message);

public sealed record Email
{
    public Email(string value) => Value = value.Contains('@', StringComparison.Ordinal) ? value : throw new ValueObjectValidationException("Not an email.");

    public string Value { get; }
}

[Operation("probe.validated", "member", Platform = true)]
public sealed record Validated(Email Email) : IOperation<Unit>;

internal sealed class ValidatedHandler : IOperationHandler<Validated, Unit>
{
    public Task<Result<Unit>> HandleAsync(Validated operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

public sealed record Unserializable
{
    public string Broken => throw new InvalidOperationException("A getter's bug.");
}

[Operation("probe.unserializable", "member", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record AnswerUnserializable : IOperation<Unserializable>;

internal sealed class AnswerUnserializableHandler : IOperationHandler<AnswerUnserializable, Unserializable>
{
    public Task<Result<Unserializable>> HandleAsync(AnswerUnserializable operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<Unserializable>>(new Unserializable());
}

internal sealed class Blocking : IOperationOutcome
{
    public Task RecordAsync(OperationOutcome outcome, CancellationToken ct)
    {
        Thread.Sleep(3000);
        return Task.CompletedTask;
    }
}

public sealed class SixthAuditFixTests
{
    private static readonly Member Admin = new("ada", "acme", Admin: true);

    private static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Any_exception_from_an_inputs_own_type_is_invalid_input()
    {
        var dispatcher = DispatcherTests.Services(new Store(), new Outcomes()).GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendFromClientAsync("probe.validated", Json("""{"email":{"value":"nope"}}"""), Admin, CancellationToken.None);

        Assert.Equal(LeveeErrors.InvalidInput, result.Error!.Code);
    }

    [Fact]
    public async Task A_result_that_cant_be_serialized_rolls_the_write_back()
    {
        var store = new Store();
        var dispatcher = DispatcherTests.Services(store, new Outcomes()).GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendFromClientAsync("probe.unserializable", Json("{}"), Admin, CancellationToken.None);

        Assert.Equal(ErrorKind.Internal, result.Error!.Kind);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task A_recorder_that_blocks_is_bounded_too()
    {
        var dispatcher = DispatcherTests.Services(new Store(), new Outcomes(), more: s => s
            .AddSingleton<IOperationOutcome, Blocking>()
            .AddSingleton(new LeveeOptions { OutcomeTimeout = TimeSpan.FromMilliseconds(100) })).GetRequiredService<Dispatcher>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await dispatcher.SendAsync(new CancelSelf(), Admin, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"{clock.Elapsed}");
    }
}

public sealed record Leaky
{
    public Leaky(string value) => Value = value.Length > 0 ? value : throw new InvalidOperationException("connection=Host=db;Password=hunter2");

    public string Value { get; }
}

[Operation("probe.leaky", "member", Platform = true)]
public sealed record LeakyInput(Leaky Value) : IOperation<Unit>;

internal sealed class LeakyInputHandler : IOperationHandler<LeakyInput, Unit>
{
    public Task<Result<Unit>> HandleAsync(LeakyInput operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

internal sealed class SlowToBuild : IOperationOutcome
{
    public SlowToBuild() => Thread.Sleep(3000);

    public Task RecordAsync(OperationOutcome outcome, CancellationToken ct) => Task.CompletedTask;
}

public sealed class SeventhAuditFixTests
{
    private static readonly Member Admin = new("ada", "acme", Admin: true);

    [Fact]
    public async Task An_inputs_own_exception_tells_the_client_only_that_it_was_refused()
    {
        var dispatcher = DispatcherTests.Services(new Store(), new Outcomes()).GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendFromClientAsync("probe.leaky", System.Text.Json.JsonDocument.Parse("""{"value":{"value":""}}""").RootElement.Clone(), Admin, CancellationToken.None);

        Assert.Equal(LeveeErrors.InvalidInput, result.Error!.Code);
        Assert.DoesNotContain("hunter2", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recorder_slow_to_build_is_bounded_too()
    {
        var dispatcher = DispatcherTests.Services(new Store(), new Outcomes(), more: s => s
            .AddScoped<IOperationOutcome, SlowToBuild>()
            .AddSingleton(new LeveeOptions { OutcomeTimeout = TimeSpan.FromMilliseconds(100) })).GetRequiredService<Dispatcher>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await dispatcher.SendAsync(new CancelSelf(), Admin, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"{clock.Elapsed}");
    }
}

[Operation("probe.parsed_scope", "member")]
public sealed record ParsedScope(string Tenant) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Guid.Parse(Tenant).ToString();
}

internal sealed class ParsedScopeHandler : IOperationHandler<ParsedScope, Unit>
{
    public Task<Result<Unit>> HandleAsync(ParsedScope operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

[Refuses]
internal sealed class CancelledAccess(CancellationTokenSource caller) : IAccessPolicy
{
    public async ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct)
    {
        await caller.CancelAsync();
        ct.ThrowIfCancellationRequested();
        return null;
    }
}

public sealed class NinthAuditFixTests
{
    private static readonly Member Admin = new("ada", "acme", Admin: true);

    [Fact]
    public async Task A_scope_key_that_throws_is_a_bug_answered_as_one()
    {
        var dispatcher = DispatcherTests.Services(new Store(), new Outcomes()).GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendAsync(new ParsedScope("not-a-guid"), Admin, CancellationToken.None);

        Assert.Equal(ErrorKind.Internal, result.Error!.Kind);
    }

    [Fact]
    public async Task A_subscription_cancelled_as_it_opens_is_told()
    {
        var outcomes = new Outcomes();
        using var caller = new CancellationTokenSource();
        var dispatcher = DispatcherTests.Services(new Store(), outcomes, more: s => s.AddSingleton<IAccessPolicy>(new CancelledAccess(caller))).GetRequiredService<Dispatcher>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.SubscribeFromClientAsync("probe.ticks",
            System.Text.Json.JsonDocument.Parse("""{"count":1}""").RootElement.Clone(), Admin, caller.Token));

        Assert.Equal(LeveeErrors.Cancelled, Assert.Single(outcomes.Seen).Error?.Code);
    }
}

[Operation("probe.mine_stream", "member", CallerScoped = true)]
public sealed record WatchMine : ISubscription<Tick>;

internal sealed class WatchMineHandler : ISubscriptionHandler<WatchMine, Tick>
{
    public async IAsyncEnumerable<Tick> SubscribeAsync(WatchMine subscription, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new Tick(1);
        await Task.Delay(Timeout.Infinite, ct);
    }
}

public interface IHolder
{
    [Personal, System.Text.Json.Serialization.JsonPropertyName("holder_email")] string Email { get; }
}

public sealed record Holder(string Email, string Plan) : IHolder;

public interface IPayer
{
    string Card { get; }
}

public sealed record Payer([property: Secret] string Card) : IPayer;

public sealed record Account(IHolder Holder, IPayer Payer);

public sealed class TwelfthAuditFixTests
{
    [Fact]
    public void A_member_written_through_an_interface_is_redacted_by_the_interfaces_wire_name_and_either_mark()
    {
        var audited = LeveeJson.Redact(new Account(new Holder("ada@example.com", "gold"), new Payer("4242424242424242")), typeof(Account));

        Assert.DoesNotContain("example.com", audited, StringComparison.Ordinal);
        Assert.DoesNotContain("4242", audited, StringComparison.Ordinal);
        Assert.Contains("\"holder_email\":\"[personal]\"", audited, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_never_written_holds_no_secret()
    {
        Assert.Null(LeveeJson.FindSecret(typeof(Unwritten)));
    }

    public sealed record Unwritten(string Name)
    {
        [Secret, System.Text.Json.Serialization.JsonIgnore] public string? Kept { get; init; }
    }
}

public readonly record struct ContactValue([property: Personal] string Email);

public sealed record MaybeContact(ContactValue? Primary);

[System.Text.Json.Serialization.JsonPolymorphic(UnknownDerivedTypeHandling = System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Known), "known")]
public interface IShape
{
    [System.Text.Json.Serialization.JsonPropertyName("shape_owner")] string Owner { get; }
}

public sealed record Known(string Owner) : IShape;

public sealed record Unlisted([property: Personal] string Owner) : IShape;

public sealed record Drawing(IShape Shape);

public sealed record Pay(IPayer Payer);

public sealed class ThirteenthAuditFixTests
{
    [Fact]
    public void A_marked_member_inside_a_nullable_value_is_redacted()
    {
        Assert.DoesNotContain("example.com", LeveeJson.Redact(new MaybeContact(new ContactValue("ada@example.com")), typeof(MaybeContact)), StringComparison.Ordinal);
        Assert.Contains("\"primary\":null", LeveeJson.Redact(new MaybeContact(null), typeof(MaybeContact)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_the_serializer_falls_back_from_is_redacted_by_what_it_writes()
    {
        var audited = LeveeJson.Redact(new Drawing(new Unlisted("ada@example.com")), typeof(Drawing));

        Assert.DoesNotContain("example.com", audited, StringComparison.Ordinal);
        Assert.Contains("\"shape_owner\":\"[personal]\"", audited, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_only_an_implementation_marks_is_found_in_the_value()
    {
        Assert.Null(LeveeJson.FindSecret(typeof(Pay)));
        Assert.Equal("card", LeveeJson.FindSecretIn(new Pay(new Payer("4242")), typeof(Pay)));
    }
}

public interface INamed
{
    [Personal] string Email { get; }
}

public interface IReachableBy
{
    string Email { get; }
}

public sealed class Person : INamed, IReachableBy
{
    public string Email { get; init; } = "";
}

public sealed record ViaSibling(IReachableBy Who);

[System.Text.Json.Serialization.JsonConverter(typeof(EmailAddressConverter))]
public sealed record EmailAddress([property: Secret] string Value);

public sealed class EmailAddressConverter : System.Text.Json.Serialization.JsonConverter<EmailAddress>
{
    public override EmailAddress Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(System.Text.Json.Utf8JsonWriter writer, EmailAddress value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public sealed record Mailing(EmailAddress To, IReadOnlyList<EmailAddress> Cc);

public enum Level { Low, High }

public class Settings
{
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Level>))]
    public Level Level { get; init; }

    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString)]
    public int Count { get; init; }

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}

public sealed record Configured(Settings Settings);

public sealed class FourteenthAuditFixTests
{
    [Fact]
    public void A_mark_on_a_sibling_interface_holds_through_the_other()
    {
        Assert.DoesNotContain("example.com", LeveeJson.Redact(new ViaSibling(new Person { Email = "ada@example.com" }), typeof(ViaSibling)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_its_own_converter_writes_whole_is_marked_whole()
    {
        var mailing = new Mailing(new EmailAddress("ada@example.com"), [new EmailAddress("bob@example.com")]);

        var audited = LeveeJson.Redact(mailing, typeof(Mailing));

        Assert.DoesNotContain("example.com", audited, StringComparison.Ordinal);
        Assert.Equal("EmailAddress", LeveeJson.FindSecret(typeof(EmailAddress)));
        Assert.NotNull(LeveeJson.FindSecretIn(mailing, typeof(Mailing)));
    }

    [Fact]
    public void Redaction_writes_what_the_serializer_would_wherever_nothing_is_marked()
    {
        var settings = new Configured(new Settings
        {
            Level = Level.High,
            Count = 7,
            Extra = new() { ["note"] = System.Text.Json.JsonSerializer.SerializeToElement("kept") },
        });

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(settings, LeveeJson.Options), LeveeJson.Redact(settings, typeof(Configured)));
    }
}

public sealed record Credential([property: Secret] string Token);

[System.Text.Json.Serialization.JsonConverter(typeof(VaultConverter))]
public sealed record Vault([property: Personal] Credential Owner);

public sealed class VaultConverter : System.Text.Json.Serialization.JsonConverter<Vault>
{
    public override Vault Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        new(new Credential(reader.GetString()!));

    public override void Write(System.Text.Json.Utf8JsonWriter writer, Vault value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Owner.Token);
}

[System.Text.Json.Serialization.JsonConverter(typeof(HolderConverter))]
public sealed class FieldHolder
{
    [System.Text.Json.Serialization.JsonInclude] internal Credential? Kept = null;
}

public sealed class HolderConverter : System.Text.Json.Serialization.JsonConverter<FieldHolder>
{
    public override FieldHolder Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) => new();

    public override void Write(System.Text.Json.Utf8JsonWriter writer, FieldHolder value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Kept?.Token);
}

public sealed record Counts(int[] Values, byte[] Raw);

public record Tallies
{
    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString)]
    public List<int> Values { get; init; } = [];

    [System.Text.Json.Serialization.JsonConverter(typeof(AbsentConverter))]
    public string? Note { get; init; }
}

public sealed class AbsentConverter : System.Text.Json.Serialization.JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) => reader.GetString();

    public override void Write(System.Text.Json.Utf8JsonWriter writer, string? value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value ?? "absent");
}

public record Shown
{
    public string Name { get; init; } = "visible";
}

public sealed record Hiding : Shown
{
    [Personal] public new string Name { get; init; } = "hidden";
}

public sealed record Shows(Shown Item);

public sealed class FifteenthAuditFixTests
{
    [Fact]
    public void A_secret_below_a_personal_member_of_an_opaque_value_is_a_secret()
    {
        Assert.Equal("Vault", LeveeJson.FindSecret(typeof(Vault)));
        Assert.DoesNotContain("tok", LeveeJson.Redact(new Vault(new Credential("tok_live")), typeof(Vault)), StringComparison.Ordinal);
        Assert.Equal("FieldHolder", LeveeJson.FindSecret(typeof(FieldHolder)));
    }

    [Fact]
    public void Arrays_redact_as_their_elements()
    {
        var counts = new Counts([1, 2], [3]);

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(counts, LeveeJson.Options), LeveeJson.Redact(counts, typeof(Counts)));
    }

    [Fact]
    public void Unmarked_members_redact_as_the_serializer_writes_them()
    {
        var tallies = new Tallies { Values = [1, 2] };
        var shows = new Shows(new Hiding());

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(tallies, LeveeJson.Options), LeveeJson.Redact(tallies, typeof(Tallies)));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(shows, LeveeJson.Options), LeveeJson.Redact(shows, typeof(Shows)));
    }

    [Fact]
    public void Preserved_references_redact_as_the_serializer_writes_them()
    {
        var options = LeveeJson.Create(o => o.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.Preserve);
        var shared = new Shown();
        var both = new[] { shared, shared };

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(both, options), LeveeJson.Redact(both, typeof(Shown[]), options));
    }
}

[Operation("probe.tally", "member", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record Tally(int[] Values, byte[] Raw) : IOperation<Unit>;

internal sealed class TallyHandler : IOperationHandler<Tally, Unit>
{
    public Task<Result<Unit>> HandleAsync(Tally operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

public sealed class ArrayWriteTests
{
    [Fact]
    public async Task A_write_with_arrays_commits_with_its_audit()
    {
        var store = new Store();
        var dispatcher = DispatcherTests.Services(store, new Outcomes()).GetRequiredService<Dispatcher>();

        var result = await dispatcher.SendAsync(new Tally([1, 2], [3]), new Member("ada", "acme", Admin: true), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Contains("[1,2]", Assert.Single(store.Audited).Input, StringComparison.Ordinal);
    }
}

public sealed class SchemaTests
{
    [Fact]
    public void A_value_its_own_converter_writes_promises_no_shape()
    {
        var schema = LeveeJson.Schema(typeof(Mailing));

        Assert.Empty((System.Text.Json.Nodes.JsonObject)schema["properties"]!["to"]!);
    }
}

public interface ILabelled
{
    string Label { get; }
}

public sealed record Labelled([property: Personal] string Label, List<int> Counts) : ILabelled;

public sealed record Labels(ILabelled Item, List<int> Counts);

public sealed class NumbersAsStringsTests
{
    [Fact]
    public void Numbers_written_as_strings_keep_both_their_format_and_every_mark()
    {
        var options = LeveeJson.Create(o => o.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.WriteAsString);

        var audited = LeveeJson.Redact(new Labels(new Labelled("Example label", [1]), [7]), typeof(Labels), options);

        Assert.DoesNotContain("Example label", audited, StringComparison.Ordinal);
        Assert.Contains("\"counts\":[\"7\"]", audited, StringComparison.Ordinal);
    }
}
