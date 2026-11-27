# Subscriptions

A subscription answers with a stream, not once. It's declared like an operation, and goes through the same pipeline as it
opens:

```csharp
[Operation("notes.watch", "member")]
public sealed record WatchNotes(string Tenant) : ISubscription<NoteView>, IScopedOperation { ... }

internal sealed class WatchNotesHandler : ISubscriptionHandler<WatchNotes, NoteView> { ... }
```

- **As it opens:** scoped, authorized by the access policy, and admitted by every `IAdmission` in a read-only unit of work
  of its own. It holds no transaction while it streams.
- **While it streams:** the projection checks again every `LeveeOptions.SubscriptionRecheck` (30 seconds): the caller's
  credentials (a revoked key, a signed-out session), the access policy, and the admissions (a lost membership). When one
  no longer holds, the stream ends. So does a recheck that finds another subject, or the same one scoped elsewhere (a
  session switched to another tenant): the stream runs as the caller who opened it, in that caller's scope.
- **A handler that throws**, or an item that can't be serialized, ends the stream with `OperationFailedException`, logged
  like any failed operation.
- **A reader that stops** stops the handler: its cancellation reaches the handler, and its `finally` runs.

## Following changes

A subscription that pushes what changed follows `IChangeSignals`: the operation that changes something raises a signal with
its transaction, and Postgres delivers it only when that commits. A signal is a kind and a key, never the data, so the
handler reads again through an operation, in the subscription's own scope, as its own caller.

```csharp
// The write raises, inside its operation
await signals.RaiseAsync("notes.changed", note.Id.ToString(), ct);

// The subscription watches first, reads, then follows: nothing that commits between is missed
internal sealed class WatchNotesHandler(IChangeSignals signals, Dispatcher dispatcher) : ISubscriptionHandler<WatchNotes, NoteView>
{
    public async IAsyncEnumerable<NoteView> SubscribeAsync(WatchNotes watch, SubscriptionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var changes = await signals.WatchAsync("notes.changed", ct);   // returns once Levee listens
        foreach (var note in (await dispatcher.SendAsync(new ListNotes(watch.Tenant), context.Principal, ct)).Value)
        {
            yield return note;
        }
        await foreach (var change in changes)
        {
            // A null key is "read again": the listening connection dropped and came back, and what changed meanwhile isn't known.
            if (change.Key is null) { /* send ListNotes again */ continue; }
            if ((await dispatcher.SendAsync(new GetNote(watch.Tenant, Guid.Parse(change.Key)), context.Principal, ct)) is { IsSuccess: true } read)
            {
                yield return read.Value;
            }
        }
    }
}
```

A watch is scoped by the subscription the dispatcher authorized, in whose handler it's called; nothing the handler
passes widens it. The listening connection is Levee's, one per process: an app opens none of its own (LEVEE018). A watcher that falls
behind loses its oldest signals, not its newest; each is only a reason to read again. A platform operation that changes
one scope's rows raises into that scope by name; a signal raised by a read-only operation, or outside one, is refused.

## Over HTTP

`GET /ops/{name}/events?input={json}` streams server-sent events: each item a `message` event, a `keepalive` event as it
opens and every `SubscriptionKeepAlive` (15 seconds) while idle, and one last `error` event, holding the
`application/problem+json` body, when it fails or its caller no longer holds.
