namespace Levee;

public interface IOperationHandler<in TOperation, TResult>
    where TOperation : IOperation<TResult>
{
    Task<Result<TResult>> HandleAsync(TOperation operation, OperationContext context, CancellationToken ct);
}

/// <summary>Streams a subscription's items, for as long as its caller reads them. It holds no transaction: to read data, dispatch an operation.</summary>
public interface ISubscriptionHandler<in TSubscription, out TItem>
    where TSubscription : ISubscription<TItem>
{
    IAsyncEnumerable<TItem> SubscribeAsync(TSubscription subscription, SubscriptionContext context, CancellationToken ct);
}

public sealed class OperationContext(Principal principal, OperationDescriptor descriptor, WorkScope scope, DateTimeOffset now)
{
    public Principal Principal { get; } = principal;
    public OperationDescriptor Descriptor { get; } = descriptor;
    public WorkScope Scope { get; } = scope;

    /// <summary>The operation's one time, at the store's precision (microseconds), so what's written reads back equal.</summary>
    public DateTimeOffset Now { get; } = now;

    /// <summary>The caller as the app's own principal type.</summary>
    public TPrincipal PrincipalAs<TPrincipal>()
        where TPrincipal : Principal =>
        Principal as TPrincipal ?? throw new InvalidOperationException($"{Descriptor.Name} runs as a {Principal.GetType().Name}, not a {typeof(TPrincipal).Name}.");
}

public sealed class SubscriptionContext(Principal principal, OperationDescriptor descriptor, WorkScope scope)
{
    public Principal Principal { get; } = principal;
    public OperationDescriptor Descriptor { get; } = descriptor;
    public WorkScope Scope { get; } = scope;
}

/// <summary>
/// The operation running in the current DI scope, set by the dispatcher before admission. How scoped services (the job
/// queue) know who's asking.
/// </summary>
public sealed class CurrentOperation
{
    public OperationContext? Context { get; internal set; }
}

/// <summary>What a unit of work may see and do. A null <see cref="Key"/> is platform scope, only ever for a declared platform operation.</summary>
public sealed record WorkScope(string? Key, bool ReadOnly)
{
    public bool Platform => Key is null;
}

public sealed record AccessRequest(Principal Principal, OperationDescriptor Operation, IOperation Input, WorkScope Scope);

/// <summary>
/// The app's authorization, asked once per operation before its unit of work opens. Required: without one, resolving the
/// dispatcher fails, at the first operation. Handlers never check permissions.
/// </summary>
public interface IAccessPolicy
{
    /// <returns>Null to allow; the refusal otherwise.</returns>
    ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct);
}

/// <summary>
/// An app's check that needs the operation's own transaction (a membership row, a lease), run after the unit of work opens
/// and before the handler. Registered as scoped; every one is asked, in registration order.
/// </summary>
public interface IAdmission
{
    /// <returns>Null to admit; the refusal otherwise.</returns>
    Task<Error?> AdmitAsync(IOperation operation, OperationContext context, CancellationToken ct);
}

/// <summary>
/// How an operation ended: <see cref="Error"/> is null when it committed (or a subscription opened). <see cref="Input"/> is as
/// the audit keeps it, secrets redacted; <see cref="Scope"/> is null when it never got one.
/// </summary>
public sealed record OperationOutcome(OperationDescriptor Operation, string Input, WorkScope? Scope, Principal Principal, Error? Error);

/// <summary>
/// Told how every dispatched operation ended, after its unit of work committed or rolled back, in a DI scope of its own: so
/// what it keeps of a refusal isn't rolled back with it. To keep something, it dispatches an operation of its own.
/// </summary>
public interface IOperationOutcome
{
    Task RecordAsync(OperationOutcome outcome, CancellationToken ct);
}

/// <summary>
/// Sends the operation's pending writes to the store inside its open transaction, so a query or a set-based statement after
/// them sees them. It never commits: the dispatcher commits once, and outside an operation this refuses. What an adapter
/// calls instead of saving.
/// </summary>
public interface IFlush
{
    Task FlushAsync(CancellationToken ct);
}

/// <summary>
/// Who a job runs as: given the <see cref="JobPrincipal"/> stored with it (whoever enqueued it), the app's own principal,
/// looked up as it is now. Without one, jobs run as the <see cref="JobPrincipal"/> itself.
/// </summary>
public interface IJobPrincipals
{
    ValueTask<Principal> RestoreAsync(JobPrincipal job, CancellationToken ct);
}

/// <summary>
/// The current unit of work's connection and transaction, for adapters that write through something other than its
/// DbContext (a job queue) and must commit or roll back with it. Null outside an operation.
/// </summary>
public interface IAmbientTransaction
{
    (System.Data.Common.DbConnection Connection, System.Data.Common.DbTransaction Transaction)? Current { get; }
}

public sealed record AuditEntry(DateTimeOffset At, string? Scope, string Subject, string Operation, string Input);

/// <summary>One operation's transaction. Opened scoped and read-only as its contract says; committed only by the dispatcher.</summary>
public interface IUnitOfWork : IAsyncDisposable
{
    WorkScope Scope { get; }

    /// <summary>Records the entry in this transaction: it commits or rolls back with the operation.</summary>
    Task AuditAsync(AuditEntry entry, CancellationToken ct);

    Task CommitAsync(CancellationToken ct);
}

/// <summary>Resolved from the operation's DI scope, so the unit of work shares that scope's instances with the handler.</summary>
public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(WorkScope scope, CancellationToken ct);

    /// <summary>
    /// What a failure from the store, beginning or inside a unit of work, means for the caller: an undeclared write, a
    /// conflict, a scope violation, contention or an unreachable store (<see cref="LeveeErrors"/>). Null for a bug.
    /// </summary>
    Error? Classify(Exception exception);
}

/// <summary>
/// On an <see cref="IAccessPolicy"/> or <see cref="IAdmission"/>: the codes it refuses with. They apply to every operation, so
/// the catalog lists them once (<see cref="OperationCatalog.Refusals"/>), and a literal code it doesn't declare fails the
/// build (LEVEE111).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RefusesAttribute(params string[] codes) : Attribute
{
    public IReadOnlyList<string> Codes { get; } = codes;
}
