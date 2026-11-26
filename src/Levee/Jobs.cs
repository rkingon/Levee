namespace Levee;

/// <summary>
/// Runs an operation later, as a job. Only from inside an operation: the enqueue is authorized as the current caller, the
/// job is inserted in the current transaction (it commits or rolls back with it), and it runs through the pipeline as a
/// <see cref="JobPrincipal"/> for that caller. Jobs run at least once, so an operation whose repeat isn't safe
/// (<see cref="Idempotency.None"/>), or that carries a <see cref="SecretAttribute"/> input, is refused.
/// </summary>
public interface IJobs
{
    /// <returns>The caller's refusal, if the access policy wouldn't let them run it now.</returns>
    Task<Result<Enqueued>> EnqueueAsync(IOperation operation, JobOptions? options, CancellationToken ct);
}

/// <summary>When and where a job runs. <c>Unique</c> skips the insert while an equal job (same operation and input) is still to run.</summary>
public sealed record JobOptions(DateTimeOffset? RunAt = null, string? Queue = null, int? MaxAttempts = null, bool Unique = false);

/// <summary>The job inserted, or with <c>Duplicate</c> the equal one already queued.</summary>
public readonly record struct Enqueued(long JobId, bool Duplicate);
