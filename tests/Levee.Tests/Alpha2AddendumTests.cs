using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Levee.AspNetCore;
using Levee.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Levee.Tests;

public sealed class Alpha2AddendumTests
{
    private sealed class Allow : IAccessPolicy
    {
        public ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct) => ValueTask.FromResult<Error?>(null);
    }

    [Fact]
    public async Task Anonymous_platform_calls_and_streams_return_401_before_a_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Probe:Greeter"] = "en";
        var store = new Store();
        builder.Services.AddSingleton(store).AddSingleton<IAccessPolicy, Allow>()
            .AddScoped<IUnitOfWorkFactory, FakeUnitOfWorkFactory>().AddLeveeOperations(builder.Configuration);
        await using var app = builder.Build();
        app.MapLeveeOperations(http => http.Request.Headers.ContainsKey("Authorization")
            ? new SystemPrincipal("operator") : (Principal)new AnonymousPrincipal());
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        using var call = await client.PostAsJsonAsync("/ops/probe.echo", new { text = "ok" }, ct);
        using var stream = await client.GetAsync("/ops/probe.ticks/events?input=%7B%22count%22%3A1%7D", ct);
        foreach (var response in new[] { call, stream })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal(LeveeErrors.Unauthenticated, error.RootElement.GetProperty("code").GetString());
        }
        Assert.Empty(store.Begun);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "review");
        using var control = await client.PostAsJsonAsync("/ops/probe.echo", new { text = "ok" }, ct);
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
    }
}
