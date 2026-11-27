using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Levee.EntityFrameworkCore;

/// <summary>Signals over Postgres NOTIFY: sent with the operation's transaction, so Postgres delivers them only when it commits.</summary>
internal sealed partial class PostgresSignals<TContext>(TContext db, SignalHub<TContext> hub, CurrentOperation current, CurrentSubscription subscription)
    : IChangeSignals
    where TContext : DbContext
{
    internal const string Channel = "levee_signals";

    public Task RaiseAsync(string kind, string? key, CancellationToken ct) => SendAsync(kind, key, scope: null, ct);

    public Task RaiseAsync(string kind, string? key, string scope, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return SendAsync(kind, key, scope, ct);
    }

    private async Task SendAsync(string kind, string? key, string? scope, CancellationToken ct)
    {
        Check(kind, key);
        var context = current.Context
            ?? throw new InvalidOperationException($"{kind} was raised outside an operation: a signal is sent with the operation's transaction.");
        if (context.Scope.ReadOnly)
        {
            throw new InvalidOperationException($"{kind} was raised by {context.Descriptor.Name}, which declares only reads: raising a signal is an effect.");
        }
        if (scope is not null && !context.Scope.Platform)
        {
            throw new InvalidOperationException($"{context.Descriptor.Name} raised {kind} into another scope: only a platform operation names one.");
        }
        // The scope is the transaction's own setting, so a signal can't claim a scope its operation wasn't authorized in.
        // Typed: a null key or scope is a parameter Postgres can't infer a type for inside json_build_object's variadic any.
        await db.Database.ExecuteSqlAsync(
            $"select pg_notify({Channel}, json_build_object('kind', {kind}::text, 'key', {key}::text, 'scope', coalesce({scope}::text, nullif(current_setting('levee.scope', true), '')))::text)",
            ct).ConfigureAwait(false);
    }

    public Task<IAsyncEnumerable<ChangeSignal>> WatchAsync(string kind, CancellationToken ct)
    {
        Check(kind, key: null);
        // The scope is the one the dispatcher authorized the subscription in, never one the caller hands over.
        var context = subscription.Context
            ?? throw new InvalidOperationException($"{kind} is watched from a subscription's handler, which the dispatcher scopes: there's none streaming here.");
        return hub.WatchAsync(kind, context.Scope, ct);
    }

    private static void Check(string kind, string? key)
    {
        if (!Kind().IsMatch(kind))
        {
            throw new ArgumentException($"'{kind}' isn't a signal kind: lowercase segments joined by dots, as notes.changed.", nameof(kind));
        }
        // NOTIFY's payload is under 8000 bytes; a key names a row, it doesn't carry one.
        if (key?.Length > 200)
        {
            throw new ArgumentException($"A signal's key is at most 200 characters: it names what changed, not what it is.", nameof(key));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$")]
    private static partial Regex Kind();
}

/// <summary>
/// One LISTEN connection per process, opened with the first watcher and reopened after a drop, fanning each signal out to
/// the watchers of its kind in its scope. A watcher that falls behind loses its oldest signals, not the newest: each is a
/// hint to read again.
/// </summary>
internal sealed partial class SignalHub<TContext>(IServiceScopeFactory scopes, ILogger<SignalHub<TContext>> log) : IAsyncDisposable
    where TContext : DbContext
{
    private readonly ConcurrentDictionary<Channel<ChangeSignal>, (string Kind, WorkScope Scope)> _watchers = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Task? _listening;

    /// <summary>Registered at the call, returned once Levee listens: a caller that watches, then reads, misses nothing between.</summary>
    public async Task<IAsyncEnumerable<ChangeSignal>> WatchAsync(string kind, WorkScope scope, CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<ChangeSignal>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _watchers[channel] = (kind, scope);
        // A watch never read is still let go when its caller stops.
        var stopped = ct.Register(() => _watchers.TryRemove(channel, out _));
        try
        {
            await Start().WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _watchers.TryRemove(channel, out _);
            await stopped.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return ReadAsync(channel, stopped, ct);
    }

    private async IAsyncEnumerable<ChangeSignal> ReadAsync(Channel<ChangeSignal> channel, CancellationTokenRegistration stopped, [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var signal in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return signal;
            }
        }
        finally
        {
            _watchers.TryRemove(channel, out _);
            await stopped.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Resolved while LISTEN is in effect, and replaced the moment the connection drops: a watch that starts then waits for
    // the next one, rather than reading before anything listens.
    private TaskCompletionSource? _ready;

    /// <summary>Starts listening with the first watch; resolves once LISTEN is in effect.</summary>
    private Task Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
            if (_ready is null)
            {
                _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _listening = Task.Run(() => ListenAsync(_stopping.Token));
            }
            return _ready.Task;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var dropped = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // The app's own context's connection, opened by hand: LISTEN runs no command through EF, so the guard isn't asked.
                await using var scope = scopes.CreateAsyncScope();
                var connection = (NpgsqlConnection)scope.ServiceProvider.GetRequiredService<TContext>().Database.GetDbConnection();
                await connection.OpenAsync(ct).ConfigureAwait(false);
                connection.Notification += (_, e) => Deliver(e.Payload);
                await using (var listen = new NpgsqlCommand($"listen {PostgresSignals<TContext>.Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                // Back after a drop: whatever committed while nothing listened is unknown, so every watch reads again.
                if (dropped)
                {
                    Refresh();
                }
                lock (_gate)
                {
                    _ready!.TrySetResult();
                }
                while (!ct.IsCancellationRequested)
                {
                    await connection.WaitAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or System.IO.IOException)
            {
                lock (_gate)
                {
                    if (_ready!.Task.IsCompleted)
                    {
                        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
                dropped = true;
                LogDropped(log, e);
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
    }

    private void Refresh()
    {
        foreach (var (channel, (kind, scope)) in _watchers)
        {
            channel.Writer.TryWrite(new ChangeSignal(kind, null, scope.Key));
        }
    }

    private void Deliver(string payload)
    {
        ChangeSignal? signal;
        try
        {
            signal = JsonSerializer.Deserialize<ChangeSignal>(payload, Wire);
        }
        catch (JsonException e)
        {
            LogUnreadable(log, e, payload);
            return;
        }
        if (signal is null)
        {
            return;
        }
        foreach (var (channel, (kind, scope)) in _watchers)
        {
            if (kind == signal.Kind && (scope.Platform || scope.Key == signal.Scope))
            {
                channel.Writer.TryWrite(signal);
            }
        }
    }

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        // Nothing will listen again: a watch still waiting for LISTEN, or reading, ends with the host rather than waiting on it.
        lock (_gate)
        {
            _ready?.TrySetException(new ObjectDisposedException(GetType().Name));
        }
        foreach (var channel in _watchers.Keys)
        {
            channel.Writer.TryComplete();
        }
        if (_listening is { } listening)
        {
            await listening.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        _stopping.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Levee's signal connection dropped; reconnecting")]
    private static partial void LogDropped(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A signal's payload isn't Levee's: {Payload}")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string payload);
}
