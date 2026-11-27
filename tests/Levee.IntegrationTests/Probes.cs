using Levee;
using Oxbow;
using Sample.Application;
using Sample.Contracts;
using Sample.Domain;

namespace Levee.IntegrationTests;

public sealed record Member(string Subject, string Tenant) : Principal(Subject);

/// <summary>An API key issued to one tenant: the scope is its credentials', never its input's.</summary>
public sealed record ApiKey(string Subject, string Tenant) : Principal(Subject), IScopedPrincipal
{
    string IScopedPrincipal.ScopeKey => Tenant;
}

/// <summary>
/// A member acts only in their own tenant, and so does a job they enqueued: this app's subjects name their tenant
/// (<c>ada@acme</c>), where a real one would look its member up. The platform acts anywhere.
/// </summary>
[Refuses("access.tenant")]
internal sealed class TenantAccess : IAccessPolicy
{
    public ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct) => ValueTask.FromResult(request.Principal switch
    {
        SystemPrincipal or JobPrincipal { System: true } => null,
        Member m when request.Scope.Key == m.Tenant => null,
        ApiKey k when request.Scope.Key == k.Tenant => null,
        JobPrincipal j when request.Scope.Key is { } key && j.Subject.EndsWith("@" + key, StringComparison.Ordinal) => null,
        _ => Error.Forbidden("access.tenant", "Not your tenant."),
    });
}

/// <summary>Declares only reads, then writes: the contract is wrong, and the database says so.</summary>
[Operation("probe.sneak", "member")]
public sealed record Sneak(string Tenant) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

/// <summary>Writes into another tenant from its own scope.</summary>
[Operation("probe.trespass", "member", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record Trespass(string Tenant, string Victim) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

/// <summary>Enqueues a job, then refuses: the job must go with it.</summary>
[Operation("probe.enqueue_then_refuse", "member", Effects = Effects.Write, Idempotency = Idempotency.None, Errors = ["probe.refused"])]
public sealed record EnqueueThenRefuse(string Tenant) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class SneakHandler(INotes notes) : IOperationHandler<Sneak, Unit>
{
    public Task<Result<Unit>> HandleAsync(Sneak operation, OperationContext context, CancellationToken ct)
    {
        notes.Add(Note.Create(Guid.CreateVersion7(), operation.Tenant, "sneaked", context.Now));
        return Task.FromResult<Result<Unit>>(Unit.Value);
    }
}

internal sealed class TrespassHandler(INotes notes) : IOperationHandler<Trespass, Unit>
{
    public Task<Result<Unit>> HandleAsync(Trespass operation, OperationContext context, CancellationToken ct)
    {
        notes.Add(Note.Create(Guid.CreateVersion7(), operation.Victim, "trespassed", context.Now));
        return Task.FromResult<Result<Unit>>(Unit.Value);
    }
}

internal sealed class EnqueueThenRefuseHandler(IJobs jobs) : IOperationHandler<EnqueueThenRefuse, Unit>
{
    public async Task<Result<Unit>> HandleAsync(EnqueueThenRefuse operation, OperationContext context, CancellationToken ct)
    {
        await jobs.EnqueueAsync(new CreateNote(Guid.CreateVersion7(), operation.Tenant, "never"), null, ct);
        return Error.Validation("probe.refused", "No.");
    }
}

/// <summary>Enqueues a note into another tenant: the enqueue is checked as the caller.</summary>
[Operation("probe.enqueue_elsewhere", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record EnqueueElsewhere(string Tenant, string Victim) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

public enum Unfit { Repeats, Secret, Streams }

/// <summary>Enqueues an operation that can't run as a job: one that isn't safe to repeat, carries a secret, or streams.</summary>
[Operation("probe.enqueue_unfit", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record EnqueueUnfit(string Tenant, Unfit Why) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

[Operation("probe.with_secret", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record WithSecret(string Tenant, [property: Secret] string Pin) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class EnqueueElsewhereHandler(IJobs jobs) : IOperationHandler<EnqueueElsewhere, Unit>
{
    public async Task<Result<Unit>> HandleAsync(EnqueueElsewhere operation, OperationContext context, CancellationToken ct)
    {
        var queued = await jobs.EnqueueAsync(new CreateNote(Guid.CreateVersion7(), operation.Victim, "planted"), null, ct);
        return queued.IsSuccess ? Unit.Value : queued.Error;
    }
}

internal sealed class EnqueueUnfitHandler(IJobs jobs) : IOperationHandler<EnqueueUnfit, Unit>
{
    public async Task<Result<Unit>> HandleAsync(EnqueueUnfit operation, OperationContext context, CancellationToken ct)
    {
        IOperation unfit = operation.Why switch
        {
            Unfit.Secret => new WithSecret(operation.Tenant, "1234"),
            Unfit.Streams => new WatchNotes(operation.Tenant),
            _ => new Trespass(operation.Tenant, operation.Tenant),
        };
        await jobs.EnqueueAsync(unfit, null, ct);
        return Unit.Value;
    }
}

[Operation("probe.watch_notes", "member")]
public sealed record WatchNotes(string Tenant) : ISubscription<int>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class WatchNotesHandler : ISubscriptionHandler<WatchNotes, int>
{
    public async IAsyncEnumerable<int> SubscribeAsync(WatchNotes subscription, SubscriptionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return 1;
    }
}

internal sealed class WithSecretHandler : IOperationHandler<WithSecret, Unit>
{
    public Task<Result<Unit>> HandleAsync(WithSecret operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<Unit>>(Unit.Value);
}

/// <summary>The app's own job kind, beside operations as jobs: Oxbow enlisted in the unit of work, whatever it inserts.</summary>
[JobKind("probe_ping")]
public sealed record Ping(string Tenant) : IJobArgs;

internal sealed class PingWorker : IWorker<Ping>
{
    public Task WorkAsync(Job<Ping> job, CancellationToken cancellationToken) => Task.CompletedTask;
}

[Operation("probe.ping_later", "member", Effects = Effects.Write, Idempotency = Idempotency.None, Errors = ["probe.refused"])]
public sealed record PingLater(string Tenant, bool Refuse) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class PingLaterHandler(IOxbowClient oxbow) : IOperationHandler<PingLater, Unit>
{
    public async Task<Result<Unit>> HandleAsync(PingLater operation, OperationContext context, CancellationToken ct)
    {
        await oxbow.InsertAsync(new Ping(operation.Tenant), cancellationToken: ct);
        return operation.Refuse ? Error.Validation("probe.refused", "No.") : Unit.Value;
    }
}

/// <summary>Writes, flushes, then reads its own write back: what a query after an add needs.</summary>
[Operation("probe.flush_then_read", "member", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record FlushThenRead(string Tenant) : IOperation<int>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class FlushThenReadHandler(INotes notes, IFlush flush) : IOperationHandler<FlushThenRead, int>
{
    public async Task<Result<int>> HandleAsync(FlushThenRead operation, OperationContext context, CancellationToken ct)
    {
        notes.Add(Note.Create(Guid.CreateVersion7(), operation.Tenant, "flushed", context.Now));
        await flush.FlushAsync(ct);
        return (await notes.ListAsync(ct)).Count;
    }
}

/// <summary>A kind another process works: this one only enqueues it.</summary>
[JobKind("probe_elsewhere")]
public sealed record Elsewhere(string Tenant) : IJobArgs;

[Operation("probe.enqueue_elsewhere_kind", "member", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record EnqueueElsewhereKind(string Tenant) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class EnqueueElsewhereKindHandler(IOxbowClient oxbow) : IOperationHandler<EnqueueElsewhereKind, Unit>
{
    public async Task<Result<Unit>> HandleAsync(EnqueueElsewhereKind operation, OperationContext context, CancellationToken ct)
    {
        await oxbow.InsertAsync(new Elsewhere(operation.Tenant), cancellationToken: ct);
        return Unit.Value;
    }
}

/// <summary>Who a job runs as, looked up again: the app's own principal.</summary>
internal sealed class RestoreMembers : IJobPrincipals
{
    public static readonly System.Collections.Concurrent.ConcurrentBag<string> Restored = [];

    public ValueTask<Principal> RestoreAsync(JobPrincipal job, CancellationToken ct)
    {
        // A key's job runs as the job itself: its scope is the one Levee carried from the enqueue.
        if (job.Subject.StartsWith("key_", StringComparison.Ordinal))
        {
            return ValueTask.FromResult<Principal>(job);
        }
        // A key whose lookup now finds it issued to another tenant.
        if (job.Subject.StartsWith("moved_", StringComparison.Ordinal))
        {
            return ValueTask.FromResult<Principal>(new ApiKey(job.Subject, "elsewhere"));
        }
        Restored.Add(job.Subject);
        return ValueTask.FromResult<Principal>(new Member(job.Subject, job.Subject[(job.Subject.IndexOf('@', StringComparison.Ordinal) + 1)..]));
    }
}

/// <summary>Writes a note in its caller's tenant: the key's, not anything the input says.</summary>
[Operation("probe.note_mine", "member", CallerScoped = true, Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record NoteMine(Guid Id, string Text) : IOperation<Unit>;

/// <summary>The same, later, as a job.</summary>
[Operation("probe.note_mine_later", "member", CallerScoped = true, Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record NoteMineLater(Guid Id, string Text) : IOperation<Unit>;

internal sealed class NoteMineHandler(INotes notes) : IOperationHandler<NoteMine, Unit>
{
    public Task<Result<Unit>> HandleAsync(NoteMine operation, OperationContext context, CancellationToken ct)
    {
        notes.Add(Note.Create(operation.Id, context.Scope.Key!, operation.Text, context.Now));
        return Task.FromResult<Result<Unit>>(Unit.Value);
    }
}

internal sealed class NoteMineLaterHandler(IJobs jobs) : IOperationHandler<NoteMineLater, Unit>
{
    public async Task<Result<Unit>> HandleAsync(NoteMineLater operation, OperationContext context, CancellationToken ct)
    {
        var enqueued = await jobs.EnqueueAsync(new NoteMine(operation.Id, operation.Text), null, ct);
        return enqueued.IsSuccess ? Unit.Value : enqueued.Error;
    }
}

/// <summary>A vendor that says when to come back.</summary>
[Operation("probe.vendor_busy", "member", CallerScoped = true, Effects = Effects.External, Idempotency = Idempotency.Idempotent, Errors = ["vendor.busy"])]
public sealed record VendorBusy : IOperation<Unit>;

[Operation("probe.vendor_busy_later", "member", CallerScoped = true, Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record VendorBusyLater : IOperation<Unit>;

internal sealed class VendorBusyHandler : IOperationHandler<VendorBusy, Unit>
{
    public Task<Result<Unit>> HandleAsync(VendorBusy operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<Unit>>(Error.Unavailable("vendor.busy", "Rate limited.", TimeSpan.FromMinutes(10)));
}

internal sealed class VendorBusyLaterHandler(IJobs jobs) : IOperationHandler<VendorBusyLater, Unit>
{
    public async Task<Result<Unit>> HandleAsync(VendorBusyLater operation, OperationContext context, CancellationToken ct)
    {
        var enqueued = await jobs.EnqueueAsync(new VendorBusy(), null, ct);
        return enqueued.IsSuccess ? Unit.Value : enqueued.Error;
    }
}

/// <summary>Raises a signal, then refuses if told to: the signal must go with it.</summary>
[Operation("probe.signal", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent, Errors = ["probe.refused"])]
public sealed record SignalProbe(string Tenant, string Key, bool Refuse) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class SignalProbeHandler(IChangeSignals signals) : IOperationHandler<SignalProbe, Unit>
{
    public async Task<Result<Unit>> HandleAsync(SignalProbe operation, OperationContext context, CancellationToken ct)
    {
        await signals.RaiseAsync("notes.changed", operation.Key, ct);
        return operation.Refuse ? Error.Validation("probe.refused", "No.") : Unit.Value;
    }
}
