using Levee.EntityFrameworkCore;
using Levee.Generated;
using Levee.Oxbow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Sample.Adapters;
using Sample.Contracts;

namespace Levee.IntegrationTests;

public sealed class IdempotencyKeyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost Build(string bannedWords = "none", TimeSpan? slowSave = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = bannedWords;
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.App));
        builder.Services.AddDbContext<SampleDbContext>((sp, o) =>
        {
            o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>());
            if (slowSave is { } takes)
            {
                o.AddInterceptors(new SlowSave(takes));
            }
        });
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<SampleDbContext>();
        builder.Services.AddLeveeOxbow();
        builder.Services.AddSingleton<IAccessPolicy, TenantAccess>();
        return builder.Build();
    }

    private static (string Tenant, Member Member) NewTenant()
    {
        var tenant = $"t{Guid.CreateVersion7():N}";
        return (tenant, new Member($"ada@{tenant}", tenant));
    }

    private Task<long> Notes(string tenant) => postgres.CountAsync("select count(*) from notes where tenant = $1", tenant);

    private Task<long> Audited(string tenant) =>
        postgres.CountAsync("select count(*) from levee_audit where scope = $1 and operation = 'notes.create'", tenant);

    [Fact]
    public async Task Equal_requests_at_once_make_one_write_and_both_answer_it()
    {
        using var host = Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var create = new CreateNote(Guid.CreateVersion7(), tenant, "once");

        var sent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => dispatcher.SendAsync(create, ada, "retry-1", Ct)));

        Assert.All(sent, s => Assert.Equal(sent[0].Value, s.Value));
        Assert.Equal((1, 1), (await Notes(tenant), await Audited(tenant)));
    }

    [Fact]
    public async Task A_key_is_kept_only_once_its_write_commits()
    {
        var (tenant, ada) = NewTenant();
        var create = new CreateNote(Guid.CreateVersion7(), tenant, "spam, then not");
        using (var strict = Build(bannedWords: "list"))
        {
            Assert.Equal("note.banned", (await strict.Services.GetRequiredService<Dispatcher>().SendAsync(create, ada, "retry-1", Ct)).Error!.Code);
        }
        using var host = Build();

        var retried = await host.Services.GetRequiredService<Dispatcher>().SendAsync(create, ada, "retry-1", Ct);

        Assert.True(retried.IsSuccess);
        Assert.Equal(1, await Notes(tenant));
    }

    [Fact]
    public async Task A_key_reused_with_other_input_is_refused_and_writes_nothing()
    {
        using var host = Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        await dispatcher.SendAsync(new CreateNote(Guid.CreateVersion7(), tenant, "first"), ada, "retry-1", Ct);

        var reused = await dispatcher.SendAsync(new CreateNote(Guid.CreateVersion7(), tenant, "second"), ada, "retry-1", Ct);

        Assert.Equal(LeveeErrors.IdempotencyMismatch, reused.Error!.Code);
        Assert.Equal(1, await Notes(tenant));
    }

    [Fact]
    public async Task A_key_past_its_lifetime_is_a_new_request()
    {
        using var host = Build();
        host.Services.GetRequiredService<LeveeOptions>().IdempotencyKeyLifetime = TimeSpan.Zero;
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var create = new CreateNote(Guid.CreateVersion7(), tenant, "once");
        Assert.True((await dispatcher.SendAsync(create, ada, "retry-1", Ct)).IsSuccess);

        var later = await dispatcher.SendAsync(create, ada, "retry-1", Ct);

        // Run again, so the note's own id refuses it.
        Assert.Equal(LeveeErrors.Conflict, later.Error!.Code);
        await postgres.ExecuteAsync(LeveeSchema.SweepExpiredKeys());
        Assert.Equal(0, await postgres.CountAsync("select count(*) from levee_idempotency where expires_at <= now()"));
    }

    [Fact]
    public async Task A_fresh_install_with_both_migrations_creates_the_keys_once()
    {
        // An app's alpha.2 migration (AddLeveeSchema, now with alpha.3's objects) and its alpha.3 one (UpgradeLeveeSchema), on an empty database.
        await postgres.ExecuteAsync($"""
            create schema fresh_keys;
            {LeveeSchema.Create("fresh_keys")}
            {LeveeSchema.Upgrade("fresh_keys")}
            """);

        Assert.Equal(1, await postgres.CountAsync("select count(*) from pg_policy where polrelid = 'fresh_keys.levee_idempotency'::regclass"));
    }

    [Fact]
    public async Task A_keyed_write_whose_result_carries_a_secret_commits_and_is_never_shown_again()
    {
        using var host = Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();

        var issued = await dispatcher.SendAsync(new IssueKey(tenant), ada, "retry-1", Ct);
        var again = await dispatcher.SendAsync(new IssueKey(tenant), ada, "retry-1", Ct);

        Assert.True(issued.IsSuccess, issued.Error?.Message);
        Assert.Equal(LeveeErrors.IdempotencyReplayed, again.Error!.Code);
        Assert.Equal(0, await postgres.CountAsync("select count(*) from levee_idempotency where scope = $1 and result is not null", tenant));
        Assert.Equal(1, await postgres.CountAsync("select count(*) from levee_audit where scope = $1 and operation = 'probe.issue_key'", tenant));
    }

    [Fact]
    public async Task A_result_the_wire_writes_is_kept_as_it_was_written()
    {
        using var host = Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();

        // jsonb refuses \u0000; the wire writes it.
        var first = await dispatcher.SendAsync(new SayNul(tenant), ada, "retry-1", Ct);
        var again = await dispatcher.SendAsync(new SayNul(tenant), ada, "retry-1", Ct);

        Assert.Equal("a\u0000b", first.Value);
        Assert.Equal(first.Value, again.Value);
    }

    [Theory]
    [InlineData(800, 0)]
    [InlineData(0, 800)]
    public async Task A_keys_lifetime_counts_from_its_commit_not_its_start(int handler, int save)
    {
        using var host = Build(slowSave: TimeSpan.FromMilliseconds(save));
        host.Services.GetRequiredService<LeveeOptions>().IdempotencyKeyLifetime = TimeSpan.FromMilliseconds(500);
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();

        // Slow before its handler returns, or after: either way the key holds from its commit.
        Assert.True((await dispatcher.SendAsync(new SlowIssue(tenant, TimeSpan.FromMilliseconds(handler)), ada, "retry-1", Ct)).IsSuccess);
        var again = await dispatcher.SendAsync(new SlowIssue(tenant, TimeSpan.FromMilliseconds(handler)), ada, "retry-1", Ct);

        Assert.True(again.IsSuccess);
        Assert.Equal(1, await postgres.CountAsync("select count(*) from levee_audit where scope = $1 and operation = 'probe.slow_issue'", tenant));
    }

    /// <summary>A save that takes its time: the last writes before the commit, after the handler has returned.</summary>
    private sealed class SlowSave(TimeSpan takes) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(takes, cancellationToken);
            return result;
        }
    }

    [Fact]
    public async Task An_installation_from_before_keys_gets_them_from_its_owner()
    {
        await postgres.ExecuteAsync($"""
            create role legacy_app login password 'legacy' nosuperuser nobypassrls;
            create schema legacy_keys;
            {LeveeSchema.Create("legacy_keys")}
            drop function legacy_keys.levee_idempotency_claim(text, text, text, bytea);
            drop function legacy_keys.levee_idempotency_complete(text, text, text, text, interval);
            drop table legacy_keys.levee_idempotency;
            {LeveeSchema.Grants("legacy_app", "legacy_keys")}
            """);

        await postgres.ExecuteAsync(LeveeSchema.Upgrade("legacy_keys"));

        Assert.Equal("True", await postgres.TextAsync(
            "select has_function_privilege('legacy_app', 'legacy_keys.levee_idempotency_claim(text, text, text, bytea)', 'execute')"));
        Assert.Equal("False", await postgres.TextAsync("select has_table_privilege('legacy_app', 'legacy_keys.levee_idempotency', 'select')"));
    }
}

public sealed record IssuedKey([property: Secret] string Token);

[Operation("probe.issue_key", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record IssueKey(string Tenant) : IOperation<IssuedKey>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class IssueKeyHandler : IOperationHandler<IssueKey, IssuedKey>
{
    public Task<Result<IssuedKey>> HandleAsync(IssueKey operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<IssuedKey>>(new IssuedKey($"key-{context.NewId():N}"));
}

/// <summary>A write that takes its time before it commits.</summary>
[Operation("probe.slow_issue", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record SlowIssue(string Tenant, TimeSpan Takes) : IOperation<int>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class SlowIssueHandler : IOperationHandler<SlowIssue, int>
{
    public async Task<Result<int>> HandleAsync(SlowIssue operation, OperationContext context, CancellationToken ct)
    {
        await Task.Delay(operation.Takes, ct);
        return 42;
    }
}

[Operation("probe.say_nul", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record SayNul(string Tenant) : IOperation<string>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class SayNulHandler : IOperationHandler<SayNul, string>
{
    public Task<Result<string>> HandleAsync(SayNul operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Result<string>>("a\u0000b");
}
