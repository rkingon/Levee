using Levee.EntityFrameworkCore;
using Levee.Generated;
using Levee.Oxbow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Oxbow;
using Sample.Adapters;

namespace Levee.IntegrationTests;

public sealed class Alpha2FourthReviewTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost Build(NpgsqlDataSource pool, IJobPrincipals? restored = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddSingleton(pool);
        builder.Services.AddDbContext<SampleDbContext>(o => o.UseNpgsql(pool));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<SampleDbContext>();
        builder.Services.AddLeveeOxbow(o =>
        {
            if (restored is not null)
            {
                o.Queues["default"] = new QueueOptions { MaxWorkers = 2 };
            }
        }).AddLeveeJobs();
        builder.Services.AddSingleton<IAccessPolicy, TenantAccess>();
        if (restored is not null)
        {
            builder.Services.AddSingleton(restored);
        }
        return builder.Build();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_disposal_settles_a_pending_or_open_watch(bool holdConnection)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timer.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timer.Token;
        var settings = new NpgsqlConnectionStringBuilder(postgres.App) { MaxPoolSize = 1 };
        await using var pool = NpgsqlDataSource.Create(settings.ConnectionString);
        var host = Build(pool);
        try
        {
            var dispatcher = host.Services.GetRequiredService<Dispatcher>();
            var subscription = await dispatcher.SubscribeAsync(new ObserveReview(), new SystemPrincipal("review"), ct);
            await using var watch = subscription.Value.GetAsyncEnumerator(ct);
            await using var held = holdConnection ? await pool.OpenConnectionAsync(ct) : null;
            if (!holdConnection)
            {
                Assert.True(await watch.MoveNextAsync());
                Assert.Equal("ready", watch.Current.Kind);
            }
            var pending = watch.MoveNextAsync().AsTask();
            Assert.False(pending.IsCompleted);
            await ((IAsyncDisposable)host).DisposeAsync();
            var settled = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1), ct)) == pending;
            if (!settled)
            {
                await timer.CancelAsync();
            }
            bool advanced = false;
            var failure = await Record.ExceptionAsync(async () => { advanced = await pending; });
            Assert.True(settled);
            Assert.False(advanced);
            if (holdConnection && failure is not null)
            {
                Assert.IsType<ObjectDisposedException>(failure.GetBaseException());
            }
            else
            {
                Assert.Null(failure);
            }
        }
        finally
        {
            await ((IAsyncDisposable)host).DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_installations_keep_their_own_audit_reads_in_either_grant_order(bool reverse)
    {
        var suffix = reverse ? "r" : "f";
        var first = "review_first_" + suffix;
        var second = "review_second_" + suffix;
        foreach (var name in new[] { first, second })
        {
            await postgres.ExecuteAsync($"create schema {name}; create role {name} login password 'audit';" +
                LeveeSchema.Create(name) + $"create table {name}.plans(id int);" + LeveeSchema.Shared($"{name}.plans", name));
        }
        foreach (var name in reverse ? new[] { second, first } : new[] { first, second })
        {
            await postgres.ExecuteAsync(LeveeSchema.Grants(name, name));
        }
        foreach (var (own, other) in new[] { (first, second), (second, first) })
        {
            var login = new NpgsqlConnectionStringBuilder(postgres.App) { Username = own, Password = "audit" };
            await using var app = NpgsqlDataSource.Create(login.ConnectionString);
            await using var query = app.CreateCommand("""
                select s.nspname, c.relname, has_table_privilege(c.oid, 'select'), has_table_privilege(c.oid, 'insert')
                from pg_class c join pg_namespace s on c.relnamespace = s.oid
                where s.nspname in ($1, $2) and c.relname in ('plans','levee_audit')
                """);
            query.Parameters.AddWithValue(own);
            query.Parameters.AddWithValue(other);
            var count = 0;
            await using var rows = await query.ExecuteReaderAsync(Ct);
            while (await rows.ReadAsync(Ct))
            {
                var ours = rows.GetString(0) == own;
                Assert.Equal(ours, rows.GetBoolean(2));
                Assert.Equal(ours && rows.GetString(1) == "plans", rows.GetBoolean(3));
                count++;
            }
            Assert.Equal(4, count);
        }
    }

    [Fact]
    public async Task Reset_reconciles_database_wide_relation_defaults()
    {
        await postgres.ExecuteAsync("create schema review_defaults; create role review_app; " +
            "alter default privileges grant select on tables to review_app;" +
            LeveeSchema.Create("review_defaults") + LeveeSchema.Grants("review_app", "review_defaults"));
        await using var app = NpgsqlDataSource.Create(postgres.App);
        await using var sql = app.CreateCommand("""
            select exists(select from pg_default_acl a cross join lateral aclexplode(a.defaclacl) g
              where g.grantee = 'review_app'::regrole and a.defaclobjtype = 'r'
                and a.defaclnamespace = 0 and g.privilege_type = 'SELECT')
            """);
        Assert.Equal(false, await sql.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task A_schema_owner_can_bootstrap_beside_an_independently_owned_store()
    {
        await postgres.ExecuteAsync("create role review_owner login password 'review'; create role review_runtime; " +
            "create schema review_product authorization review_owner; create schema review_directory;");
        var login = new NpgsqlConnectionStringBuilder(postgres.App) { Username = "review_owner", Password = "review" };
        await using var owner = NpgsqlDataSource.Create(login.ConnectionString);
        await using var install = owner.CreateCommand(LeveeSchema.Create("review_product"));
        await install.ExecuteNonQueryAsync(Ct);
        await using var grant = owner.CreateCommand(LeveeSchema.Grants("review_runtime", "review_product"));
        await grant.ExecuteNonQueryAsync(Ct);
    }

    private sealed class CachedIdentity(Principal principal) : IJobPrincipals
    {
        public ValueTask<Principal> RestoreAsync(JobPrincipal job, CancellationToken ct) => ValueTask.FromResult(principal);
    }

    [Fact]
    public async Task A_completed_tenant_process_leaves_its_hosts_service_identity_usable()
    {
        await using var pool = NpgsqlDataSource.Create(postgres.App);
        var identity = new SystemPrincipal("review-cron");
        using var host = Build(pool, new CachedIdentity(identity));
        await host.StartAsync(Ct);
        try
        {
            var dispatcher = host.Services.GetRequiredService<Dispatcher>();
            Assert.True((await dispatcher.SendAsync(new ReviewPing(), identity, Ct)).IsSuccess);
            var start = await dispatcher.SendAsync(new StartNoteProcess("review-tenant", "review", false), identity, Ct);
            Assert.True(start.IsSuccess, start.Error?.Message);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timer.CancelAfter(TimeSpan.FromSeconds(15));
            await using var job = pool.CreateCommand("select state::text from oxbow_job where kind = 'levee_process' and args->>'scope' = 'review-tenant'");
            while (!Equals(await job.ExecuteScalarAsync(timer.Token), "completed"))
            {
                await Task.Delay(50, timer.Token);
            }
            var after = await dispatcher.SendAsync(new ReviewPing(), identity, Ct);
            Assert.True(after.IsSuccess, after.Error?.Code + " " + after.Error?.Message);
        }
        finally
        {
            await host.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task An_unscoped_write_commits_its_audit_with_an_ordinary_function_owner()
    {
        await postgres.ExecuteAsync("create role review_audit_owner; grant usage on schema public to review_audit_owner;" +
            "alter table public.levee_audit owner to review_audit_owner;" +
            "alter function public.levee_audit_write(uuid,text,text,text,jsonb) owner to review_audit_owner;");
        try
        {
            await using var pool = NpgsqlDataSource.Create(postgres.App);
            using var host = Build(pool);
            var dispatcher = host.Services.GetRequiredService<Dispatcher>();
            var identity = new SystemPrincipal("review-writer");
            var control = await dispatcher.SendAsync(new EmitPlatformReview(), identity, Ct);
            Assert.True(control.IsSuccess, control.Error?.Message);
            var written = await dispatcher.SendAsync(new EmitUnscopedReview(), identity, Ct);
            Assert.True(written.IsSuccess, written.Error?.Code + " " + written.Error?.Message);
        }
        finally
        {
            await postgres.ExecuteAsync("alter table public.levee_audit owner to current_user; " +
                "alter function public.levee_audit_write(uuid,text,text,text,jsonb) owner to current_user;");
        }
    }

    [Fact]
    public async Task A_platform_observer_can_distinguish_a_platform_signal_from_an_unscoped_one()
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timer.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timer.Token;
        await using var pool = NpgsqlDataSource.Create(postgres.App);
        using var host = Build(pool);
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var identity = new SystemPrincipal("review-observer");
        await using var watch = (await dispatcher.SubscribeAsync(new ObserveReview(), identity, ct)).Value.GetAsyncEnumerator(ct);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("ready", watch.Current.Kind);
        Assert.True((await dispatcher.SendAsync(new EmitPlatformReview(), identity, ct)).IsSuccess);
        Assert.True(await watch.MoveNextAsync());
        var platform = watch.Current;
        Assert.True((await dispatcher.SendAsync(new EmitUnscopedReview(), identity, ct)).IsSuccess);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal(new ChangeSignal("review.changed", "version", "review-tenant"), platform);
        Assert.Equal(new ChangeSignal("review.changed", "version", null), watch.Current);
    }
    [Fact]
    public async Task Overlapping_runs_with_one_restored_identity_complete_in_their_own_scopes()
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timer.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = timer.Token;
        var first = $"first-{Guid.NewGuid():N}";
        var second = $"second-{Guid.NewGuid():N}";
        var identity = new SystemPrincipal("review-shared");
        await using var pool = NpgsqlDataSource.Create(postgres.App);
        using var host = Build(pool, new CachedIdentity(identity));
        await host.StartAsync(ct);
        try
        {
            var dispatcher = host.Services.GetRequiredService<Dispatcher>();
            Assert.True((await dispatcher.SendAsync(new StartHandedProcess(first, first, Hold: true), identity, ct)).IsSuccess);
            // The gate is installed inside RunAsync, after restoration and confinement.
            while (!HandedProcess.Gates.ContainsKey(first))
            {
                await Task.Delay(25, ct);
            }
            Assert.True((await dispatcher.SendAsync(new StartHandedProcess(second, second), identity, ct)).IsSuccess);
            await using var job = pool.CreateCommand("select state::text from oxbow_job where kind = 'levee_process' and args->>'scope' = $1");
            var tenant = job.Parameters.AddWithValue(second);
            foreach (var scope in new[] { second, first })
            {
                if (scope == first)
                {
                    HandedProcess.Gates[first].TrySetResult();
                }
                tenant.Value = scope;
                string? state;
                while ((state = await job.ExecuteScalarAsync(ct) as string) is not ("discarded" or "completed" or "cancelled"))
                {
                    await Task.Delay(25, ct);
                }
                Assert.Equal("completed", state);
                Assert.Equal(1, await postgres.CountAsync("select count(*) from notes where tenant = $1 and text = 'handed'", scope));
            }
        }
        finally
        {
            HandedProcess.Gates.GetOrAdd(first, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            await host.StopAsync(Ct);
            HandedProcess.Gates.TryRemove(first, out _);
        }
    }

}

[Operation("review.ping", "platform", Platform = true)]
public sealed record ReviewPing : IOperation<string>;

internal sealed class ReviewPingHandler : IOperationHandler<ReviewPing, string>
{
    public Task<Result<string>> HandleAsync(ReviewPing operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<string>>("ok");
}

[Operation("review.emit_platform", "platform", Platform = true, Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record EmitPlatformReview : IOperation<Unit>;

[Operation("review.emit_unscoped", "platform", Unscoped = true, Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record EmitUnscopedReview : IOperation<Unit>;

internal sealed class EmitPlatformReviewHandler(IChangeSignals signals) : IOperationHandler<EmitPlatformReview, Unit>
{
    public async Task<Result<Unit>> HandleAsync(EmitPlatformReview operation, OperationContext context, CancellationToken ct)
    {
        await signals.RaiseAsync("review.changed", "version", "review-tenant", ct);
        return Unit.Value;
    }
}

internal sealed class EmitUnscopedReviewHandler(IChangeSignals signals) : IOperationHandler<EmitUnscopedReview, Unit>
{
    public async Task<Result<Unit>> HandleAsync(EmitUnscopedReview operation, OperationContext context, CancellationToken ct)
    {
        await signals.RaiseAsync("review.changed", "version", ct);
        return Unit.Value;
    }
}

[Operation("review.observe", "platform", Platform = true)]
public sealed record ObserveReview : ISubscription<ChangeSignal>;

internal sealed class ObserveReviewHandler(IChangeSignals signals) : ISubscriptionHandler<ObserveReview, ChangeSignal>
{
    public async IAsyncEnumerable<ChangeSignal> SubscribeAsync(ObserveReview subscription, SubscriptionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var watch = await signals.WatchAsync("review.changed", ct);
        yield return new ChangeSignal("ready", null, null);
        await foreach (var signal in watch.WithCancellation(ct))
        {
            yield return signal;
        }
    }
}
