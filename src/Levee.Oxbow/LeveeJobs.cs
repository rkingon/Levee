using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Oxbow;

namespace Levee.Oxbow;

public static partial class LeveeOxbowServiceCollectionExtensions
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
        services.AddSingleton(sp => OxbowTables(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OxbowOptions>>().Value));
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

    /// <summary>The tables Oxbow's own migrations create, as configured: what it installs, read from its DDL rather than guessed by prefix.</summary>
    private static OwnedRelations OxbowTables(OxbowOptions options) => new(OxbowSchema.GetMigrations(0, OxbowSchema.LatestVersion, options.Schema, options.TablePrefix)
        .SelectMany(m => CreatedTable().Matches(m.Sql).Select(t => t.Groups[1].Value.Contains('.', StringComparison.Ordinal)
            ? t.Groups[1].Value : $"{options.Schema}.{t.Groups[1].Value}"))
        .Distinct(StringComparer.Ordinal)
        .ToArray());

    [System.Text.RegularExpressions.GeneratedRegex(@"create\s+(?:unlogged\s+)?table\s+(?:if\s+not\s+exists\s+)?([a-z0-9_.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CreatedTable();

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
        oxbow.AddWorker<ProcessJob, ProcessJobWorker>();
        var services = oxbow.Services;
        // Read at first use, once the composition has registered every process.
        oxbow.Services.AddSingleton(_ => new ProcessInputs(services));
        if (sealSecrets)
        {
            oxbow.AddWorker<SealedOperationJob, SealedOperationJobWorker>();
            oxbow.AddWorker<SealedProcessJob, SealedProcessJobWorker>();
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

/// <summary>A process: its input's type by name and assembly, its input, and who it runs for, where.</summary>
[JobKind("levee_process")]
internal sealed record ProcessJob(string Process, JsonElement Input, string Subject, bool System, string? Scope = null, bool Unscoped = false) : IJobArgs;

[JobKind("levee_process_sealed"), EncryptArgs]
internal sealed record SealedProcessJob(string Process, JsonElement Input, string Subject, bool System, string? Scope = null, bool Unscoped = false)
    : IJobArgs;

internal sealed class OxbowJobs(IOxbowClient client, Dispatcher dispatcher, CurrentOperation current, LeveeOptions levee, LeveeJobsOptions jobs,
    IServiceProvider services) : IJobs
{
    public async Task<Enqueued> StartAsync<TInput>(TInput input, JobOptions? options, CancellationToken ct)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        var context = current.Context
            ?? throw new InvalidOperationException("A process is started only from inside an operation, in its transaction.");
        if (services.GetService(typeof(IServiceProviderIsService)) is IServiceProviderIsService known && !known.IsService(typeof(IProcess<TInput>)))
        {
            throw new InvalidOperationException($"No IProcess<{typeof(TInput).Name}> is registered: implement it in Application, where the generated registration finds it.");
        }
        if (context.Scope.ReadOnly)
        {
            throw new InvalidOperationException($"{context.Descriptor.Name} declares only reads, and starting a process is an effect.");
        }
        var secret = LeveeJson.FindSecretIn(input, typeof(TInput), levee.Json);
        if (secret is not null && !jobs.SealSecrets)
        {
            throw new InvalidOperationException(
                $"{typeof(TInput).Name} can't start a process: its input {secret} is a secret, and a process's input is stored. Pass a reference to it, "
                + "or seal them: AddLeveeJobs(sealSecrets: true) with Oxbow's AddArgsEncryption.");
        }
        var (subject, system) = context.Principal switch
        {
            JobPrincipal job => (job.Subject, job.System),
            SystemPrincipal platform => (platform.Subject, true),
            _ => (context.Principal.Subject, false),
        };
        // The scope the starting operation was authorized in: every step runs there, as a caller-scoped job does.
        var scope = context.Scope.Key;
        var unscoped = context.Scope.Unscoped;
        var name = ProcessInputs.NameOf(typeof(TInput));
        var stored = JsonSerializer.SerializeToElement(input, levee.Json);
        var insert = new InsertOptions
        {
            ScheduledAt = options?.RunAt,
            Queue = options?.Queue,
            MaxAttempts = options?.MaxAttempts,
            Unique = options is { Unique: true } ? new UniqueOptions { ByArgs = true, ByState = LiveStates } : null,
        };
        var result = secret is null
            ? await client.InsertAsync(new ProcessJob(name, stored, subject, system, scope, unscoped), insert, ct)
            : await client.InsertAsync(new SealedProcessJob(name, stored, subject, system, scope, unscoped), insert, ct);
        return new Enqueued(result.Job.Id, result.UniqueSkippedAsDuplicate);
    }

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
        if (descriptor.Streams)
        {
            throw new InvalidOperationException(
                $"{descriptor.Name} streams, and a job runs an operation that answers once. Enqueue an operation that does the work instead.");
        }
        if (descriptor.Idempotency == Idempotency.None)
        {
            throw new InvalidOperationException(
                $"{descriptor.Name} can't run as a job: jobs run at least once, and it declares that repeating it isn't safe (Idempotency.None).");
        }
        // By its type and by what its members actually are: an implementation behind an interface may carry the mark.
        var secret = LeveeJson.FindSecretIn(operation, descriptor.InputType, levee.Json);
        if (secret is not null && !jobs.SealSecrets)
        {
            throw new InvalidOperationException(
                $"{descriptor.Name} can't run as a job: its input {secret} is a secret, and job inputs are stored. Pass a reference to it, "
                + "or seal them: AddLeveeJobs(sealSecrets: true) with Oxbow's AddArgsEncryption.");
        }
        // The scope it's authorized in is the one stored: the caller's credentials read once, not again after.
        var authorized = await dispatcher.AuthorizeScopeAsync(operation, caller, ct);
        if (!authorized.IsSuccess)
        {
            return authorized.Error;
        }

        var (subject, system) = caller switch
        {
            JobPrincipal job => (job.Subject, job.System),
            SystemPrincipal platform => (platform.Subject, true),
            _ => (caller.Subject, false),
        };
        // A caller-scoped job runs where its caller could: the scope its credentials carried at enqueue.
        var scope = descriptor.CallerScoped ? authorized.Value.Key
            : caller is IScopedPrincipal scoped && !string.IsNullOrWhiteSpace(scoped.ScopeKey) ? scoped.ScopeKey : null;
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

/// <summary>
/// Runs a process as the caller that started it, in its scope: each step it sends is an operation of its own. A snooze runs
/// it again later; an exception is a failed run, retried by Oxbow until its attempts run out.
/// </summary>
internal sealed class ProcessJobWorker(IServiceScopeFactory scopes, Dispatcher dispatcher, IEnumerable<IJobPrincipals> principals, LeveeOptions levee,
    ProcessInputs inputs) : IWorker<ProcessJob>
{
    public Task WorkAsync(Job<ProcessJob> job, CancellationToken cancellationToken) =>
        Run.ProcessAsync(scopes, dispatcher, principals, levee, inputs, job.Args.Process, job.Args.Input, job.Args.Subject, job.Args.System, job.Args.Scope,
            job.Args.Unscoped, job.Attempt, cancellationToken);
}

internal sealed class SealedProcessJobWorker(IServiceScopeFactory scopes, Dispatcher dispatcher, IEnumerable<IJobPrincipals> principals, LeveeOptions levee,
    ProcessInputs inputs) : IWorker<SealedProcessJob>
{
    public Task WorkAsync(Job<SealedProcessJob> job, CancellationToken cancellationToken) =>
        Run.ProcessAsync(scopes, dispatcher, principals, levee, inputs, job.Args.Process, job.Args.Input, job.Args.Subject, job.Args.System, job.Args.Scope,
            job.Args.Unscoped, job.Attempt, cancellationToken);
}

/// <summary>
/// The inputs of every registered process, by the name a job stores: a stored name resolves to one of these or to nothing,
/// so no type is ever loaded from what the queue holds.
/// </summary>
internal sealed class ProcessInputs(IServiceCollection services)
{
    private readonly Lazy<Dictionary<string, Func<IServiceProvider, object, ProcessContext, CancellationToken, Task>>> _runs = new(() =>
        services
            .Select(d => d.ServiceType)
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IProcess<>))
            .Select(t => t.GenericTypeArguments[0])
            .Distinct()
            .ToDictionary(NameOf, input => typeof(Run).GetMethod(nameof(Run.StepAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(input)
                .CreateDelegate<Func<IServiceProvider, object, ProcessContext, CancellationToken, Task>>(), StringComparer.Ordinal));

    public static string NameOf(Type input) => $"{input.FullName}, {input.Assembly.GetName().Name}";

    public Type? Find(string name) => _runs.Value.ContainsKey(name) ? Type.GetType(name, throwOnError: false) : null;

    public Func<IServiceProvider, object, ProcessContext, CancellationToken, Task> RunOf(string name) => _runs.Value[name];
}

internal static class Run
{
    public static async Task ProcessAsync(IServiceScopeFactory scopes, Dispatcher dispatcher, IEnumerable<IJobPrincipals> principals, LeveeOptions levee,
        ProcessInputs inputs, string process, JsonElement input, string subject, bool system, string? scope, bool unscoped, int attempt, CancellationToken ct)
    {
        var type = inputs.Find(process) ?? throw new JobCancelException($"No process takes {process}: renamed or removed since it was started.");
        var job = new JobPrincipal(subject, system, scope);
        var principal = principals.FirstOrDefault() is { } restore ? await restore.RestoreAsync(job, ct) : job;
        if (principal.Subject != subject)
        {
            throw new JobCancelException($"Restored as {principal.Subject}, not {subject}, who started it.");
        }
        // Held to where it was started, the platform's work aside, whichever way the principal reaches the dispatcher.
        if (scope is not null || unscoped)
        {
            Dispatcher.Confine(principal, new WorkScope(scope, ReadOnly: false) { Unscoped = unscoped });
        }
        var context = new ProcessContext(principal, scope, attempt, (operation, token) => dispatcher.SendAsStepAsync(operation, principal, token));
        var value = input.Deserialize(type, levee.Json)
            ?? throw new JobCancelException($"{type.Name}'s stored input is empty.");
        await using var services = scopes.CreateAsyncScope();
        try
        {
            await inputs.RunOf(process)(services.ServiceProvider, value, context, ct);
        }
        catch (ProcessSnoozed snoozed)
        {
            throw new JobSnoozeException(snoozed.Delay);
        }
    }

    // Awaited inside, so a process that snoozes or throws before its first await fails its task, not its caller.
    internal static async Task StepAsync<TInput>(IServiceProvider services, object input, ProcessContext context, CancellationToken ct)
        where TInput : notnull =>
        await services.GetRequiredService<IProcess<TInput>>().RunAsync((TInput)input, context, ct);

    public static async Task OperationAsync(Dispatcher dispatcher, IEnumerable<IJobPrincipals> principals, string operation, JsonElement input,
        string subject, bool system, string? scope, CancellationToken ct)
    {
        var job = new JobPrincipal(subject, system, scope);
        var principal = principals.FirstOrDefault() is { } restore ? await restore.RestoreAsync(job, ct) : job;
        // It runs as whoever enqueued it, where they were: a lookup that now finds them elsewhere (a session switched tenant)
        // doesn't move the queued work there.
        if (principal.Subject != subject)
        {
            throw new JobCancelException($"Restored as {principal.Subject}, not {subject}, who enqueued it.");
        }
        // The dispatcher compares the scope it reads, once, with the one stored, and refuses a caller-scoped job moved elsewhere.
        var result = await dispatcher.SendJsonAsync(operation, input, principal, scope, ct);
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

internal sealed class OperationUnavailableException(string message) : Exception(message);
