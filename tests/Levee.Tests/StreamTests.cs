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
}
