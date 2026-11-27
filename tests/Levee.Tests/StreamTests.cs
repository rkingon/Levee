using Levee.AspNetCore;
using Levee.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Levee.Tests;

public sealed class StreamTests
{
    [Fact]
    public async Task A_stream_keeps_alive_and_ends_when_its_caller_no_longer_holds()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Probe:Greeter"] = "en";
        builder.Services.AddSingleton(new Store())
            .AddSingleton<IAccessPolicy, TestAccess>()
            .AddScoped<IUnitOfWorkFactory, FakeUnitOfWorkFactory>()
            .AddLeveeOperations(builder.Configuration, o =>
            {
                o.SubscriptionKeepAlive = TimeSpan.FromMilliseconds(40);
                o.SubscriptionRecheck = TimeSpan.FromMilliseconds(150);
            });
        await using var app = builder.Build();
        // The key is good when the stream opens and revoked by the first recheck, however slow the machine.
        var checks = 0;
        app.MapLeveeOperations(_ => Interlocked.Increment(ref checks) == 1 ? new Member("ada", "acme") : null);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = app.GetTestClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var body = await http.GetStringAsync(new Uri("/ops/probe.forever/events", UriKind.Relative), timeout.Token);

        Assert.Contains("data: {\"number\":1}", body, StringComparison.Ordinal);
        Assert.Contains("event: keepalive", body, StringComparison.Ordinal);
        Assert.Contains("event: error", body, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"unauthenticated\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_that_never_runs_dry_still_ends_when_revoked()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Probe:Greeter"] = "en";
        builder.Services.AddSingleton(new Store())
            .AddSingleton<IAccessPolicy, TestAccess>()
            .AddScoped<IUnitOfWorkFactory, FakeUnitOfWorkFactory>()
            .AddLeveeOperations(builder.Configuration, o => o.SubscriptionRecheck = TimeSpan.FromMilliseconds(100));
        await using var app = builder.Build();
        var checks = 0;
        app.MapLeveeOperations(_ => Interlocked.Increment(ref checks) == 1 ? new Member("ada", "acme") : null);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = app.GetTestClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        using var response = await http.GetAsync(new Uri("/ops/probe.firehose/events", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        string? line;
        var ended = false;
        var read = 0;
        while ((line = await reader.ReadLineAsync(timeout.Token)) is not null)
        {
            // A slow reader: the stream's writer waits on it while the producer fills the channel.
            if (++read % 200 == 0)
            {
                await Task.Delay(5, timeout.Token);
            }
            if (line == "event: error")
            {
                ended = true;
                break;
            }
        }

        Assert.True(ended);
    }

    [Fact]
    public async Task A_stream_ends_when_its_callers_scope_changes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Probe:Greeter"] = "en";
        builder.Services.AddSingleton(new Store())
            .AddSingleton<IAccessPolicy, TestAccess>()
            .AddScoped<IUnitOfWorkFactory, FakeUnitOfWorkFactory>()
            .AddLeveeOperations(builder.Configuration, o => o.SubscriptionRecheck = TimeSpan.FromMilliseconds(100));
        await using var app = builder.Build();
        // The same key, its session switched to another tenant after the stream opened: still authorized there, not here.
        var checks = 0;
        app.MapLeveeOperations(_ => new Key("k1", Interlocked.Increment(ref checks) == 1 ? "acme" : "globex"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = app.GetTestClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var body = await http.GetStringAsync(new Uri("/ops/probe.mine_stream/events", UriKind.Relative), timeout.Token);

        Assert.Contains("event: error", body, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"scope.violation\"", body, StringComparison.Ordinal);
    }
}
