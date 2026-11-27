namespace Levee;

/// <summary>That something of <see cref="Kind"/> changed, in <see cref="Scope"/>: a hint to read again, never the data.</summary>
public sealed record ChangeSignal(string Kind, string? Key, string? Scope);

/// <summary>
/// Change notifications, the store's own: raised inside an operation and delivered when it commits, never if it rolls
/// back; watched by a subscription in the scope it was opened in. A subscription that pushes what changed reads it again
/// through the signal, so a signal carries a kind and a key, never a row. The listening connection is Levee's: an app
/// opens none of its own (LEVEE018).
/// </summary>
public interface IChangeSignals
{
    /// <summary>Raises <paramref name="kind"/> in the operation's own scope, delivered when it commits.</summary>
    Task RaiseAsync(string kind, string? key, CancellationToken ct);

    /// <summary>As <see cref="RaiseAsync(string, string?, CancellationToken)"/>, into <paramref name="scope"/>: a platform operation's, changing one scope's rows.</summary>
    Task RaiseAsync(string kind, string? key, string scope, CancellationToken ct);

    /// <summary>
    /// The signals of <paramref name="kind"/> in the subscription's scope (every scope's, for a platform subscription), as
    /// they commit. Watching starts at the call: watch, then read what's there, then follow the signals, and nothing that
    /// commits between is missed.
    /// </summary>
    IAsyncEnumerable<ChangeSignal> WatchAsync(string kind, SubscriptionContext context, CancellationToken ct);
}
