using System.Net;
using Levee.AspNetCore;
using Levee.Client;
using Levee.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Levee.Tests;

public sealed class ClientTests : IAsyncDisposable
{
    private readonly WebApplication _app;

    public ClientTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Probe:Greeter"] = "en";
        builder.Services.AddSingleton(new Store())
            .AddSingleton<IAccessPolicy, TestAccess>()
            .AddScoped<IUnitOfWorkFactory, FakeUnitOfWorkFactory>()
            .AddLeveeOperations(builder.Configuration);
        _app = builder.Build();
        _app.MapLeveeOperations(http => http.Request.Headers.Authorization.ToString() == "Bearer ada" ? new Member("ada", "acme") : null);
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The app's authentication, as a handler on the client; then whatever else stands between it and the platform.</summary>
    private LeveeClient Client(OperationCatalog? catalog = null, int retries = 2, DelegatingHandler? between = null)
    {
        HttpMessageHandler handler = _app.GetTestServer().CreateHandler();
        if (between is not null)
        {
            between.InnerHandler = handler;
            handler = between;
        }
        var http = new HttpClient(new Bearer { InnerHandler = handler }) { BaseAddress = new Uri("http://platform") };
        return new LeveeClient(http, catalog ?? LeveeComposition.Catalog, new LeveeClientOptions { Retries = retries, RetryDelay = TimeSpan.Zero });
    }

    private sealed class Bearer : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new("Bearer", "ada");
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>The platform did the work, then its answer was lost on the way back: a proxy answered instead.</summary>
    private sealed class LoseFirstAnswer : DelegatingHandler
    {
        public List<string?> Keys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Keys.Add(request.Headers.TryGetValues("Idempotency-Key", out var key) ? key.Single() : null);
            var answer = await base.SendAsync(request, cancellationToken);
            if (Keys.Count > 1)
            {
                return answer;
            }
            answer.Dispose();
            return new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") };
        }
    }

    [Fact]
    public async Task An_operation_is_sent_and_its_result_read_with_the_contract_wire()
    {
        var said = await Client().SendAsync(new Echo("hi"), Ct);

        Assert.Equal("hi", said.Value.Said);
    }

    [Fact]
    public async Task A_declared_failure_reads_back_as_the_error_the_platform_answered()
    {
        var refused = await Client().SendAsync(new Refuse(), Ct);

        Assert.Equal((ErrorKind.Validation, "probe.refused"), (refused.Error!.Kind, refused.Error.Code));
    }

    [Fact]
    public async Task A_bug_reads_back_as_internal_with_the_trace_to_quote()
    {
        var crashed = await Client(retries: 0).SendAsync(new Crash(), Ct);

        Assert.Equal(ErrorKind.Internal, crashed.Error!.Kind);
        Assert.False(string.IsNullOrEmpty(crashed.Error.Trace));
        Assert.Contains(crashed.Error.Trace!, crashed.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_whose_answer_was_lost_is_sent_again_under_its_key_and_written_once()
    {
        var lost = new LoseFirstAnswer();
        var name = $"n{Guid.NewGuid():N}";

        var counted = await Client(between: lost).SendAsync(new Count("acme", name), Ct);

        Assert.Equal(1, counted.Value);
        Assert.Equal(1, Counted.Runs[name]);
        Assert.Equal(2, lost.Keys.Count);
        Assert.NotNull(lost.Keys[0]);
        Assert.Equal(lost.Keys[0], lost.Keys[1]);
    }

    [Fact]
    public async Task A_platform_out_of_reach_is_unavailable_once_its_retries_run_out()
    {
        var gone = await Client(retries: 1, between: new Unreachable()).SendAsync(new Echo("hi"), Ct);

        Assert.Equal((ErrorKind.Unavailable, LeveeErrors.Unreachable), (gone.Error!.Kind, gone.Error.Code));
    }

    private sealed class Unreachable : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    [Fact]
    public async Task A_stream_delivers_its_items_and_ends_with_the_platforms_failure()
    {
        var client = Client();

        var ticks = await client.SubscribeAsync(new Ticks(3), Ct);
        var crashing = await client.SubscribeAsync(new CrashingTicks(), Ct);
        var read = new List<int>();
        var failed = await Assert.ThrowsAsync<OperationFailedException>(async () =>
        {
            await foreach (var tick in crashing.Value.WithCancellation(Ct))
            {
                read.Add(tick.Number);
            }
        });

        Assert.Equal([1, 2, 3], await ticks.Value.Select(t => t.Number).ToListAsync(Ct));
        Assert.Equal([1], read);
        Assert.Equal(ErrorKind.Internal, failed.Error.Kind);
    }

    [Fact]
    public async Task An_operation_outside_the_clients_catalog_is_refused_before_anything_is_sent()
    {
        var lost = new LoseFirstAnswer();
        var echoOnly = new OperationCatalog([LeveeComposition.Catalog.Find("probe.echo")!]);

        var refused = await Client(echoOnly, between: lost).SendAsync(new Count("acme", "x"), Ct);

        Assert.Equal(LeveeErrors.UnknownOperation, refused.Error!.Code);
        Assert.Empty(lost.Keys);
    }
}
