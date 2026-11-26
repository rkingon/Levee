using System.Text.Json;
using Levee.Generated;
using Levee.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Levee.Tests;

public sealed class McpTests
{
    private static readonly Member Ada = new("ada", "acme", Admin: true);

    private static (Func<string, JsonElement, CancellationToken, Task<Result<JsonElement>>> Send, List<OperationTool> Tools) Build(
        LeveeMcpOptions? mcp = null, Principal? caller = null)
    {
        var services = DispatcherTests.Services(new Store(), new Outcomes());
        var dispatcher = services.GetRequiredService<Dispatcher>();
        var tools = OperationTool.For(services.GetRequiredService<OperationCatalog>(), services.GetRequiredService<LeveeOptions>(),
            mcp ?? new LeveeMcpOptions { Principal = _ => ValueTask.FromResult<Principal?>(Ada) });
        return ((name, input, ct) => dispatcher.SendFromClientAsync(name, input, caller ?? Ada, ct), tools);
    }

    [Fact]
    public void A_tool_per_operation_a_client_may_call_named_by_a_fixed_rule()
    {
        var (_, tools) = Build();
        var names = tools.Select(t => t.ProtocolTool.Name).ToList();
        var remember = tools.Single(t => t.ProtocolTool.Name == "probe_remember").ProtocolTool;

        Assert.Contains("probe_echo", names);
        Assert.DoesNotContain("auth_sign_in", names);
        Assert.DoesNotContain("probe_ticks", names);
        Assert.Equal((false, false, true), (remember.Annotations!.ReadOnlyHint, remember.Annotations.IdempotentHint, remember.Annotations.DestructiveHint));
        Assert.True(remember.InputSchema.GetProperty("properties").GetProperty("pin").GetProperty("writeOnly").GetBoolean());
    }

    [Fact]
    public async Task A_call_goes_through_the_dispatcher_and_answers_its_result_or_its_problem()
    {
        var (send, tools) = Build();
        var echo = tools.Single(t => t.ProtocolTool.Name == "probe_echo");
        var refuse = tools.Single(t => t.ProtocolTool.Name == "probe_refuse");

        var said = await echo.CallAsync(send, new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("hi") }, null, CancellationToken.None);
        var refused = await refuse.CallAsync(send, null, null, CancellationToken.None);
        var anonymous = await echo.CallAsync((_, _, _) => Task.FromResult<Result<JsonElement>>(Error.Unauthenticated("Sign in first.")), null, null,
            CancellationToken.None);

        Assert.Equal("""{"said":"hi"}""", Assert.IsType<TextContentBlock>(Assert.Single(said.Content)).Text);
        Assert.NotEqual(true, said.IsError);
        Assert.True(refused.IsError);
        Assert.Contains("\"code\":\"probe.refused\"", Assert.IsType<TextContentBlock>(Assert.Single(refused.Content)).Text, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"unauthenticated\"", Assert.IsType<TextContentBlock>(Assert.Single(anonymous.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_operations_one_tool_name_fail_at_startup()
    {
        var catalog = new OperationCatalog(
        [
            OperationDescriptor.Create<Echo, EchoView>("a.b_c", "", "member", Effects.Read, Idempotency.Safe, platform: true),
            OperationDescriptor.Create<Crash, Unit>("a_b.c", "", "member", Effects.Read, Idempotency.Safe, platform: true),
        ]);

        var clash = Assert.Throws<InvalidOperationException>(() => OperationTool.For(catalog, new LeveeOptions(), new LeveeMcpOptions()));

        Assert.Contains("a_b_c", clash.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_server_offers_the_catalogs_tools()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Store())
            .AddSingleton<IAccessPolicy, TestAccess>()
            .AddScoped<IUnitOfWorkFactory, FakeUnitOfWorkFactory>()
            .AddLeveeOperations(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection([new("Probe:Greeter", "en")]).Build());
        services.AddLogging();
        services.AddMcpServer().WithLeveeOperations(_ => ValueTask.FromResult<Principal?>(Ada), d => d.Name.StartsWith("probe.", StringComparison.Ordinal));
        using var provider = services.BuildServiceProvider();

        var tools = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;

        Assert.Contains(tools, t => t.ProtocolTool.Name == "probe_echo");
        Assert.DoesNotContain(tools, t => t.ProtocolTool.Name == "platform_reset");
    }

    [Fact]
    public async Task A_bound_input_is_the_servers_not_the_models()
    {
        var (send, tools) = Build(new LeveeMcpOptions { BoundInputs = ["text"], Bind = (_, _) => ValueTask.FromResult<IReadOnlyDictionary<string, JsonElement>>(new Dictionary<string, JsonElement>()) });
        var echo = tools.Single(t => t.ProtocolTool.Name == "probe_echo");

        var said = await echo.CallAsync(send, new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("from the model") },
            new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("from the server") }, CancellationToken.None);

        Assert.False(echo.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("text", out _));
        Assert.Equal("""{"said":"from the server"}""", Assert.IsType<TextContentBlock>(Assert.Single(said.Content)).Text);
    }

    [Fact]
    public async Task A_result_can_be_rendered_as_other_content()
    {
        var (send, tools) = Build(new LeveeMcpOptions
        {
            Render = (d, result) => d.Name == "probe.echo" ? [new ImageContentBlock { Data = System.Text.Encoding.UTF8.GetBytes(result.GetProperty("said").GetString()!), MimeType = "image/png" }] : null,
        });

        var shown = await tools.Single(t => t.ProtocolTool.Name == "probe_echo")
            .CallAsync(send, new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("aGk=") }, null, CancellationToken.None);

        Assert.Equal("image/png", Assert.IsType<ImageContentBlock>(Assert.Single(shown.Content)).MimeType);
    }

    [Fact]
    public void A_server_with_no_dispatcher_projects_a_catalog_it_sends_over_the_wire()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer().WithLeveeOperations(o =>
        {
            o.Catalog = LeveeComposition.Catalog;
            o.Send = (_, name, _, _) => Task.FromResult<Result<JsonElement>>(JsonSerializer.SerializeToElement(new { sent = name }));
        });
        using var provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!, t => t.ProtocolTool.Name == "probe_echo");
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMcpServer().WithLeveeOperations(_ => { }));
    }
}
