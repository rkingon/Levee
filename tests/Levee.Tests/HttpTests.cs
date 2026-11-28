using System.Net;
using System.Text.Json;
using Levee.AspNetCore;
using Levee.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Levee.Tests;

public sealed class HttpTests : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _http;

    public HttpTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Probe:Greeter"] = "en";
        builder.Services.AddSingleton(new Store())
            .AddSingleton<IAccessPolicy, TestAccess>()
            .AddScoped<IUnitOfWorkFactory, FakeUnitOfWorkFactory>()
            .AddLeveeOperations(builder.Configuration);
        _app = builder.Build();
        // The host's authentication is a lookup: a key, a session.
        _app.MapLeveeOperations(async http =>
        {
            await Task.Yield();
            return http.Request.Headers.Authorization.ToString() == "Bearer ada" ? new Member("ada", "acme") : null;
        });
        _app.StartAsync().GetAwaiter().GetResult();
        _http = _app.GetTestClient();
        _http.DefaultRequestHeaders.Authorization = new("Bearer", "ada");
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task The_catalog_shows_schemas_and_metadata_but_not_internal_operations()
    {
        using var response = await _http.GetAsync(new Uri("/ops", UriKind.Relative), TestContext.Current.CancellationToken);
        var operations = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.DoesNotContain(operations.EnumerateArray(), o => o.GetProperty("name").GetString() == "auth.sign_in");
        var remember = operations.EnumerateArray().Single(o => o.GetProperty("name").GetString() == "probe.remember");
        Assert.True(remember.GetProperty("input").GetProperty("properties").GetProperty("pin").GetProperty("writeOnly").GetBoolean());
        var speak = operations.EnumerateArray().Single(o => o.GetProperty("name").GetString() == "probe.speak");
        Assert.Equal("outside", speak.GetProperty("metadata").GetProperty("Turn").GetProperty("reach").GetString());
    }

    private async Task<(HttpResponseMessage Response, JsonElement Problem)> Post(string name, string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(new Uri($"/ops/{name}", UriKind.Relative), content, TestContext.Current.CancellationToken);
        return (response, JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement);
    }

    private async Task<(HttpStatusCode Status, string Body)> PostKeyed(string name, string body, params string[] keys)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/ops/{name}", UriKind.Relative))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        foreach (var key in keys)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_idempotency_key_makes_a_write_safe_to_send_again()
    {
        var body = $$"""{"tenant":"acme","name":"{{Guid.NewGuid():N}}"}""";

        var first = await PostKeyed("probe.count", body, "k1");
        var again = await PostKeyed("probe.count", body, "k1");
        var twice = await PostKeyed("probe.count", body, "k1", "k2");

        Assert.Equal((HttpStatusCode.OK, "1"), first);
        Assert.Equal(first, again);
        Assert.Equal(HttpStatusCode.BadRequest, twice.Status);
        Assert.Contains(LeveeErrors.InvalidIdempotencyKey, twice.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_keyed_write_answers_the_wire_it_kept_however_its_converters_write()
    {
        var body = $$"""{"tenant":"acme","name":"{{Guid.NewGuid():N}}"}""";

        var first = await PostKeyed("probe.stamp", body, "k1");
        var again = await PostKeyed("probe.stamp", body, "k1");

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(first, again);
    }

    [Fact]
    public async Task A_failure_is_a_problem_with_its_code_kind_and_trace()
    {
        var (response, problem) = await Post("probe.refuse", "{}");
        using var owned = response;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(("Bad Request", 400, "probe.refused", "validation", false),
            (problem.GetProperty("title").GetString(), problem.GetProperty("status").GetInt32(), problem.GetProperty("code").GetString(),
                problem.GetProperty("kind").GetString(), problem.GetProperty("transient").GetBoolean()));
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("trace").GetString()));
    }

    [Fact]
    public async Task A_bug_is_a_500_with_only_the_trace_to_find_it_by()
    {
        var (response, problem) = await Post("probe.crash", "{}");
        using var owned = response;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(("internal", true), (problem.GetProperty("code").GetString(), problem.GetProperty("transient").GetBoolean()));
        Assert.Contains(problem.GetProperty("trace").GetString()!, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bug", problem.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_says_when_to_retry()
    {
        var (response, problem) = await Post("probe.busy", "{}");
        using var owned = response;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter!.Delta);
        Assert.True(problem.GetProperty("transient").GetBoolean());
    }

    [Fact]
    public async Task Malformed_input_names_the_field()
    {
        var (response, problem) = await Post("probe.echo", """{"text":5}""");
        using var owned = response;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("input.invalid", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("$.text", out _), problem.GetRawText());
    }

    [Fact]
    public async Task The_catalog_lists_what_each_operation_fails_with()
    {
        using var response = await _http.GetAsync(new Uri("/ops", UriKind.Relative), TestContext.Current.CancellationToken);
        var operations = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        var refuse = operations.EnumerateArray().Single(o => o.GetProperty("name").GetString() == "probe.refuse");
        Assert.Equal(["probe.refused"], refuse.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("access.tenant", refuse.GetProperty("refusals").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_stream_that_fails_ends_with_an_error_event()
    {
        using var response = await _http.GetAsync(new Uri("/ops/probe.crashing_ticks/events", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("data: {\"number\":1}", body, StringComparison.Ordinal);
        Assert.Contains("event: error", body, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"internal\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quiet_stream_answers_its_status_as_it_opens()
    {
        using var opened = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        opened.CancelAfter(TimeSpan.FromSeconds(5));

        using var response = await _http.GetAsync(new Uri("/ops/probe.quiet/events", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, opened.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_subscription_streams_as_server_sent_events()
    {
        using var response = await _http.GetAsync(new Uri("/ops/probe.ticks/events?input=" + Uri.EscapeDataString("""{"count":2}"""), UriKind.Relative),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("data: {\"number\":1}", body, StringComparison.Ordinal);
        Assert.Contains("data: {\"number\":2}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_internal_operation_isnt_there_for_a_client()
    {
        using var response = await _http.PostAsync(new Uri("/ops/auth.sign_in", UriKind.Relative), new StringContent("""{"link":"x"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_operation_runs_as_the_caller_the_host_looked_up()
    {
        using var ok = await _http.PostAsync(new Uri("/ops/probe.echo", UriKind.Relative), new StringContent("""{"text":"hi"}"""),
            TestContext.Current.CancellationToken);
        _http.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await _http.PostAsync(new Uri("/ops/probe.echo", UriKind.Relative), new StringContent("""{"text":"hi"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal("""{"said":"hi"}""", await ok.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}

/// <summary>Written by a converter that never writes the same twice: a write's wire is what it was written as, once.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(StampJson))]
[ValueObject(typeof(int))]
public sealed record Stamp(int Value);

public sealed class StampJson : System.Text.Json.Serialization.JsonConverter<Stamp>
{
    private static int _written;

    public override Stamp Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) => new(reader.GetInt32());

    public override void Write(System.Text.Json.Utf8JsonWriter writer, Stamp value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteNumberValue(Interlocked.Increment(ref _written));
}

public sealed record Stamped(Stamp Stamp);

[Operation("probe.stamp", "member", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record StampIt(string Tenant, string Name) : IOperation<Stamped>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class StampItHandler : IOperationHandler<StampIt, Stamped>
{
    public Task<Result<Stamped>> HandleAsync(StampIt operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<Stamped>>(new Stamped(new Stamp(0)));
}
