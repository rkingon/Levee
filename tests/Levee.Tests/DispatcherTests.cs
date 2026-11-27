using System.Text.Json;
using Levee.Generated;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Levee.Tests;

public sealed class DispatcherTests
{
    private static readonly Member Ada = new("ada", "acme");
    private readonly Store _store = new();

    private readonly Outcomes _outcomes = new();

    internal static IServiceProvider Services(Store store, Outcomes outcomes, string greeter = "en", Action<IServiceCollection>? more = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new("Probe:Greeter", greeter)]).Build();
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<IOperationOutcome>(outcomes)
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>))
            .AddSingleton<IAccessPolicy, TestAccess>()
            .AddScoped<IUnitOfWorkFactory, FakeUnitOfWorkFactory>()
            .AddLeveeOperations(configuration);
        more?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private Dispatcher Build(Action<IServiceCollection>? more = null) => Services(_store, _outcomes, more: more).GetRequiredService<Dispatcher>();

    [Fact]
    public void The_generated_catalog_holds_every_operation_with_its_contract()
    {
        var remember = LeveeComposition.Catalog.Find("probe.remember")!;

        Assert.Equal(["auth.sign_in", "platform.reset", "probe.busy", "probe.cancel_self", "probe.computed", "probe.crash", "probe.crashing_ticks", "probe.echo", "probe.firehose", "probe.forever", "probe.leaky", "probe.maybe", "probe.mine", "probe.mine_stream", "probe.now", "probe.odd", "probe.parsed_scope", "probe.pay", "probe.quiet", "probe.reach", "probe.refuse", "probe.register", "probe.remember", "probe.sign_up", "probe.sneak", "probe.speak", "probe.strict", "probe.ticks", "probe.unserializable", "probe.validated", "probe.visits", "probe.wait", "probe.watched"],
            LeveeComposition.Catalog.All.Select(o => o.Name));
        Assert.Equal((Effects.Write, Idempotency.None, "member", false, false), (remember.Effects, remember.Idempotency, remember.Access, remember.ReadOnly, remember.Platform));
        Assert.True(LeveeComposition.Catalog.Find("platform.reset")!.Platform);
        Assert.Equal("Says it back.", LeveeComposition.Catalog.Find(typeof(Echo))!.Summary);
    }

    [Fact]
    public async Task A_read_runs_read_only_at_platform_scope_and_isnt_audited()
    {
        var result = await Build().SendAsync(new Echo("hi"), Ada, CancellationToken.None);

        Assert.Equal("hi", result.Value.Said);
        Assert.Equal(new WorkScope(null, ReadOnly: true), Assert.Single(_store.Begun));
        Assert.Empty(_store.Audited);
    }

    [Fact]
    public async Task A_write_runs_in_its_scope_and_is_audited_without_its_secrets()
    {
        await Build().SendAsync(new Remember("acme", "milk", "1234"), Ada, CancellationToken.None);

        Assert.Equal(new WorkScope("acme", ReadOnly: false), Assert.Single(_store.Begun));
        var audit = Assert.Single(_store.Audited);
        Assert.Equal(("acme", "ada", "probe.remember"), (audit.Scope, audit.Subject, audit.Operation));
        Assert.Equal("""{"tenant":"acme","text":"milk","pin":"[secret]"}""", audit.Input);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_scoped_operation_without_its_scope_is_refused_before_anything_runs(string tenant)
    {
        var result = await Build().SendAsync(new Remember(tenant, "milk", "1"), new Member("root", null, Admin: true), CancellationToken.None);

        Assert.Equal("scope.missing", result.Error!.Code);
        Assert.Empty(_store.Begun);
    }

    [Fact]
    public async Task A_refusal_commits_nothing()
    {
        var result = await Build().SendAsync(new Refuse(), Ada, CancellationToken.None);

        Assert.Equal("probe.refused", result.Error!.Code);
        Assert.Equal((0, 1), (_store.Commits, _store.Disposed));
        Assert.Empty(_store.Audited);
    }

    [Fact]
    public async Task A_denied_caller_never_opens_a_unit_of_work()
    {
        var other = await Build().SendAsync(new Remember("globex", "milk", "1"), Ada, CancellationToken.None);
        var platform = await Build().SendAsync(new Reset(), Ada, CancellationToken.None);

        Assert.Equal(("access.tenant", "access.platform"), (other.Error!.Code, platform.Error!.Code));
        Assert.Empty(_store.Begun);
    }

    [Fact]
    public async Task What_the_store_refuses_is_an_error_naming_the_operation()
    {
        var result = await Build().SendAsync(new Sneak(), Ada, CancellationToken.None);

        Assert.Equal((ErrorKind.Forbidden, LeveeErrors.UndeclaredWrite), (result.Error!.Kind, result.Error.Code));
        Assert.Contains("probe.sneak", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, _store.Commits);
    }

    [Fact]
    public async Task An_admission_refuses_inside_the_unit_of_work_before_the_handler()
    {
        var result = await Build(s => s.AddScoped<IAdmission, RefuseEverything>()).SendAsync(new Remember("acme", "x", "1"), Ada, CancellationToken.None);

        Assert.Equal("admission.closed", result.Error!.Code);
        Assert.Single(_store.Begun);
        Assert.Equal(0, _store.Commits);
    }

    [Fact]
    public async Task Json_in_and_out_is_strict()
    {
        var dispatcher = Build();
        var ok = await dispatcher.SendJsonAsync("probe.echo", Json("""{"text":"hi"}"""), Ada, CancellationToken.None);
        var unknownMember = await dispatcher.SendJsonAsync("probe.echo", Json("""{"text":"hi","extra":1}"""), Ada, CancellationToken.None);
        var missing = await dispatcher.SendJsonAsync("probe.echo", Json("{}"), Ada, CancellationToken.None);
        var unknownOperation = await dispatcher.SendJsonAsync("probe.nope", Json("{}"), Ada, CancellationToken.None);

        Assert.Equal("""{"said":"hi"}""", ok.Value.GetRawText());
        Assert.Equal(("input.invalid", "input.invalid", "operation.unknown"), (unknownMember.Error!.Code, missing.Error!.Code, unknownOperation.Error!.Code));
    }

    [Fact]
    public void The_apps_metadata_is_in_the_catalog()
    {
        var turn = LeveeComposition.Catalog.Find("probe.speak")!.Get<TurnAttribute>();

        Assert.Equal((Reach.Outside, true), (turn!.Reach, turn.Pinned));
        Assert.Null(LeveeComposition.Catalog.Find("probe.echo")!.Get<TurnAttribute>());
    }

    [Fact]
    public async Task An_internal_operation_runs_through_the_pipeline_in_process()
    {
        var result = await Build().SendAsync(new SignIn("grl_abc"), new SystemPrincipal("sign-in"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(LeveeComposition.Catalog.Find("auth.sign_in")!.Internal);
        Assert.Equal("""{"link":"[secret]"}""", Assert.Single(_store.Audited).Input);
    }

    [Fact]
    public async Task Every_outcome_is_told_after_the_unit_of_work_ends()
    {
        var dispatcher = Build();

        await dispatcher.SendAsync(new Echo("hi"), Ada, CancellationToken.None);
        await dispatcher.SendAsync(new Refuse(), Ada, CancellationToken.None);
        await dispatcher.SendAsync(new Remember("globex", "x", "1"), Ada, CancellationToken.None);

        Assert.Equal([("probe.echo", null), ("probe.refuse", "probe.refused"), ("probe.remember", "access.tenant")],
            _outcomes.Seen.Select(o => (o.Operation.Name, o.Error?.Code)));
        Assert.Equal(_store.Begun.Count, _store.Disposed);
    }

    [Fact]
    public void A_set_is_served_by_every_adapter()
    {
        using var scope = Services(_store, _outcomes).CreateScope();

        Assert.Equal(["discord", "slack"], scope.ServiceProvider.GetServices<IChannel>().Select(c => c.Name).Order());
    }

    [Fact]
    public void A_singleton_adapter_is_one_per_process()
    {
        var services = Services(_store, _outcomes);
        using var one = services.CreateScope();
        using var two = services.CreateScope();

        Assert.Equal(one.ServiceProvider.GetRequiredService<ISignals>().Id, two.ServiceProvider.GetRequiredService<ISignals>().Id);
    }

    [Theory]
    [InlineData("en", "hello", false)]
    [InlineData("fr", "bonjour", true)]
    public void A_setting_binds_its_adapter_for_as_long_as_that_adapter_lives(string setting, string greeting, bool shared)
    {
        var services = Services(_store, _outcomes, greeter: setting);
        using var one = services.CreateScope();
        using var two = services.CreateScope();
        var first = one.ServiceProvider.GetRequiredService<IGreeter>();

        Assert.Equal(greeting, first.Greet());
        Assert.Equal(shared, ReferenceEquals(first, two.ServiceProvider.GetRequiredService<IGreeter>()));
    }

    [Fact]
    public async Task A_result_may_be_nothing()
    {
        var dispatcher = Build();

        Assert.Null((await dispatcher.SendAsync(new Maybe(false), Ada, CancellationToken.None)).Value);
        Assert.Equal("null", (await dispatcher.SendJsonAsync("probe.maybe", Json("""{"some":false}"""), Ada, CancellationToken.None)).Value.GetRawText());
    }

    [Fact]
    public async Task Now_is_at_the_stores_precision()
    {
        var now = (await Build().SendAsync(new Now(), Ada, CancellationToken.None)).Value;

        Assert.Equal(0, now.Ticks % TimeSpan.TicksPerMicrosecond);
    }

    [Fact]
    public async Task A_subscription_streams_after_one_authorization_and_holds_no_unit_of_work()
    {
        var dispatcher = Build();

        var opened = await dispatcher.SubscribeAsync(new Ticks(3), Ada, CancellationToken.None);
        var ticks = new List<int>();
        await foreach (var tick in opened.Value)
        {
            ticks.Add(tick.Number);
        }
        var denied = await dispatcher.SubscribeAsync(new Ticks(1), new Stranger("eve"), CancellationToken.None);
        var sent = await dispatcher.SendJsonAsync("probe.ticks", Json("""{"count":1}"""), Ada, CancellationToken.None);

        Assert.Equal([1, 2, 3], ticks);
        Assert.Empty(_store.Begun);
        Assert.Equal(("access.unknown", "operation.streams"), (denied.Error!.Code, sent.Error!.Code));
    }

    [Fact]
    public async Task A_client_never_reaches_an_internal_operation()
    {
        var dispatcher = Build();

        var client = await dispatcher.SendFromClientAsync("auth.sign_in", Json("""{"link":"x"}"""), new SystemPrincipal("api"), CancellationToken.None);
        var inProcess = await dispatcher.SendJsonAsync("auth.sign_in", Json("""{"link":"x"}"""), new SystemPrincipal("api"), CancellationToken.None);

        Assert.Equal("operation.unknown", client.Error!.Code);
        Assert.True(inProcess.IsSuccess);
    }

    [Fact]
    public async Task An_outcome_sees_the_input_as_the_audit_does()
    {
        await Build().SendAsync(new Remember("acme", "milk", "1234"), Ada, CancellationToken.None);

        var outcome = Assert.Single(_outcomes.Seen);
        Assert.Equal(("""{"tenant":"acme","text":"milk","pin":"[secret]"}""", "acme"), (outcome.Input, outcome.Scope!.Key));
    }

    [Fact]
    public void A_routed_set_gives_each_adapter_by_name_and_makes_only_that_one()
    {
        using var scope = Services(_store, _outcomes).CreateScope();
        var launchers = scope.ServiceProvider.GetRequiredService<IAdapters<ILauncher>>();

        Assert.Equal(["docker", "ecs"], launchers.Names.Order());
        Assert.Equal("ecs", launchers.Get("ecs").Name);
        Assert.Throws<KeyNotFoundException>(() => launchers.Get("lambda"));
        Assert.Equal(2, scope.ServiceProvider.GetServices<ILauncher>().Count());
    }

    [Fact]
    public void An_adapter_serving_several_ports_is_one_instance()
    {
        using var scope = Services(_store, _outcomes).CreateScope();

        Assert.Equal(scope.ServiceProvider.GetRequiredService<IListening>().Id, scope.ServiceProvider.GetRequiredService<ITyping>().Id);
    }

    [Fact]
    public void The_catalog_is_every_part_and_a_name_in_two_fails_at_startup()
    {
        var services = Services(_store, _outcomes, more: s => s.AddLeveePart(LeveeComposition.Catalog.All.Where(o => o.Name == "probe.echo")));

        var refused = Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<Dispatcher>());
        Assert.Contains("probe.echo", refused.Message, StringComparison.Ordinal);
        Assert.Equal(LeveeComposition.Catalog.All.Count, Services(_store, _outcomes).GetRequiredService<OperationCatalog>().All.Count);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
}
