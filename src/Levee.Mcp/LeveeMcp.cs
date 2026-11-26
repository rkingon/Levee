using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Levee.Mcp;

/// <summary>How the catalog is projected as MCP tools.</summary>
public sealed class LeveeMcpOptions
{
    /// <summary>Who a call runs as, in-process (the request's user, a turn's agent), or null for no one. Required unless <see cref="Send"/> is set.</summary>
    public Func<RequestContext<CallToolRequestParams>, ValueTask<Principal?>>? Principal { get; set; }

    /// <summary>Narrows the tools further, by an operation's metadata, say.</summary>
    public Func<OperationDescriptor, bool>? Include { get; set; }

    /// <summary>
    /// Inputs the server supplies rather than the model (a turn's id), by their name on the wire: taken out of every tool's
    /// schema, and filled from <see cref="Bind"/> on each call, over anything the model sent.
    /// </summary>
    public IReadOnlyCollection<string> BoundInputs { get; set; } = [];

    /// <summary>The values of <see cref="BoundInputs"/> for a call: asynchronous, so it can look them up itself rather than rely on <see cref="Principal"/> having run first.</summary>
    public Func<RequestContext<CallToolRequestParams>, OperationDescriptor, ValueTask<IReadOnlyDictionary<string, JsonElement>>>? Bind { get; set; }

    /// <summary>A result as content other than its JSON (an image the model should see), or null for the JSON.</summary>
    public Func<OperationDescriptor, JsonElement, IList<ContentBlock>?>? Render { get; set; }

    /// <summary>
    /// How a call is sent when this process has no dispatcher: to the app over the wire (its <c>/ops</c>), for an MCP server
    /// that runs beside the client. The default sends in-process through the <see cref="Dispatcher"/>'s client path.
    /// </summary>
    public Func<RequestContext<CallToolRequestParams>, string, JsonElement, CancellationToken, Task<Result<JsonElement>>>? Send { get; set; }

    /// <summary>The catalog to project, when the process has none of its own (a remote server: a Contracts project's <c>LeveeCatalog</c>).</summary>
    public OperationCatalog? Catalog { get; set; }
}

public static class LeveeMcpBuilderExtensions
{
    /// <summary>
    /// A tool for every operation a client may call (not internal, answering once), through the dispatcher's client path:
    /// authorized, scoped and audited like any call. A tool's name is its operation's with each dot an underscore
    /// (<c>notes.create</c> is <c>notes_create</c>); two operations that would share one fail at startup.
    /// </summary>
    public static IMcpServerBuilder WithLeveeOperations(this IMcpServerBuilder builder,
        Func<RequestContext<CallToolRequestParams>, ValueTask<Principal?>> principal, Func<OperationDescriptor, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return builder.WithLeveeOperations(o =>
        {
            o.Principal = principal;
            o.Include = include;
        });
    }

    /// <summary>As the other overload, with bound inputs, rendered results or a remote catalog (<see cref="LeveeMcpOptions"/>).</summary>
    public static IMcpServerBuilder WithLeveeOperations(this IMcpServerBuilder builder, Action<LeveeMcpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var mcp = new LeveeMcpOptions();
        configure(mcp);
        if (mcp.Send is null && mcp.Principal is null)
        {
            throw new InvalidOperationException("Levee's MCP tools need a Principal to send in-process, or a Send to the app over the wire.");
        }
        if (mcp.BoundInputs.Count > 0 && mcp.Bind is null)
        {
            throw new InvalidOperationException("BoundInputs are filled by Bind: set it.");
        }
        builder.Services.AddOptions<McpServerOptions>().PostConfigure<IServiceProvider>((options, services) =>
        {
            var catalog = mcp.Catalog ?? services.GetService<OperationCatalog>()
                ?? throw new InvalidOperationException("No catalog to project: register Levee (AddLeveeOperations), or set LeveeMcpOptions.Catalog.");
            options.ToolCollection ??= [];
            foreach (var tool in OperationTool.For(catalog, services.GetService<LeveeOptions>() ?? new LeveeOptions(), mcp))
            {
                options.ToolCollection.Add(tool);
            }
        });
        return builder;
    }
}

internal sealed class OperationTool(OperationDescriptor descriptor, Tool tool, LeveeOptions levee, LeveeMcpOptions mcp) : McpServerTool
{
    public override Tool ProtocolTool => tool;

    public override IReadOnlyList<object> Metadata => [];

    public OperationDescriptor Descriptor => descriptor;

    public static List<OperationTool> For(OperationCatalog catalog, LeveeOptions levee, LeveeMcpOptions mcp)
    {
        var tools = catalog.All.Where(d => !d.Internal && !d.Streams && (mcp.Include?.Invoke(d) ?? true))
            .Select(d => new OperationTool(d, Describe(d, levee, mcp.BoundInputs), levee, mcp))
            .ToList();
        if (tools.GroupBy(t => t.ProtocolTool.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } clash)
        {
            throw new InvalidOperationException(
                $"{string.Join(" and ", clash.Select(t => t.Descriptor.Name))} would both be the MCP tool {clash.Key}: rename one operation.");
        }
        if (tools.FirstOrDefault(t => t.ProtocolTool.Name.Length > 64) is { } longest)
        {
            throw new InvalidOperationException($"{longest.Descriptor.Name} is too long a name for an MCP tool (64 characters).");
        }
        return tools;
    }

    private static Tool Describe(OperationDescriptor d, LeveeOptions levee, IReadOnlyCollection<string> bound)
    {
        var schema = LeveeJson.Schema(d.InputType, levee.Json);
        if (schema is JsonObject root)
        {
            (root["properties"] as JsonObject)?.ToList().Where(p => bound.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).ToList().ForEach(p => ((JsonObject)root["properties"]!).Remove(p.Key));
            if (root["required"] is JsonArray required)
            {
                foreach (var name in required.Where(n => n is not null && bound.Contains(n.GetValue<string>(), StringComparer.OrdinalIgnoreCase)).ToList())
                {
                    required.Remove(name);
                }
            }
        }
        return new Tool
        {
            Name = d.Name.Replace('.', '_'),
            Title = d.Name,
            Description = string.IsNullOrWhiteSpace(d.Summary) ? d.Name : d.Summary,
            InputSchema = JsonSerializer.SerializeToElement(schema),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = d.ReadOnly,
                IdempotentHint = d.Idempotency is Idempotency.Safe or Idempotency.Idempotent,
                // A create refused on a repeat (Conflicts) only adds; anything else that writes may overwrite or remove.
            DestructiveHint = !d.ReadOnly && d.Idempotency != Idempotency.Conflicts,
                OpenWorldHint = d.Effects.HasFlag(Effects.External),
            },
        };
    }

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Func<string, JsonElement, CancellationToken, Task<Result<JsonElement>>> send = mcp.Send is { } remote
            ? (name, input, ct) => remote(request, name, input, ct)
            : await InProcessAsync(request);
        var bound = mcp.Bind is { } bind ? await bind(request, descriptor) : null;
        var log = request.Services?.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("Levee.Mcp");
        return await CallAsync(send, request.Params?.Arguments, bound, cancellationToken, log);
    }

    private static readonly Action<Microsoft.Extensions.Logging.ILogger, string, Exception?> RenderFailedMessage =
        Microsoft.Extensions.Logging.LoggerMessage.Define<string>(Microsoft.Extensions.Logging.LogLevel.Error, new(1, "RenderFailed"),
            "Rendering {Operation}'s result failed; answered as JSON");

    private static void RenderFailed(Microsoft.Extensions.Logging.ILogger log, Exception e, string operation) => RenderFailedMessage(log, operation, e);

    private async Task<Func<string, JsonElement, CancellationToken, Task<Result<JsonElement>>>> InProcessAsync(RequestContext<CallToolRequestParams> request)
    {
        var caller = await mcp.Principal!(request);
        var dispatcher = request.Services?.GetRequiredService<Dispatcher>()
            ?? throw new InvalidOperationException("The MCP server has no services to find Levee's dispatcher in.");
        return (name, input, ct) => caller is null
            ? Task.FromResult<Result<JsonElement>>(Error.Unauthenticated("Sign in first."))
            : dispatcher.SendFromClientAsync(name, input, caller, ct);
    }

    /// <summary>The call itself: the arguments, bound inputs over them, as the operation's input; the result or the problem as the answer.</summary>
    internal async Task<CallToolResult> CallAsync(Func<string, JsonElement, CancellationToken, Task<Result<JsonElement>>> send,
        IDictionary<string, JsonElement>? arguments, IReadOnlyDictionary<string, JsonElement>? bound, CancellationToken ct,
        Microsoft.Extensions.Logging.ILogger? log = null)
    {
        // A bound input is the server's alone: whatever the model sent for it is dropped, and the server must supply it.
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in arguments ?? new Dictionary<string, JsonElement>())
        {
            // In any casing: the wire binds names case-insensitively, so "TEXT" would reach a bound "text".
            if (!mcp.BoundInputs.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                merged[name] = value;
            }
        }
        foreach (var name in mcp.BoundInputs)
        {
            if (bound is null || !bound.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException($"Bind supplied no {name} for {descriptor.Name}: every bound input is the server's to fill.");
            }
            merged[name] = value;
        }
        var result = await send(descriptor.Name, JsonSerializer.SerializeToElement(merged, levee.Json), ct);
        if (!result.IsSuccess)
        {
            return Failed(result.Error);
        }
        // After the commit: a Render that fails is the server's bug, and the result still goes back, as JSON.
        IList<ContentBlock>? rendered = null;
        try
        {
            rendered = mcp.Render?.Invoke(descriptor, result.Value);
        }
        catch (Exception e)
        {
            if (log is not null)
            {
                RenderFailed(log, e, descriptor.Name);
            }
        }
        if (rendered is not null)
        {
            return new CallToolResult { Content = rendered };
        }
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = result.Value.GetRawText() }],
            StructuredContent = result.Value.ValueKind == JsonValueKind.Object ? result.Value : null,
        };
    }

    private CallToolResult Failed(Error error) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(ProblemView.From(error, Activity.Current?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString()), levee.Json),
            },
        ],
    };
}
