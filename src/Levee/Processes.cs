using System.Diagnostics.CodeAnalysis;

namespace Levee;

/// <summary>
/// A process: long-running work an operation starts (an agent's turn that runs for minutes, a backfill between pauses), run
/// as a job, outside any transaction. It reaches the app only by sending operations, as the caller that started it and in
/// the scope it was started in, so each step commits on its own and nothing is held open while it waits. Its state is
/// what those operations commit: a run that's retried or snoozed starts again from the top and reads where it was.
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

    internal ProcessContext(Principal principal, string? scope, int attempt, Func<IOperation, CancellationToken, Task<Result<object?>>> send)
    {
        Principal = principal;
        Scope = scope;
        Attempt = attempt;
        _send = send;
    }

    /// <summary>The caller that started it, restored.</summary>
    public Principal Principal { get; }

    /// <summary>The scope it was started in; null for the platform's.</summary>
    public string? Scope { get; }

    /// <summary>Which run this is, from 1: a retry or a snooze runs it again.</summary>
    public int Attempt { get; }

    /// <summary>Sends <paramref name="operation"/> through the pipeline as the process's caller, in its scope: one step, committed on its own.</summary>
    public async Task<Result<TResult>> SendAsync<TResult>(IOperation<TResult> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = await _send(operation, ct).ConfigureAwait(false);
        return result.IsSuccess ? Result<TResult>.Success((TResult)result.Value!) : result.Error;
    }

    /// <summary>Ends this run and runs the process again after <paramref name="delay"/>, from the top.</summary>
    [DoesNotReturn]
    public void Snooze(TimeSpan delay) => throw new ProcessSnoozed(delay, Principal.Subject);
}

/// <summary>A snoozed run, for the job runner to schedule again.</summary>
internal sealed class ProcessSnoozed(TimeSpan delay, string subject) : Exception($"{subject}'s process snoozed for {delay}.")
{
    public TimeSpan Delay { get; } = delay;
}
