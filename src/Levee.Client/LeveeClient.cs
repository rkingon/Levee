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
    /// How long after its first send a call is still sent again. A write's key is the platform's for its
    /// <c>LeveeOptions.IdempotencyKeyLifetime</c> (24 hours, from just before its commit), so a window well inside that is what
    /// makes a retry find the first result rather than write again.
    /// </summary>
    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether each write carries an idempotency key. Off for a client that sends as no one (what anyone may do), whose
    /// caller the platform can't tell apart from anyone else's, so it holds no key: its writes are sent once, never retried.
    /// </summary>
    public bool IdempotencyKeys { get; set; } = true;

    /// <summary>The clock its retries wait by and its window is measured on.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;
}

/// <summary>
/// A platform's operations over HTTP, typed by a Contracts project's catalog (<c>LeveeCatalog.Catalog</c>), on the contract
/// wire the platform itself reads and writes, so what it refuses is refused here the same way. Authentication is the app's,
/// a handler on the <see cref="HttpClient"/> (<c>AddLeveeClient(...).AddHttpMessageHandler&lt;T&gt;()</c>). Anything that
/// isn't the platform's own answer (a connection refused, a body cut off, a sign-in page, what the contract doesn't read, a
/// problem that disagrees with its status) is <see cref="ErrorKind.Unavailable"/> <see cref="LeveeErrors.Unreachable"/>; the
/// caller's cancellation is thrown, never answered.
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
        var (descriptor, input) = found.Value;
        var body = System.Text.Encoding.UTF8.GetBytes(input.GetRawText());
        var key = descriptor.ReadOnly || !_options.IdempotencyKeys ? null : Guid.CreateVersion7().ToString();
        var retries = descriptor.ReadOnly || key is not null && ((descriptor.Effects & Effects.External) == 0 || descriptor.Idempotency == Idempotency.Idempotent)
            ? _options.Retries
            : 0;
        var answered = await WithRetriesAsync<object?>(retries, async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Path(descriptor.Name)) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (key is not null)
            {
                request.Headers.Add("Idempotency-Key", key);
            }
            using var deadline = Deadline(ct);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (await ReadWholeAsync(response, deadline, ct) is { } stalled)
            {
                return stalled;
            }
            if (!response.IsSuccessStatusCode)
            {
                return await FailureAsync(response, _options.Time, ct);
            }
            if (!Is(response, "application/json"))
            {
                return NotThePlatform(response);
            }
            // Whatever answered may say when to come back, whatever it sent.
            var retryAfter = RetryAfter(response.Headers.RetryAfter, _options.Time);
            await using var content = await response.Content.ReadAsStreamAsync(ct);
            JsonElement wire;
            try
            {
                wire = (await JsonDocument.ParseAsync(content, cancellationToken: ct)).RootElement.Clone();
            }
            catch (JsonException e)
            {
                return Unreachable($"{descriptor.Name} was answered with what isn't JSON: {e.Message}", retryAfter);
            }
            return ContractWire.ReadResult(descriptor, wire, message => Unreachable(message, retryAfter));
        }, ct);
        return answered.IsSuccess ? Result<TResult>.Success((TResult)answered.Value!) : answered.Error;
    }

    /// <summary>
    /// Opens <paramref name="subscription"/>'s stream: its items as they come, ended by the platform's last failure if it
    /// fails. The stream is the caller's to dispose, whether or not it's read: that's what closes it.
    /// </summary>
    public async Task<Result<LeveeSubscription<TItem>>> SubscribeAsync<TItem>(ISubscription<TItem> subscription, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var found = Find(subscription);
        if (!found.IsSuccess)
        {
            return found.Error;
        }
        var (descriptor, input) = found.Value;
        var opened = await WithRetriesAsync<HttpResponseMessage>(_options.Retries, async () =>
        {
            // The open is held to the client's timeout, not the stream it opens.
            using var deadline = Deadline(ct);
            var response = await http.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, $"{Path(descriptor.Name)}/events?input={Uri.EscapeDataString(input.GetRawText())}"),
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.IsSuccessStatusCode && Is(response, "text/event-stream"))
            {
                return Result<HttpResponseMessage>.Success(response);
            }
            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return NotThePlatform(response);
                }
                return await ReadWholeAsync(response, deadline, ct) ?? await FailureAsync(response, _options.Time, ct);
            }
        }, ct);
        return opened.IsSuccess ? new LeveeSubscription<TItem>(opened.Value, descriptor) : opened.Error;
    }

    /// <summary>The operation's contract and its input on the wire, refused as the platform would refuse it.</summary>
    private Result<(OperationDescriptor Descriptor, JsonElement Input)> Find(IOperation operation)
    {
        if (catalog.Find(operation.GetType()) is not { } descriptor)
        {
            return Error.NotFound(LeveeErrors.UnknownOperation, $"{operation.GetType().Name} isn't in the catalog this client was given.");
        }
        var input = ContractWire.WriteInput(descriptor, operation);
        return input.IsSuccess ? (descriptor, input.Value) : input.Error;
    }

    private string Path(string name) => $"{_options.Prefix.TrimEnd('/')}/{name}";

    /// <summary>Sends, and sends again after a transient failure, never past <see cref="LeveeClientOptions.RetryWindow"/> from the first send.</summary>
    private async Task<Result<T>> WithRetriesAsync<T>(int retries, Func<Task<Result<T>>> send, CancellationToken ct)
    {
        var time = _options.Time;
        var since = time.GetTimestamp();
        for (var attempt = 0; ; attempt++)
        {
            Result<T> answered;
            try
            {
                answered = await send();
            }
            // A body cut off is an IOException (HttpIOException) as it's read, past the status that said it was coming.
            catch (Exception e) when (e is HttpRequestException or IOException && !ct.IsCancellationRequested)
            {
                answered = Unreachable($"The platform couldn't be reached: {e.Message}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                answered = Unreachable("The platform didn't answer in time.");
            }
            var delay = answered.IsSuccess ? TimeSpan.Zero : answered.Error.RetryAfter ?? _options.RetryDelay * Math.Pow(2, attempt);
            // A retry that would be sent past the window isn't waited for: the failure is the answer now.
            if (answered.IsSuccess || !answered.Error.Transient || attempt >= retries || time.GetElapsedTime(since) + delay > _options.RetryWindow)
            {
                return answered;
            }
            await Task.Delay(delay, time, ct);
            // And none is sent past it, however late the wait ended.
            if (time.GetElapsedTime(since) > _options.RetryWindow)
            {
                return answered;
            }
        }
    }

    /// <summary>The platform's problem, read back into its error; anything else (a proxy's page) is the platform out of reach.</summary>
    private static async Task<Error> FailureAsync(HttpResponseMessage response, TimeProvider time, CancellationToken ct)
    {
        // Whoever answered (the platform, or a gateway in front of it) may say when to come back.
        var retryAfter = RetryAfter(response.Headers.RetryAfter, time);
        if (Is(response, "application/problem+json"))
        {
            try
            {
                await using var content = await response.Content.ReadAsStreamAsync(ct);
                if (await JsonSerializer.DeserializeAsync<ProblemView>(content, LeveeJson.Options, ct) is { } problem)
                {
                    return ContractWire.ReadProblem(problem, (int)response.StatusCode, retryAfter, message => Unreachable(message, retryAfter));
                }
            }
            catch (JsonException)
            {
            }
        }
        return Unreachable($"The platform answered {(int)response.StatusCode} with no problem of its own.", retryAfter);
    }

    /// <summary>One send's time, from before it's sent to the last byte of its answer: the client's own timeout.</summary>
    private CancellationTokenSource Deadline(CancellationToken ct)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(http.Timeout);
        return deadline;
    }

    /// <summary>
    /// An answer's body, read whole by its send's deadline; null once it's read. One that stalls past it, or breaks off, is the
    /// platform out of reach, as whatever answered said when to come back (its headers arrived).
    /// </summary>
    private async Task<Error?> ReadWholeAsync(HttpResponseMessage response, CancellationTokenSource deadline, CancellationToken ct)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(deadline.Token);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unreachable("The platform didn't answer in time.", RetryAfter(response.Headers.RetryAfter, _options.Time));
        }
        // A body cut off is an IOException (HttpIOException) as it's read; one that won't decode as its encoding says, an
        // InvalidDataException.
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException && !ct.IsCancellationRequested)
        {
            return Unreachable($"The platform's answer broke off: {e.Message}", RetryAfter(response.Headers.RetryAfter, _options.Time));
        }
    }

    /// <summary>When the platform says to come back: after a delay, or at a time, which is a delay from now.</summary>
    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, TimeProvider time) =>
        header?.Delta ?? (header?.Date is { } at ? (at - time.GetUtcNow() is { Ticks: > 0 } wait ? wait : TimeSpan.Zero) : null);

    internal static bool Is(HttpResponseMessage response, string mediaType) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>An answer that isn't the platform's contract; whatever gave it may still say when to come back.</summary>
    private Error NotThePlatform(HttpResponseMessage response) =>
        Unreachable($"Something answered {(int)response.StatusCode} as {response.Content.Headers.ContentType?.MediaType ?? "nothing"}, not the platform's contract (a sign-in page, a proxy).",
            RetryAfter(response.Headers.RetryAfter, _options.Time));

    internal static Error Unreachable(string message) => Unreachable(message, retryAfter: null);

    private static Error Unreachable(string message, TimeSpan? retryAfter) =>
        retryAfter is { } wait ? Error.Unavailable(LeveeErrors.Unreachable, message, wait) : Error.Unavailable(LeveeErrors.Unreachable, message);
}

/// <summary>
/// An open subscription's items, read once, as they come. It holds the stream open until it's disposed (or read to its
/// end); disposing it, read or not, closes it.
/// </summary>
public sealed class LeveeSubscription<TItem> : IAsyncEnumerable<TItem>, IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly OperationDescriptor _descriptor;
    private int _read;

    internal LeveeSubscription(HttpResponseMessage response, OperationDescriptor descriptor)
    {
        _response = response;
        _descriptor = descriptor;
    }

    public ValueTask DisposeAsync()
    {
        _response.Dispose();
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerator<TItem> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        Interlocked.Exchange(ref _read, 1) == 0
            ? Items(cancellationToken).GetAsyncEnumerator(cancellationToken)
            : throw new InvalidOperationException("A stream is read once.");

    private async IAsyncEnumerable<TItem> Items([EnumeratorCancellation] CancellationToken ct)
    {
        using (_response)
        {
            await using var stream = await _response.Content.ReadAsStreamAsync(ct);
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
                    throw new OperationFailedException(LeveeClient.Unreachable($"{_descriptor.Name}'s stream broke off: {e.Message}"));
                }
                // An item is a message, the stream's end an error; any other event (a keepalive) carries neither.
                if (items.Current.EventType is SseParser.EventTypeDefault or "error")
                {
                    yield return Item(items.Current);
                }
            }
        }
    }

    /// <summary>An item, or the stream's last word: an <c>error</c> event is the platform's problem, and anything unreadable ends it as out of reach.</summary>
    private TItem Item(SseItem<string> item)
    {
        JsonElement wire;
        try
        {
            wire = JsonDocument.Parse(item.Data).RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new OperationFailedException(LeveeClient.Unreachable($"{_descriptor.Name}'s stream sent what isn't JSON: {e.Message}"));
        }
        if (item.EventType == "error")
        {
            ProblemView? problem;
            try
            {
                problem = wire.Deserialize<ProblemView>(LeveeJson.Options);
            }
            catch (JsonException)
            {
                problem = null;
            }
            // The stream's status was sent as it opened; the problem names its own.
            throw new OperationFailedException(problem is null
                ? LeveeClient.Unreachable($"{_descriptor.Name}'s stream ended with an error that isn't a problem.")
                : ContractWire.ReadProblem(problem, problem.Status, null, LeveeClient.Unreachable));
        }
        var read = ContractWire.ReadResult(_descriptor, wire, LeveeClient.Unreachable);
        return read.IsSuccess ? (TItem)read.Value! : throw new OperationFailedException(read.Error);
    }
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
