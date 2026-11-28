using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Levee.Client;

public sealed class LeveeClientOptions
{
    /// <summary>Where the platform maps its operations: <c>MapLeveeOperations</c>'s prefix.</summary>
    public string Prefix { get; set; } = "/ops";

    /// <summary>
    /// How many times a transient failure (<see cref="Error.Transient"/>) is tried again before it's answered. A write is
    /// retried under the idempotency key it was first sent with, so a retry after a lost response gets the first result. A
    /// write that acts outside the platform (<see cref="Effects.External"/>) is retried only when it declares a repeat safe
    /// (<see cref="Idempotency.Idempotent"/>): one that failed rolled back, but what it did outside may have happened.
    /// </summary>
    public int Retries { get; set; } = 2;

    /// <summary>The wait before the first retry, doubled each time, unless the failure says when to come back.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long after its first send a call is still retried. A write's key is the platform's for its
    /// <c>LeveeOptions.IdempotencyKeyLifetime</c> (24 hours) from its commit, so a window well inside that is what makes a
    /// retry find the first result rather than write again.
    /// </summary>
    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether each write carries an idempotency key. Off for a client that sends as no one (what anyone may do), whose
    /// caller the platform can't tell apart from anyone else's, so it holds no key: its writes are sent once, never retried.
    /// </summary>
    public bool IdempotencyKeys { get; set; } = true;
}

/// <summary>
/// A platform's operations over HTTP, typed by a Contracts project's catalog (<c>LeveeCatalog.Catalog</c>): one wire, the
/// strict one, and a failure as the <see cref="Error"/> the platform answered. Authentication is the app's, a handler on
/// the <see cref="HttpClient"/> (<c>AddLeveeClient(...).AddHttpMessageHandler&lt;T&gt;()</c>). Anything that isn't the
/// platform's own answer (a connection refused, a body cut off, a sign-in page, what the contract doesn't read) is
/// <see cref="ErrorKind.Unavailable"/> <see cref="LeveeErrors.Unreachable"/>; the caller's cancellation is thrown, never answered.
/// </summary>
public sealed class LeveeClient(HttpClient http, OperationCatalog catalog, LeveeClientOptions? options = null)
{
    private readonly LeveeClientOptions _options = options ?? new();

    /// <summary>
    /// Sends <paramref name="operation"/> and answers its result, or its failure. A write carries an idempotency key of its
    /// own, kept across its retries, so it's written once however many times it's sent within <see cref="LeveeClientOptions.RetryWindow"/>.
    /// </summary>
    public async Task<Result<TResult>> SendAsync<TResult>(IOperation<TResult> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var found = Find(operation);
        if (!found.IsSuccess)
        {
            return found.Error;
        }
        var (descriptor, body) = found.Value;
        var key = descriptor.ReadOnly || !_options.IdempotencyKeys ? null : Guid.CreateVersion7().ToString();
        var retries = descriptor.ReadOnly || key is not null && ((descriptor.Effects & Effects.External) == 0 || descriptor.Idempotency == Idempotency.Idempotent)
            ? _options.Retries
            : 0;
        var answered = await WithRetriesAsync(retries, async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Path(descriptor.Name)) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (key is not null)
            {
                request.Headers.Add("Idempotency-Key", key);
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return await FailureAsync(response, ct);
            }
            if (!Is(response, "application/json"))
            {
                return NotThePlatform(response);
            }
            await using var content = await response.Content.ReadAsStreamAsync(ct);
            try
            {
                return Result<object?>.Success(await JsonSerializer.DeserializeAsync(content, descriptor.ResultType, LeveeJson.Options, ct));
            }
            catch (JsonException e)
            {
                return Unreachable($"{descriptor.Name} was answered with what its contract doesn't read: {e.Message}");
            }
        }, ct);
        return answered.IsSuccess ? Result<TResult>.Success((TResult)answered.Value!) : answered.Error;
    }

    /// <summary>Opens <paramref name="subscription"/>'s stream: its items as they come, ended by the platform's last failure if it fails.</summary>
    public async Task<Result<IAsyncEnumerable<TItem>>> SubscribeAsync<TItem>(ISubscription<TItem> subscription, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var found = Find(subscription);
        if (!found.IsSuccess)
        {
            return found.Error;
        }
        var (descriptor, body) = found.Value;
        var input = Uri.EscapeDataString(System.Text.Encoding.UTF8.GetString(body));
        var opened = await WithRetriesAsync(_options.Retries, async () =>
        {
            var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{Path(descriptor.Name)}/events?input={input}"),
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode && Is(response, "text/event-stream"))
            {
                return Result<object?>.Success(response);
            }
            using (response)
            {
                return response.IsSuccessStatusCode ? NotThePlatform(response) : await FailureAsync(response, ct);
            }
        }, ct);
        return opened.IsSuccess
            ? Result<IAsyncEnumerable<TItem>>.Success(Items<TItem>((HttpResponseMessage)opened.Value!, descriptor, CancellationToken.None))
            : opened.Error;
    }

    /// <summary>The operation's contract and its input on the wire; an input its own type refuses is refused here, as the platform would.</summary>
    private Result<(OperationDescriptor Descriptor, byte[] Body)> Find(IOperation operation)
    {
        if (catalog.Find(operation.GetType()) is not { } descriptor)
        {
            return Error.NotFound(LeveeErrors.UnknownOperation, $"{operation.GetType().Name} isn't in the catalog this client was given.");
        }
        try
        {
            return (descriptor, JsonSerializer.SerializeToUtf8Bytes(operation, descriptor.InputType, LeveeJson.Options));
        }
        catch (JsonException e)
        {
            return Error.InvalidInput(e, descriptor.Name);
        }
    }

    private static async IAsyncEnumerable<TItem> Items<TItem>(HttpResponseMessage response, OperationDescriptor descriptor,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var items = SseParser.Create(stream).EnumerateAsync(ct).GetAsyncEnumerator(ct);
            while (true)
            {
                try
                {
                    if (!await items.MoveNextAsync())
                    {
                        yield break;
                    }
                }
                catch (Exception e) when (e is IOException or HttpRequestException && !ct.IsCancellationRequested)
                {
                    throw new OperationFailedException(Unreachable($"{descriptor.Name}'s stream broke off: {e.Message}"));
                }
                if (items.Current.EventType != "keepalive")
                {
                    yield return Item<TItem>(items.Current, descriptor);
                }
            }
        }
    }

    /// <summary>An item, or the stream's last word: an <c>error</c> event is the platform's problem, and anything unreadable ends it as out of reach.</summary>
    private static TItem Item<TItem>(SseItem<string> item, OperationDescriptor descriptor)
    {
        try
        {
            if (item.EventType == "error")
            {
                throw new OperationFailedException(JsonSerializer.Deserialize<ProblemView>(item.Data, LeveeJson.Options) is { } problem
                    ? Error.FromProblem(problem, null)
                    : Unreachable($"{descriptor.Name}'s stream ended with an error that isn't a problem."));
            }
            return (TItem)JsonSerializer.Deserialize(item.Data, descriptor.ResultType, LeveeJson.Options)!;
        }
        catch (JsonException e)
        {
            throw new OperationFailedException(Unreachable($"{descriptor.Name}'s stream sent what its contract doesn't read: {e.Message}"));
        }
    }

    private string Path(string name) => $"{_options.Prefix.TrimEnd('/')}/{name}";

    private async Task<Result<object?>> WithRetriesAsync(int retries, Func<Task<Result<object?>>> send, CancellationToken ct)
    {
        var since = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            Result<object?> answered;
            try
            {
                answered = await send();
            }
            // A body cut off is an IOException (HttpIOException) as it's read, past the status that said it was coming.
            catch (Exception e) when (e is HttpRequestException or IOException && !ct.IsCancellationRequested)
            {
                answered = Unreachable($"The platform couldn't be reached: {e.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                answered = Unreachable("The platform didn't answer in time.");
            }
            var wait = answered.IsSuccess ? TimeSpan.Zero : answered.Error.RetryAfter ?? _options.RetryDelay * Math.Pow(2, attempt);
            if (answered.IsSuccess || !answered.Error.Transient || attempt >= retries || since.Elapsed + wait > _options.RetryWindow)
            {
                return answered;
            }
            await Task.Delay(wait, ct);
        }
    }

    /// <summary>The platform's problem, read back into its error; anything else (a proxy's page) is the platform out of reach.</summary>
    private static async Task<Result<object?>> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (Is(response, "application/problem+json"))
        {
            try
            {
                await using var content = await response.Content.ReadAsStreamAsync(ct);
                if (await JsonSerializer.DeserializeAsync<ProblemView>(content, LeveeJson.Options, ct) is { } problem)
                {
                    return Error.FromProblem(problem, response.Headers.RetryAfter?.Delta);
                }
            }
            catch (JsonException)
            {
            }
        }
        return Unreachable($"The platform answered {(int)response.StatusCode} with no problem of its own.");
    }

    /// <summary>Whether the response is <paramref name="mediaType"/>: media types aren't case-sensitive.</summary>
    private static bool Is(HttpResponseMessage response, string mediaType) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase);

    private static Error NotThePlatform(HttpResponseMessage response) =>
        Unreachable($"Something answered {(int)response.StatusCode} as {response.Content.Headers.ContentType?.MediaType ?? "nothing"}, not the platform's contract (a sign-in page, a proxy).");

    private static Error Unreachable(string message) => Error.Unavailable(LeveeErrors.Unreachable, message);
}

public static class LeveeClientServiceCollectionExtensions
{
    /// <summary>
    /// A <see cref="LeveeClient"/> for the platform whose contract is <paramref name="catalog"/> (a Contracts project's
    /// <c>LeveeCatalog.Catalog</c>), through <c>IHttpClientFactory</c>: set its base address in <paramref name="configure"/>,
    /// and add the app's authentication to what this returns, as a handler.
    /// </summary>
    public static IHttpClientBuilder AddLeveeClient(this IServiceCollection services, OperationCatalog catalog, Action<HttpClient> configure,
        Action<LeveeClientOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);
        var configured = new LeveeClientOptions();
        options?.Invoke(configured);
        return services.AddHttpClient(nameof(LeveeClient), configure).AddTypedClient(http => new LeveeClient(http, catalog, configured));
    }
}
