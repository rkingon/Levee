namespace Levee;

/// <summary>
/// Runs an operation later, as a job. Only from inside an operation: the enqueue is authorized as the current caller, the
/// job is inserted in the current transaction (it commits or rolls back with it), and it runs through the pipeline as a
/// <see cref="JobPrincipal"/> for that caller. Jobs run at least once, so an operation whose repeat isn't safe
/// (<see cref="Idempotency.None"/>) is refused, and so is one carrying a <see cref="SecretAttribute"/> input, at any depth,
/// unless jobs seal their inputs (<c>AddLeveeJobs(sealSecrets: true)</c>).
/// </summary>
public interface IJobs
{
    /// <returns>The caller's refusal, if the access policy wouldn't let them run it now.</returns>
    Task<Result<Enqueued>> EnqueueAsync(IOperation operation, JobOptions? options, CancellationToken ct);

    /// <summary>
    /// Starts the <see cref="IProcess{TInput}"/> for <paramref name="input"/>, inserted in the current transaction like a
    /// job: it runs only if the operation that starts it commits, as that operation's caller, in its scope. Its input is
    /// stored, so it carries no <see cref="SecretAttribute"/> unless jobs seal their inputs.
    /// </summary>
    Task<Enqueued> StartAsync<TInput>(TInput input, JobOptions? options, CancellationToken ct)
        where TInput : notnull;
}

/// <summary>
/// When and where a job runs. <c>Unique</c> skips the insert while an equal job is still to run: the same operation and input,
/// enqueued by the same caller in the same scope, since who it runs as is part of what's stored.
/// </summary>
public sealed record JobOptions(DateTimeOffset? RunAt = null, string? Queue = null, int? MaxAttempts = null, bool Unique = false);

/// <summary>The job inserted, or with <c>Duplicate</c> the equal one already queued.</summary>
public readonly record struct Enqueued(long JobId, bool Duplicate);
