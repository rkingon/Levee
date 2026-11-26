using Levee;

namespace Levee.Tests;

[Operation("probe.echo", "member", Summary = "Says it back.", Platform = true)]
public sealed record Echo(string Text) : IOperation<EchoView>;

public sealed record EchoView(string Said);

[Operation("probe.remember", "member", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record Remember(string Tenant, string Text, [property: Secret] string Pin) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

[Operation("probe.refuse", "member", Effects = Effects.Write, Idempotency = Idempotency.None, Platform = true, Errors = ["probe.refused"])]
public sealed record Refuse : IOperation<Unit>;

[Operation("probe.sneak", "member", Platform = true)]
public sealed record Sneak : IOperation<Unit>;

[Operation("platform.reset", "platform", Effects = Effects.Write, Idempotency = Idempotency.Idempotent, Platform = true)]
public sealed record Reset : IOperation<Unit>;

internal sealed class EchoHandler : IOperationHandler<Echo, EchoView>
{
    public Task<Result<EchoView>> HandleAsync(Echo operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<EchoView>>(new EchoView(operation.Text));
}

internal sealed class RememberHandler : IOperationHandler<Remember, Unit>
{
    public Task<Result<Unit>> HandleAsync(Remember operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

internal sealed class RefuseHandler : IOperationHandler<Refuse, Unit>
{
    public Task<Result<Unit>> HandleAsync(Refuse operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<Unit>>(Error.Validation("probe.refused", "No."));
}

internal sealed class SneakHandler : IOperationHandler<Sneak, Unit>
{
    public Task<Result<Unit>> HandleAsync(Sneak operation, OperationContext context, CancellationToken ct) =>
        throw new StoreRefusal(LeveeErrors.UndeclaredWrite);
}

internal sealed class ResetHandler : IOperationHandler<Reset, Unit>
{
    public Task<Result<Unit>> HandleAsync(Reset operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}
