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
    private LeveeClient Client(OperationCatalog? catalog = null, int retries = 2, DelegatingHandler? between = null, bool keys = true)
    {
        HttpMessageHandler handler = _app.GetTestServer().CreateHandler();
        if (between is not null)
        {
            between.InnerHandler = handler;
            handler = between;
        }
        var http = new HttpClient(new Bearer { InnerHandler = handler }) { BaseAddress = new Uri("http://platform") };
        return new LeveeClient(http, catalog ?? LeveeComposition.Catalog, new LeveeClientOptions { Retries = retries, RetryDelay = TimeSpan.Zero, IdempotencyKeys = keys });
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
    public async Task An_outside_effect_not_declared_safe_to_repeat_is_never_sent_again()
    {
        var reference = $"c{Guid.NewGuid():N}";

        var charged = await Client().SendAsync(new Charge("acme", reference), Ct);

        Assert.Equal(ErrorKind.Internal, charged.Error!.Kind);
        Assert.Equal(1, Counted.Runs[reference]);
    }

    /// <summary>The platform answered, and the body broke off on the way: the status said it was coming.</summary>
    private sealed class CutOffFirstAnswer : DelegatingHandler
    {
        private int _sent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var answer = await base.SendAsync(request, cancellationToken);
            if (Interlocked.Increment(ref _sent) > 1)
            {
                return answer;
            }
            answer.Dispose();
            var cut = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) };
            cut.Content.Headers.ContentType = new("application/json");
            return cut;
        }

        private sealed class BrokenStream : MemoryStream
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                throw new IOException("The response ended prematurely.");

            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The response ended prematurely.");
        }
    }

    [Fact]
    public async Task A_write_whose_body_broke_off_is_sent_again_under_its_key()
    {
        var name = $"n{Guid.NewGuid():N}";

        var counted = await Client(between: new CutOffFirstAnswer()).SendAsync(new Count("acme", name), Ct);

        Assert.Equal(1, counted.Value);
        Assert.Equal(1, Counted.Runs[name]);
    }

    /// <summary>A sign-in page in front of the platform, answering 200 for everything.</summary>
    private sealed class SignInPage : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>sign in</html>", System.Text.Encoding.UTF8, "text/html") });
    }

    [Fact]
    public async Task What_isnt_the_platform_answering_is_the_platform_out_of_reach()
    {
        var client = Client(retries: 0, between: new SignInPage());

        var sent = await client.SendAsync(new Echo("hi"), Ct);
        var opened = await client.SubscribeAsync(new Ticks(1), Ct);

        Assert.Equal(LeveeErrors.Unreachable, sent.Error!.Code);
        Assert.Equal(LeveeErrors.Unreachable, opened.Error!.Code);
    }

    [Fact]
    public async Task A_client_that_holds_no_keys_sends_a_write_once()
    {
        var lost = new LoseFirstAnswer();

        var counted = await Client(between: lost, keys: false).SendAsync(new Count("acme", $"n{Guid.NewGuid():N}"), Ct);

        Assert.Equal(LeveeErrors.Unreachable, counted.Error!.Code);
        Assert.Equal([null], lost.Keys);
    }

    /// <summary>Rewrites what every answer says it is: a media type in another case, or a stream whose item isn't the contract's.</summary>
    private sealed class Rewrite(Func<HttpResponseMessage, HttpResponseMessage> answer) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            answer(await base.SendAsync(request, cancellationToken));
    }

    [Fact]
    public async Task A_media_type_is_read_whatever_its_case()
    {
        var shouted = new Rewrite(answer =>
        {
            answer.Content.Headers.ContentType = new("Application/JSON");
            return answer;
        });

        var said = await Client(between: shouted).SendAsync(new Echo("hi"), Ct);

        Assert.Equal("hi", said.Value.Said);
    }

    [Fact]
    public async Task A_stream_item_the_contract_doesnt_read_ends_the_stream_as_a_failure()
    {
        var garbled = new Rewrite(answer =>
        {
            answer.Dispose();
            var stream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"number\":\n\n") };
            stream.Content.Headers.ContentType = new("text/event-stream");
            return stream;
        });
        var opened = await Client(between: garbled).SubscribeAsync(new Ticks(1), Ct);

        var failed = await Assert.ThrowsAsync<OperationFailedException>(async () =>
        {
            await foreach (var _ in opened.Value.WithCancellation(Ct))
            {
            }
        });

        Assert.Equal(LeveeErrors.Unreachable, failed.Error.Code);
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

/// <summary>Charges a card, then fails: what it did outside the platform isn't rolled back with it.</summary>
[Operation("probe.charge", "member", Effects = Effects.Write | Effects.External, Idempotency = Idempotency.Conflicts)]
public sealed record Charge(string Tenant, string Reference) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class ChargeHandler : IOperationHandler<Charge, Unit>
{
    public Task<Result<Unit>> HandleAsync(Charge operation, OperationContext context, CancellationToken ct)
    {
        Counted.Runs.AddOrUpdate(operation.Reference, 1, (_, n) => n + 1);
        throw new InvalidOperationException("the charge went through, and then this broke");
    }
}
