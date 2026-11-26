using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Oxbow;

namespace Levee.Oxbow;

public static class LeveeOxbowServiceCollectionExtensions
{
    /// <summary>
    /// Oxbow, enlisted in Levee's unit of work: every insert through the scoped <see cref="IOxbowClient"/> joins the current
    /// operation's transaction, so a job commits or rolls back with the data it belongs to, and an insert outside an
    /// operation is refused. Add the app's own workers to the builder; <see cref="AddLeveeJobs"/> adds operations as jobs.
    /// Oxbow uses the app's registered <c>NpgsqlDataSource</c>.
    /// </summary>
    public static IOxbowBuilder AddLeveeOxbow(this IServiceCollection services, Action<OxbowOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddOxbow(o =>
            {
                o.RequireAmbientTransaction = true;
                configure?.Invoke(o);
                // A process that works no queue only enqueues: Levee's own workers mustn't make it refuse the kinds others work.
                if (o.Queues.Count == 0)
                {
                    o.SkipUnknownKindCheck = true;
                }
            })
            .AddTransactionAccessor<LeveeTransactionAccessor>();
    }

    /// <summary>
    /// <see cref="IJobs"/>: an operation enqueued as a job, authorized as its caller, run later through the pipeline for them.
    /// With <paramref name="sealSecrets"/>, an operation with <see cref="SecretAttribute"/> inputs may be enqueued too, its
    /// whole input encrypted at rest by Oxbow's args encryption (<c>AddArgsEncryption</c>, Oxbow.DataProtection), which the app
    /// registers with a key ring every node shares. Without it, such an operation is refused.
    /// </summary>
    public static IOxbowBuilder AddLeveeJobs(this IOxbowBuilder oxbow, bool sealSecrets = false)
    {
        ArgumentNullException.ThrowIfNull(oxbow);
        oxbow.AddWorker<OperationJob, OperationJobWorker>();
        if (sealSecrets)
        {
            oxbow.AddWorker<SealedOperationJob, SealedOperationJobWorker>();
        }
        oxbow.Services.AddSingleton(new LeveeJobsOptions(sealSecrets));
        oxbow.Services.AddScoped<IJobs, OxbowJobs>();
        return oxbow;
    }
}

internal sealed class LeveeTransactionAccessor(IAmbientTransaction ambient) : IOxbowTransactionAccessor
{
    public (NpgsqlConnection Connection, NpgsqlTransaction Transaction)? Current =>
        ambient.Current is ({ } connection, { } transaction)
            ? (connection as NpgsqlConnection ?? throw new InvalidOperationException("Oxbow needs the unit of work on Npgsql."),
                transaction as NpgsqlTransaction ?? throw new InvalidOperationException("Oxbow needs the unit of work on Npgsql."))
            : null;
}

internal sealed record LeveeJobsOptions(bool SealSecrets);

/// <summary>Every Levee job is one Oxbow kind: the operation's name, its input as the wire carries it, and who it runs for.</summary>
[JobKind("levee_operation")]
internal sealed record OperationJob(string Operation, JsonElement Input, string Subject, bool System, string? Scope = null) : IJobArgs;

/// <summary>The same, for an operation with secret inputs: encrypted at rest, decrypted only to run.</summary>
[JobKind("levee_operation_sealed"), EncryptArgs]
internal sealed record SealedOperationJob(string Operation, JsonElement Input, string Subject, bool System, string? Scope = null) : IJobArgs;

internal sealed class OxbowJobs(IOxbowClient client, Dispatcher dispatcher, CurrentOperation current, LeveeOptions levee, LeveeJobsOptions jobs) : IJobs
{
    /// <summary>A finished job doesn't block an equal new one: only one still to run does.</summary>
    private static readonly IReadOnlyList<JobState> LiveStates =
        [JobState.Available, JobState.Pending, JobState.Running, JobState.Scheduled, JobState.Retryable];

    public async Task<Result<Enqueued>> EnqueueAsync(IOperation operation, JobOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var caller = current.Context?.Principal
            ?? throw new InvalidOperationException("A job is enqueued only from inside an operation, in its transaction.");
        var descriptor = dispatcher.Catalog.Find(operation.GetType())
            ?? throw new ArgumentException($"{operation.GetType().Name} isn't in the catalog.", nameof(operation));
        if (descriptor.Idempotency == Idempotency.None)
        {
            throw new InvalidOperationException(
                $"{descriptor.Name} can't run as a job: jobs run at least once, and it declares that repeating it isn't safe (Idempotency.None).");
        }
        var secret = LeveeJson.FindSecret(descriptor.InputType, levee.Json);
        if (secret is not null && !jobs.SealSecrets)
        {
            throw new InvalidOperationException(
                $"{descriptor.Name} can't run as a job: its input {secret} is a secret, and job inputs are stored. Pass a reference to it, "
                + "or seal them: AddLeveeJobs(sealSecrets: true) with Oxbow's AddArgsEncryption.");
        }
        if (await dispatcher.AuthorizeAsync(operation, caller, ct) is { } denied)
        {
            return denied;
        }

        var (subject, system) = caller switch
        {
            JobPrincipal job => (job.Subject, job.System),
            SystemPrincipal platform => (platform.Subject, true),
            _ => (caller.Subject, false),
        };
        // A caller-scoped job runs where its caller could: the scope its credentials carried at enqueue.
        var scope = caller is IScopedPrincipal scoped && !string.IsNullOrWhiteSpace(scoped.ScopeKey) ? scoped.ScopeKey : null;
        var input = JsonSerializer.SerializeToElement(operation, operation.GetType(), levee.Json);
        var insert = new InsertOptions
        {
            ScheduledAt = options?.RunAt,
            Queue = options?.Queue,
            MaxAttempts = options?.MaxAttempts,
            Unique = options is { Unique: true } ? new UniqueOptions { ByArgs = true, ByState = LiveStates } : null,
        };
        var result = secret is null
            ? await client.InsertAsync(new OperationJob(descriptor.Name, input, subject, system, scope), insert, ct)
            : await client.InsertAsync(new SealedOperationJob(descriptor.Name, input, subject, system, scope), insert, ct);
        return new Enqueued(result.Job.Id, result.UniqueSkippedAsDuplicate);
    }
}

/// <summary>
/// Runs the job's operation through the dispatcher, in a unit of work of its own, as a <see cref="JobPrincipal"/> for whoever
/// enqueued it. A refusal is final (the job is cancelled with it); a transient failure (<see cref="Error.Transient"/>: the
/// store or a vendor unavailable, or a bug) is retried by Oxbow, with its backoff or after the error's own
/// <see cref="Error.RetryAfter"/>, until its attempts run out.
/// </summary>
internal sealed class OperationJobWorker(Dispatcher dispatcher, IEnumerable<IJobPrincipals> principals) : IWorker<OperationJob>
{
    public Task WorkAsync(Job<OperationJob> job, CancellationToken cancellationToken) =>
        Run.OperationAsync(dispatcher, principals, job.Args.Operation, job.Args.Input, job.Args.Subject, job.Args.System, job.Args.Scope, cancellationToken);
}

internal sealed class SealedOperationJobWorker(Dispatcher dispatcher, IEnumerable<IJobPrincipals> principals) : IWorker<SealedOperationJob>
{
    public Task WorkAsync(Job<SealedOperationJob> job, CancellationToken cancellationToken) =>
        Run.OperationAsync(dispatcher, principals, job.Args.Operation, job.Args.Input, job.Args.Subject, job.Args.System, job.Args.Scope, cancellationToken);
}

internal static class Run
{
    public static async Task OperationAsync(Dispatcher dispatcher, IEnumerable<IJobPrincipals> principals, string operation, JsonElement input,
        string subject, bool system, string? scope, CancellationToken ct)
    {
        var job = new JobPrincipal(subject, system, scope);
        var principal = principals.FirstOrDefault() is { } restore ? await restore.RestoreAsync(job, ct) : job;
        var result = await dispatcher.SendJsonAsync(operation, input, principal, ct);
        if (result.IsSuccess)
        {
            return;
        }
        var refusal = $"{result.Error.Code}: {result.Error.Message}";
        throw result.Error switch
        {
            // The cause knew when to come back (a vendor's Retry-After): that, not Oxbow's backoff.
            { Transient: true, RetryAfter: { } after } => new JobRetryException(after, refusal),
            { Transient: true } => new OperationUnavailableException(refusal),
            _ => new JobCancelException(refusal),
        };
    }
}

public sealed class OperationUnavailableException(string message) : Exception(message);
