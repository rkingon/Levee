# MCP

`Levee.Mcp` projects the catalog as MCP tools, for the platform's own agents. Like `/ops`, it's an internal surface: a
public MCP server whose tool names outside clients rely on is a `Projection`, the app's own.

```csharp
services.AddMcpServer()
    .WithHttpTransport()                       // or stdio
    .WithLeveeOperations(o =>
    {
        o.Principal = request => Principals.Of(request.User);
        o.Include = d => d.Get<AgentTool>() is not null;
    });
```

- **One tool per operation a client may call:** not internal, answering once. Its name is the operation's with each dot
  an underscore (`notes.create` is `notes_create`); two operations that would share a name, or one over 64 characters,
  fail at startup.
- **From the contract:** the description is the operation's summary, the input schema is its input's (`[Secret]` members
  write-only), and the hints say whether it only reads, is safe to repeat, may overwrite or remove (anything that writes
  but a create refused on a repeat), and reaches outside the platform.
- **Through the dispatcher's client path,** as the principal `Principal` resolves: authorized, scoped, audited, and never
  an internal operation. A failure answers as a tool error holding the same problem body HTTP sends.

## Options

- **`BoundInputs` and `Bind`:** inputs the server supplies rather than the model (a turn's id). They're taken out of every
  schema; anything the model sends for them, in any casing, is dropped; `Bind` must supply each, or the call fails.
- **`Render`:** a result as content other than its JSON, such as an image the model should see.
- **`Send` and `Catalog`:** an MCP server with no dispatcher of its own, beside the client (stdio), sending each call to
  the app's `/ops` over the wire, with the Contracts project's `LeveeCatalog` as its catalog.
