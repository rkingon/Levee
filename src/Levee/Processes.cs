using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Levee;

/// <summary>
/// A process: long-running work an operation starts (an agent's turn that runs for minutes, a backfill between pauses), run
/// as a job, outside any transaction. It reaches the app only by sending operations, as the caller that started it and in
/// the scope it was started in, so each one commits on its own and nothing is held open while it waits. A run that's
/// retried or snoozed starts again from the top, and resumes where it was through its steps
/// (<see cref="ProcessContext.StepAsync{T}"/>): each one done is answered from its record, not done again.
/// Implemented in Application, its I/O through ports; started with <see cref="IJobs.StartAsync{TInput}"/>.
/// </summary>
/// <typeparam name="TInput">What it's started with, stored with the job: renaming it strands runs still queued.</typeparam>
public interface IProcess<in TInput>
    where TInput : notnull
{
    Task RunAsync(TInput input, ProcessContext context, CancellationToken ct);
}

/// <summary>Who a process runs as and where, and its one way into the app.</summary>
public sealed class ProcessContext
{
    private readonly Func<IOperation, CancellationToken, Task<Result<object?>>> _send;
    private readonly Func<string, Func<CancellationToken, Task<JsonElement>>, CancellationToken, Task<JsonElement>> _step;
    private readonly bool _sealed;

    // step: runs a step whose result is recorded under its name, or answers the one recorded (the job's own); sealedResults:
    // whether what it records is sealed at rest, as a sealed process's input is.
    internal ProcessContext(Principal principal, string? scope, int attempt, Func<IOperation, CancellationToken, Task<Result<object?>>> send,
        Func<string, Func<CancellationToken, Task<JsonElement>>, CancellationToken, Task<JsonElement>>? step = null, bool sealedResults = false)
    {
        Principal = principal;
        Scope = scope;
        Attempt = attempt;
        _send = send;
        _step = step ?? ((_, run, ct) => run(ct));
        _sealed = sealedResults;
    }

    /// <summary>The caller that started it, restored.</summary>
    public Principal Principal { get; }

    /// <summary>The scope it was started in; null for the platform's.</summary>
    public string? Scope { get; }

    /// <summary>Which attempt this is, from 1: a failed run is retried with the next. A snooze isn't an attempt, so a process that snoozes knows where it is from its own state, not from this.</summary>
    public int Attempt { get; }

    /// <summary>Sends <paramref name="operation"/> through the pipeline as the process's caller, in its scope: one step, committed on its own.</summary>
    public async Task<Result<TResult>> SendAsync<TResult>(IOperation<TResult> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = await _send(operation, ct).ConfigureAwait(false);
        return result.IsSuccess ? Result<TResult>.Success((TResult)result.Value!) : result.Error;
    }

    /// <summary>
    /// One step of the process, recorded under <paramref name="name"/> once it succeeds: a later run (after a failed attempt,
    /// a snooze, a rescue) gets the recorded result back instead of running it again, so a process written from the top
    /// resumes where it was. A failure records nothing, so the next run tries it again. At least once: a run that dies after
    /// the step acted but before its result was recorded runs it again, so a vendor call takes an idempotency key.
    /// </summary>
    /// <param name="name">What the step does, unique in the process: its result's identity, so renaming it runs it again.</param>
    /// <param name="run">The step: an operation sent (<see cref="SendAsync{TResult}"/>), or a port's call.</param>
    /// <param name="ct">Passed to <paramref name="run"/>.</param>
    public async Task<Result<T>> StepAsync<T>(string name, Func<CancellationToken, Task<Result<T>>> run, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(run);
        // Before it acts: a step whose result can't be recorded, or read back once it is, would act again on every run.
        if (OperationCatalog.Unreadable(typeof(T)) is { } unreadable)
        {
            throw new InvalidOperationException($"Step {name}'s result {typeof(T).Name} isn't one a step can record and read back: {unreadable}");
        }
        if (!_sealed && LeveeJson.FindSecret(typeof(T), LeveeJson.Options) is { } declared)
        {
            throw new InvalidOperationException(Unsealed(name, declared));
        }
        try
        {
            var recorded = await _step(name, async token =>
            {
                var result = await run(token).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    throw new StepFailed(result.Error);
                }
                if (!_sealed && result.Value is { } value && LeveeJson.FindSecretIn(value, typeof(T), LeveeJson.Options) is { } secret)
                {
                    throw new InvalidOperationException(Unsealed(name, secret));
                }
                var written = JsonSerializer.SerializeToElement(result.Value, LeveeJson.Options);
                // Read back before it's recorded: a value the language allows can still be one its type doesn't read (a choice's
                // subtype it doesn't name), and a record no run can read would stand for good.
                written.Deserialize<T>(LeveeJson.Options);
                return written;
            }, ct).ConfigureAwait(false);
            return Result<T>.Success(recorded.Deserialize<T>(LeveeJson.Options)!);
        }
        catch (StepFailed failed)
        {
            return failed.Error;
        }
    }

    private static string Unsealed(string step, string secret) =>
        $"Step {step}'s result {secret} is a secret, and a step's result is stored. Return a reference to it, "
        + "or seal them: AddLeveeJobs(sealSecrets: true) with Oxbow's AddArgsEncryption.";

    /// <summary>Ends this run and runs the process again after <paramref name="delay"/>, from the top.</summary>
    [DoesNotReturn]
    public void Snooze(TimeSpan delay) => throw new ProcessSnoozed(delay, Principal.Subject);
}

/// <summary>A step that failed: through the job's step unrecorded, then answered as the failure it was.</summary>
internal sealed class StepFailed(Error error) : Exception($"{error.Code}: {error.Message}")
{
    public Error Error { get; } = error;
}

/// <summary>
/// The process's attempt is no longer the one running (it was rescued and runs again elsewhere): the operation it was
/// sending rolled back, and the run ends here.
/// </summary>
internal sealed class AttemptSuperseded(Exception inner) : Exception("The process's attempt was superseded.", inner);

/// <summary>A snoozed run, for the job runner to schedule again.</summary>
internal sealed class ProcessSnoozed(TimeSpan delay, string subject) : Exception($"{subject}'s process snoozed for {delay}.")
{
    public TimeSpan Delay { get; } = delay;
}
