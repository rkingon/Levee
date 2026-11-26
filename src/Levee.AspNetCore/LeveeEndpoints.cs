using System.Net.ServerSentEvents;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Levee.AspNetCore;

/// <summary>An operation as a client sees it: its contract, the JSON Schemas of its input and result, and the app's metadata.</summary>
/// <param name="Streams">A subscription: <c>GET {prefix}/{name}/events?input={json}</c> streams its items as server-sent events; <see cref="Result"/> is one item.</param>
/// <param name="Errors">The codes it fails with besides Levee's own (<see cref="LeveeErrors"/>).</param>
/// <param name="Refusals">The codes the access policy and admissions may refuse it with, the same for every operation.</param>
public sealed record OperationView(string Name, string Summary, string Access, Effects Effects, Idempotency Idempotency, bool ReadOnly, bool Streams,
    JsonNode Input, JsonNode Result, IReadOnlyList<string> Errors, IReadOnlyList<string> Refusals, IReadOnlyDictionary<string, JsonObject> Metadata);

public static class LeveeEndpointRouteBuilderExtensions
{
    /// <summary>
    /// <c>GET {prefix}</c> lists the catalog; <c>POST {prefix}/{name}</c> runs an operation with the body as its input. Both
    /// need a caller: <paramref name="principal"/> is the host's authentication, who the request runs as, or null for no one.
    /// Internal operations are neither listed nor run.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026", Justification = "The overloads differ by the principal's delegate, not by the optional prefix.")]
    public static RouteGroupBuilder MapLeveeOperations(this IEndpointRouteBuilder endpoints, Func<HttpContext, ValueTask<Principal?>> principal,
        string prefix = "/ops")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(principal);
        var group = endpoints.MapGroup(prefix);
        group.MapGet("", async (HttpContext http, Dispatcher dispatcher, LeveeOptions options) => await principal(http) is null
            ? Fail(Error.Unauthenticated("Sign in first."), http, options)
            : Results.Json(dispatcher.Catalog.All.Where(d => !d.Internal).Select(d => View(d, dispatcher.Catalog.Refusals, options.Json)), options.Json));
        group.MapPost("{name}", async (string name, HttpContext http, Dispatcher dispatcher, LeveeOptions options) =>
        {
            if (await principal(http) is not { } caller)
            {
                return Fail(Error.Unauthenticated("Sign in first."), http, options);
            }
            // Buffered, so an empty body is no input however it was sent (no Content-Length, chunked).
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body, http.RequestAborted);
            JsonElement input = default;
            if (body.Length > 0)
            {
                body.Position = 0;
                try
                {
                    input = await JsonSerializer.DeserializeAsync<JsonElement>(body, options.Json, http.RequestAborted);
                }
                catch (JsonException e)
                {
                    return Fail(Error.InvalidInput(e), http, options);
                }
            }
            var result = await dispatcher.SendFromClientAsync(name, input, caller, http.RequestAborted);
            return result.IsSuccess ? Results.Json(result.Value, options.Json) : Fail(result.Error, http, options);
        });
        group.MapGet("{name}/events", async (string name, string? input, HttpContext http, Dispatcher dispatcher, LeveeOptions options) =>
        {
            if (await principal(http) is not { } caller)
            {
                return Fail(Error.Unauthenticated("Sign in first."), http, options);
            }
            JsonElement parsed = default;
            if (!string.IsNullOrEmpty(input))
            {
                try
                {
                    parsed = JsonDocument.Parse(input).RootElement;
                }
                catch (JsonException e)
                {
                    return Fail(Error.InvalidInput(e), http, options);
                }
            }
            var opened = await dispatcher.SubscribeFromClientAsync(name, parsed, caller, http.RequestAborted);
            if (!opened.IsSuccess)
            {
                return Fail(opened.Error, http, options);
            }
            async Task<Error?> StillAllowed(CancellationToken ct) =>
                await principal(http) is { } now
                    ? await dispatcher.AuthorizeFromClientAsync(name, parsed, now, ct)
                    : Error.Unauthenticated("The caller's credentials no longer hold.");
            return TypedResults.ServerSentEvents(Events(opened.Value, http, options, StillAllowed, http.RequestAborted));
        });
        return group;
    }

    /// <summary>As the other overload, for a host whose authentication needs no I/O.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026", Justification = "The overloads differ by the principal's delegate, not by the optional prefix.")]
    public static RouteGroupBuilder MapLeveeOperations(this IEndpointRouteBuilder endpoints, Func<HttpContext, Principal?> principal, string prefix = "/ops")
    {
        ArgumentNullException.ThrowIfNull(principal);
        return endpoints.MapLeveeOperations(http => ValueTask.FromResult(principal(http)), prefix);
    }

    /// <summary>
    /// Each item as a message. Between items, a <c>keepalive</c> event every <see cref="LeveeOptions.SubscriptionKeepAlive"/>, and
    /// every <see cref="LeveeOptions.SubscriptionRecheck"/> the caller checked again. A failure, the handler's or a caller no
    /// longer allowed, is one last <c>error</c> event holding its problem, since the status is long sent.
    /// </summary>
    private static async IAsyncEnumerable<SseItem<string>> Events(IAsyncEnumerable<JsonElement> items, HttpContext http, LeveeOptions options,
        Func<CancellationToken, Task<Error?>> stillAllowed, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // The pump stops with the stream, however it ends (a failed recheck, a client gone), and is awaited, so the handler's
        // own finally runs and its scope is released.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = System.Threading.Channels.Channel.CreateBounded<SseItem<string>>(16);
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in items.WithCancellation(stop.Token))
                {
                    await channel.Writer.WriteAsync(new SseItem<string>(item.GetRawText()), stop.Token);
                }
            }
            catch (OperationFailedException f)
            {
                channel.Writer.TryWrite(Failure(f.Error, http, options));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            // Something now, so the status and headers go out as the stream opens, not with the first item.
            yield return new SseItem<string>("", "keepalive");
            var recheckAt = DateTimeOffset.UtcNow + options.SubscriptionRecheck;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(options.SubscriptionKeepAlive < options.SubscriptionRecheck ? options.SubscriptionKeepAlive : options.SubscriptionRecheck);
                bool more;
                var timedOut = false;
                try
                {
                    more = await channel.Reader.WaitToReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    more = true;
                    timedOut = true;
                }
                if (!more)
                {
                    yield break;
                }
                // Up to the recheck's deadline: a producer that keeps the channel full mustn't put the recheck off.
                while (DateTimeOffset.UtcNow < recheckAt && channel.Reader.TryRead(out var next))
                {
                    yield return next;
                    if (next.EventType == "error")
                    {
                        yield break;
                    }
                }
                if (DateTimeOffset.UtcNow >= recheckAt)
                {
                    recheckAt = DateTimeOffset.UtcNow + options.SubscriptionRecheck;
                    if (await stillAllowed(ct) is { } denied)
                    {
                        yield return Failure(denied, http, options);
                        yield break;
                    }
                }
                if (timedOut)
                {
                    yield return new SseItem<string>("", "keepalive");
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await pump;
        }
    }

    private static SseItem<string> Failure(Error error, HttpContext http, LeveeOptions options) =>
        new(JsonSerializer.Serialize(Problem(error, http), options.Json), "error");

    private static OperationView View(OperationDescriptor d, IReadOnlyList<string> refusals, JsonSerializerOptions json) => new(
        d.Name, d.Summary, d.Access, d.Effects, d.Idempotency, d.ReadOnly, d.Streams,
        LeveeJson.Schema(d.InputType, json), LeveeJson.Schema(d.ResultType, json), d.Errors, refusals,
        d.Metadata.ToDictionary(m => m.GetType().Name.EndsWith("Attribute", StringComparison.Ordinal) ? m.GetType().Name[..^"Attribute".Length] : m.GetType().Name, m => Values(m, json)));

    private static JsonObject Values(Attribute metadata, JsonSerializerOptions json)
    {
        var values = new JsonObject();
        foreach (var p in metadata.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != nameof(Attribute.TypeId)))
        {
            values[json.PropertyNamingPolicy?.ConvertName(p.Name) ?? p.Name] = JsonSerializer.SerializeToNode(p.GetValue(metadata), p.PropertyType, json);
        }
        return values;
    }

    private static IResult Fail(Error error, HttpContext http, LeveeOptions options) => LeveeProblems.Result(error, http, options.Json);

    private static ProblemView Problem(Error error, HttpContext http) => LeveeProblems.Problem(error, http);
}

/// <summary>
/// An <see cref="Error"/> as HTTP, the same for every projection: <see cref="LeveeEndpointRouteBuilderExtensions.MapLeveeOperations(IEndpointRouteBuilder, Func{HttpContext, ValueTask{Principal?}}, string)"/>
/// and an app's own (typed REST routes, a webhook) alike.
/// </summary>
public static class LeveeProblems
{
    public static int Status(ErrorKind kind) => ProblemView.StatusOf(kind);

    /// <summary>The problem body, its trace the error's or else the request's.</summary>
    public static ProblemView Problem(Error error, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(http);
        return ProblemView.From(error, System.Diagnostics.Activity.Current?.TraceId.ToHexString() ?? http.TraceIdentifier);
    }

    /// <summary>The response: <c>application/problem+json</c>, with <c>Retry-After</c> when the error carries a wait.</summary>
    public static IResult Result(Error error, HttpContext http, JsonSerializerOptions? json = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(http);
        if (error.RetryAfter is { } after)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(after.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var problem = Problem(error, http);
        return Results.Json(problem, json ?? LeveeJson.Options, "application/problem+json", problem.Status);
    }
}
