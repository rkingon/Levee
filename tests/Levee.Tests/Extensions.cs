using Levee;

namespace Levee.Tests;

public enum Reach
{
    Inside,
    Outside,
}

/// <summary>An app's own metadata, as an agent platform's turn scope and disposition would be.</summary>
[OperationMetadata]
[AttributeUsage(AttributeTargets.Class)]
public sealed class TurnAttribute(Reach reach) : Attribute
{
    public Reach Reach { get; } = reach;
    public bool Pinned { get; set; }
}

[Operation("probe.speak", "member", Effects = Effects.External, Idempotency = Idempotency.Conflicts, Platform = true)]
[Turn(Reach.Outside, Pinned = true)]
public sealed record Speak(string Text) : IOperation<Unit>;

/// <summary>The platform's own: dispatched in-process, never projected.</summary>
[Operation("auth.sign_in", "anyone", Effects = Effects.Write, Idempotency = Idempotency.None, Platform = true, Internal = true)]
public sealed record SignIn([property: Secret] string Link) : IOperation<Unit>;

internal sealed class SpeakHandler : IOperationHandler<Speak, Unit>
{
    public Task<Result<Unit>> HandleAsync(Speak operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

internal sealed class SignInHandler : IOperationHandler<SignIn, Unit>
{
    public Task<Result<Unit>> HandleAsync(SignIn operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

internal sealed class Outcomes : IOperationOutcome
{
    public List<OperationOutcome> Seen { get; } = [];

    public Task RecordAsync(OperationOutcome outcome, CancellationToken ct)
    {
        Seen.Add(outcome);
        return Task.CompletedTask;
    }
}

/// <summary>A set: every adapter serves it.</summary>
[Port(Many = true)]
public interface IChannel
{
    string Name { get; }
}

[Adapter]
internal sealed class Slack : IChannel
{
    public string Name => "slack";
}

[Adapter]
internal sealed class Discord : IChannel
{
    public string Name => "discord";
}

/// <summary>One per process, as a held connection would be.</summary>
[Port]
public interface ISignals
{
    Guid Id { get; }
}

[Adapter(Lifetime = AdapterLifetime.Singleton)]
internal sealed class Signals : ISignals
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>Chosen by a setting, each adapter with its own lifetime.</summary>
[Port(SelectedBy = "Probe:Greeter")]
public interface IGreeter
{
    string Greet();
}

[Adapter("en")]
internal sealed class English : IGreeter
{
    public string Greet() => "hello";
}

[Adapter("fr", Lifetime = AdapterLifetime.Singleton)]
internal sealed class French : IGreeter
{
    public string Greet() => "bonjour";
}
