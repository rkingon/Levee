using System.Runtime.CompilerServices;
using Levee;

namespace Levee.Tests;

public sealed record Tick(int Number);

[Operation("probe.ticks", "member", Summary = "Counts.", Platform = true)]
public sealed record Ticks(int Count) : ISubscription<Tick>;

internal sealed class TicksHandler : ISubscriptionHandler<Ticks, Tick>
{
    public async IAsyncEnumerable<Tick> SubscribeAsync(Ticks subscription, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 1; i <= subscription.Count; i++)
        {
            await Task.Yield();
            yield return new Tick(i);
        }
    }
}

[Operation("probe.maybe", "member", Platform = true)]
public sealed record Maybe(bool Some) : IOperation<EchoView?>;

internal sealed class MaybeHandler : IOperationHandler<Maybe, EchoView?>
{
    public Task<Result<EchoView?>> HandleAsync(Maybe operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<EchoView?>>(operation.Some ? new EchoView("yes") : null);
}

[Operation("probe.now", "member", Platform = true)]
public sealed record Now : IOperation<DateTimeOffset>;

internal sealed class NowHandler : IOperationHandler<Now, DateTimeOffset>
{
    public Task<Result<DateTimeOffset>> HandleAsync(Now operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<DateTimeOffset>>(context.Now);
}

/// <summary>A routed set: named adapters, one taken by name.</summary>
[Port(Many = true)]
public interface ILauncher
{
    string Name { get; }
}

[Adapter("docker")]
internal sealed class DockerLauncher : ILauncher
{
    public string Name => "docker";
}

[Adapter("ecs")]
internal sealed class EcsLauncher : ILauncher
{
    public string Name => "ecs";
}

/// <summary>Two facets of one adapter.</summary>
[Port]
public interface IListening
{
    Guid Id { get; }
}

[Port]
public interface ITyping
{
    Guid Id { get; }
}

[Adapter(Lifetime = AdapterLifetime.Singleton)]
internal sealed class Chat : IListening, ITyping
{
    public Guid Id { get; } = Guid.NewGuid();
}
