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
  no longer holds, the stream ends.
- **A handler that throws** ends the stream with `OperationFailedException`, logged like any failed operation.
- **A reader that stops** stops the handler: its cancellation reaches the handler, and its `finally` runs.

## Over HTTP

`GET /ops/{name}/events?input={json}` streams server-sent events: each item a `message` event, a `keepalive` event as it
opens and every `SubscriptionKeepAlive` (15 seconds) while idle, and one last `error` event, holding the
`application/problem+json` body, when it fails or its caller no longer holds.
