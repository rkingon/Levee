using Levee.EntityFrameworkCore;
using Levee.Generated;
using Levee.Oxbow;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Oxbow;
using Sample.Adapters;
using Sample.Contracts;
using Sample.Domain;

namespace Levee.IntegrationTests;

public sealed class PipelineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost Build(bool work = false, string? bannedWords = "list", bool seal = false, ILoggerProvider? logs = null)
    {
        var builder = Host.CreateApplicationBuilder();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }
        builder.Configuration["Sample:BannedWords"] = bannedWords;
        var services = builder.Services;
        services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.App));
        services.AddDbContext<SampleDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddLeveeOperations(builder.Configuration);
        services.AddLeveePostgres<SampleDbContext>();
        services.AddDataProtection();
        services.AddLeveeOxbow(o =>
            {
                if (work)
                {
                    o.Queues["default"] = new QueueOptions { MaxWorkers = 2 };
                }
            })
            .AddLeveeJobs(sealSecrets: seal)
            .AddWorker<Ping, PingWorker>()
            .AddArgsEncryption();
        services.AddSingleton<IAccessPolicy, TenantAccess>();
        services.AddScoped<IJobPrincipals, RestoreMembers>();
        return builder.Build();
    }

    private static (string Tenant, Member Member) NewTenant()
    {
        var tenant = $"t{Guid.NewGuid():N}";
        return (tenant, new Member($"ada@{tenant}", tenant));
    }

    [Fact]
    public async Task A_write_commits_with_its_audit_and_reads_see_only_their_scope()
    {
        using var host = Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (acme, ada) = NewTenant();
        var (globex, bob) = NewTenant();

        var created = await dispatcher.SendAsync(new CreateNote(Guid.CreateVersion7(), acme, " milk "), ada, Ct);
        var mine = await dispatcher.SendAsync(new ListNotes(acme), ada, Ct);
        var theirs = await dispatcher.SendAsync(new ListNotes(globex), bob, Ct);

        Assert.Equal("milk", created.Value.Text);
        Assert.Equal([created.Value.Id], mine.Value.Select(n => n.Id));
        Assert.Empty(theirs.Value);
        Assert.Equal(1, await postgres.CountAsync("select count(*) from levee_audit where scope = $1 and operation = 'notes.create'", acme));
    }

    [Theory]
    [InlineData("list", "note.banned")]
    [InlineData("none", null)]
    public async Task A_setting_chooses_which_adapter_serves_a_port(string bannedWords, string? refusal)
    {
        using var host = Build(bannedWords: bannedWords);
        var (acme, ada) = NewTenant();

        var result = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new CreateNote(Guid.CreateVersion7(), acme, "buy spam"), ada, Ct);

        Assert.Equal(refusal, result.Error?.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("shouty")]
    public void A_port_without_a_valid_setting_fails_at_startup(string? bannedWords)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Build(bannedWords: bannedWords));

        Assert.Contains("Sample:BannedWords chooses the adapter for Sample.Application.IBannedWords: one of list, none", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_denied_caller_changes_nothing()
    {
        using var host = Build();
        var (acme, _) = NewTenant();
        var (_, bob) = NewTenant();

        var result = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new CreateNote(Guid.CreateVersion7(), acme, "milk"), bob, Ct);

        Assert.Equal("access.tenant", result.Error!.Code);
        Assert.Equal(0, await postgres.CountAsync("select count(*) from levee_audit where scope = $1", acme));
    }

    [Fact]
    public async Task A_read_that_writes_fails_at_the_database()
    {
        using var host = Build();
        var (acme, ada) = NewTenant();

        var result = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new Sneak(acme), ada, Ct);

        Assert.Equal(LeveeErrors.UndeclaredWrite, result.Error!.Code);
        Assert.Equal(0, await postgres.CountAsync("select count(*) from notes where tenant = $1", acme));
    }

    [Fact]
    public async Task A_write_outside_its_scope_fails_at_the_database()
    {
        using var host = Build();
        var (acme, ada) = NewTenant();
        var (globex, _) = NewTenant();

        var result = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new Trespass(acme, globex), ada, Ct);

        Assert.Equal(LeveeErrors.ScopeViolation, result.Error!.Code);
        Assert.Equal(0, await postgres.CountAsync("select count(*) from notes where tenant = $1", globex));
    }

    [Fact]
    public async Task The_audit_is_append_only_even_to_its_owner()
    {
        using var host = Build();
        var (acme, ada) = NewTenant();
        await host.Services.GetRequiredService<Dispatcher>().SendAsync(new CreateNote(Guid.CreateVersion7(), acme, "milk"), ada, Ct);

        await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync($"delete from levee_audit where scope = '{acme}'"));
    }

    [Fact]
    public async Task A_job_commits_or_rolls_back_with_its_operation()
    {
        using var host = Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (acme, ada) = NewTenant();
        var (globex, bob) = NewTenant();

        var queued = await dispatcher.SendAsync(new CreateNoteLater(acme, "milk"), ada, Ct);
        var refused = await dispatcher.SendAsync(new EnqueueThenRefuse(globex), bob, Ct);

        Assert.True(queued.IsSuccess);
        Assert.Equal("probe.refused", refused.Error!.Code);
        const string Jobs = "select count(*) from oxbow_job where kind = 'levee_operation' and args->'input'->>'tenant' = $1";
        Assert.Equal(1, await postgres.CountAsync(Jobs, acme));
        Assert.Equal(0, await postgres.CountAsync(Jobs, globex));
    }

    [Fact]
    public async Task A_repeated_create_conflicts_instead_of_writing_twice()
    {
        using var host = Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (acme, ada) = NewTenant();
        var create = new CreateNote(Guid.CreateVersion7(), acme, "milk");

        await dispatcher.SendAsync(create, ada, Ct);
        var again = await dispatcher.SendAsync(create, ada, Ct);

        Assert.Equal((ErrorKind.Conflict, LeveeErrors.Conflict), (again.Error!.Kind, again.Error.Code));
        Assert.Equal(1, await postgres.CountAsync("select count(*) from notes where tenant = $1", acme));
    }

    [Fact]
    public async Task An_enqueue_is_authorized_as_its_caller()
    {
        using var host = Build();
        var (acme, ada) = NewTenant();
        var (globex, _) = NewTenant();

        var result = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new EnqueueElsewhere(acme, globex), ada, Ct);

        Assert.Equal("access.tenant", result.Error!.Code);
        Assert.Equal(0, await postgres.CountAsync("select count(*) from oxbow_job where args->'input'->>'tenant' = $1", globex));
    }

    [Theory]
    [InlineData("40001", LeveeErrors.Contention)]
    [InlineData("40P01", LeveeErrors.Contention)]
    [InlineData("57P03", LeveeErrors.StoreUnavailable)]
    public async Task Contention_and_an_unready_store_are_unavailable_so_callers_and_jobs_retry(string sqlState, string code)
    {
        using var host = Build();
        await using var scope = host.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();

        var failed = new DbUpdateException("save failed", new PostgresException("no", "ERROR", "ERROR", sqlState));

        Assert.Equal((ErrorKind.Unavailable, code, true), store.Classify(failed) is { } e ? (e.Kind, e.Code, e.Transient) : default);
    }

    [Theory]
    [InlineData("23514")]
    [InlineData("23503")]
    [InlineData("23502")]
    [InlineData("P0001")]
    public async Task A_rule_the_store_holds_is_a_conflict_no_retry_can_fix(string sqlState)
    {
        using var host = Build();
        await using var scope = host.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();

        var refused = store.Classify(new DbUpdateException("commit failed", new PostgresException("no", "ERROR", "ERROR", sqlState)));

        Assert.Equal((ErrorKind.Conflict, LeveeErrors.ConstraintViolation, false), (refused!.Kind, refused.Code, refused.Transient));
    }

    [Fact]
    public async Task Levees_schema_lives_where_the_app_keeps_its_tables()
    {
        await postgres.ExecuteAsync("create schema elsewhere; create table elsewhere.things (id int primary key, tenant text not null);");
        await postgres.ExecuteAsync(LeveeSchema.Create("elsewhere") + LeveeSchema.Isolation("elsewhere.things", "tenant", schema: "elsewhere"));

        Assert.Equal(1, await postgres.CountAsync("select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = 'elsewhere' and p.proname = 'levee_scope'"));
        Assert.Equal(1, await postgres.CountAsync("select count(*) from pg_tables where schemaname = 'elsewhere' and tablename = 'levee_audit'"));
        Assert.Equal(1, await postgres.CountAsync("select count(*) from pg_policies where schemaname = 'elsewhere' and tablename = 'things'"));
    }

    [Fact]
    public async Task An_unreachable_store_is_unavailable()
    {
        using var host = Build();
        await using var scope = host.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();

        Assert.Equal(LeveeErrors.StoreUnavailable, store.Classify(new NpgsqlException("down", new TimeoutException()))?.Code);
        Assert.Null(store.Classify(new InvalidOperationException("a bug")));
        Assert.Equal((ErrorKind.Conflict, LeveeErrors.Stale), store.Classify(new DbUpdateConcurrencyException("0 rows")) is { } stale ? (stale.Kind, stale.Code) : default);
        Assert.Equal((ErrorKind.Validation, LeveeErrors.InvalidValue),
            store.Classify(new PostgresException("invalid input syntax for type uuid", "ERROR", "ERROR", PostgresErrorCodes.InvalidTextRepresentation)) is { } value ? (value.Kind, value.Code) : default);
    }

    [Theory]
    [InlineData(Unfit.Repeats, "Idempotency.None")]
    [InlineData(Unfit.Secret, "is a secret")]
    [InlineData(Unfit.Streams, "streams")]
    public async Task An_operation_unfit_to_run_as_a_job_is_refused(Unfit unfit, string why)
    {
        var logs = new Logs();
        using var host = Build(logs: logs);
        var (acme, ada) = NewTenant();

        var result = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new EnqueueUnfit(acme, unfit), ada, Ct);

        // A bug, not a refusal: the caller gets only the trace; the log has why, under it.
        Assert.Equal((ErrorKind.Internal, LeveeErrors.Internal), (result.Error!.Kind, result.Error.Code));
        var logged = Assert.Single(logs.Entries, e => e.Exception is InvalidOperationException);
        Assert.Contains(why, logged.Exception!.Message, StringComparison.Ordinal);
        Assert.Contains(result.Error.Trace!, logged.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task The_apps_own_jobs_commit_or_roll_back_with_the_operation(bool refuse, long jobs)
    {
        using var host = Build();
        var (acme, ada) = NewTenant();

        await host.Services.GetRequiredService<Dispatcher>().SendAsync(new PingLater(acme, refuse), ada, Ct);

        Assert.Equal(jobs, await postgres.CountAsync("select count(*) from oxbow_job where kind = 'probe_ping' and args->>'tenant' = $1", acme));
    }

    [Fact]
    public async Task An_adapter_flushes_to_read_its_own_writes_and_only_the_dispatcher_commits()
    {
        using var host = Build();
        var (acme, ada) = NewTenant();

        var read = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new FlushThenRead(acme), ada, Ct);
        using var outside = host.Services.CreateScope();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => outside.ServiceProvider.GetRequiredService<IFlush>().FlushAsync(Ct));

        Assert.Equal(1, read.Value);
        Assert.Contains("outside an operation", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_process_that_works_no_queue_enqueues_kinds_others_work()
    {
        using var host = Build();
        var (acme, ada) = NewTenant();

        var result = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new EnqueueElsewhereKind(acme), ada, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, await postgres.CountAsync("select count(*) from oxbow_job where kind = 'probe_elsewhere' and args->>'tenant' = $1", acme));
    }

    [Fact]
    public async Task A_job_with_secret_inputs_is_sealed_at_rest()
    {
        using var host = Build(seal: true);
        var (acme, ada) = NewTenant();

        var result = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new EnqueueUnfit(acme, Unfit.Secret), ada, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, await postgres.CountAsync(
            "select count(*) from oxbow_job where kind = 'levee_operation_sealed' and args #>> '{}' like 'oxbow:enc:%' and args::text not like '%1234%'"));
    }

    [Fact]
    public async Task A_job_runs_through_the_pipeline_for_whoever_enqueued_it()
    {
        using var host = Build(work: true);
        await host.StartAsync(Ct);
        var (acme, ada) = NewTenant();

        await host.Services.GetRequiredService<Dispatcher>().SendAsync(new CreateNoteLater(acme, "milk"), ada, Ct);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await postgres.CountAsync("select count(*) from notes where tenant = $1", acme) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
        }
        Assert.Equal(1, await postgres.CountAsync("select count(*) from levee_audit where scope = $1 and operation = 'notes.create' and subject = $2", acme, ada.Subject));
        Assert.Contains(ada.Subject, RestoreMembers.Restored);
        await host.StopAsync(Ct);
    }

    [Fact]
    public void A_client_reads_the_contract_from_Contracts_alone()
    {
        var create = Sample.Contracts.LeveeCatalog.Catalog.Find(typeof(CreateNote))!;

        Assert.Equal(("notes.create", Idempotency.Conflicts), (create.Name, create.Idempotency));
        Assert.Equal(["note.empty", "note.banned"], create.Errors);
    }

    [Fact]
    public async Task A_caller_scoped_job_runs_in_the_scope_its_enqueuer_carried()
    {
        using var host = Build(work: true);
        await host.StartAsync(Ct);
        var (acme, _) = NewTenant();
        var key = new ApiKey($"key_1@{acme}", acme);

        var sent = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new NoteMineLater(Guid.CreateVersion7(), "milk"), key, Ct);

        Assert.True(sent.IsSuccess, sent.Error?.Message);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await postgres.CountAsync("select count(*) from notes where tenant = $1", acme) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
        }
        Assert.Equal(1, await postgres.CountAsync("select count(*) from notes where tenant = $1 and text = 'milk'", acme));
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task A_job_waits_as_long_as_its_failure_says()
    {
        using var host = Build(work: true);
        await host.StartAsync(Ct);
        var (acme, _) = NewTenant();
        var started = DateTimeOffset.UtcNow;

        await host.Services.GetRequiredService<Dispatcher>().SendAsync(new VendorBusyLater(), new ApiKey($"key_2@{acme}", acme), Ct);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        const string Waiting = "select count(*) from oxbow_job where kind = 'levee_operation' and args->>'operation' = 'probe.vendor_busy' and args->>'scope' = $1 and state = 'retryable' and scheduled_at > $2";
        while (await postgres.CountAsync(Waiting, acme, started.AddMinutes(9)) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
        }
        Assert.Equal(1, await postgres.CountAsync(Waiting, acme, started.AddMinutes(9)));
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task The_app_role_writes_the_audit_only_through_Levee()
    {
        await using var connection = new NpgsqlConnection(postgres.App);
        await connection.OpenAsync(Ct);
        await using var forge = new NpgsqlCommand(
            "insert into levee_audit (id, at, scope, subject, operation, input) values (gen_random_uuid(), now(), null, 'someone-else', 'x', '{}')", connection);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => forge.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task The_apps_context_runs_nothing_outside_an_operation()
    {
        using var host = Build();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var (tenant, _) = NewTenant();
        var now = TimeProvider.System.GetUtcNow();

        var raw = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ExecuteSqlRawAsync("delete from notes", Ct));
        // Two rows: EF wraps them in a transaction of its own, which isn't an operation's.
        db.Set<Note>().AddRange(Note.Create(Guid.CreateVersion7(now), tenant, "one", now), Note.Create(Guid.CreateVersion7(now), tenant, "two", now));
        var saved = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));
        db.ChangeTracker.Clear();

        Assert.Contains("outside an operation's unit of work", raw.Message, StringComparison.Ordinal);
        Assert.Contains("outside an operation's unit of work", saved.ToString(), StringComparison.Ordinal);
        using (LeveeOwner.Run())
        {
            using (LeveeOwner.Run())
            {
            }
            // The guard steps aside for owner work, an inner Run's end included; the role and row-level security still hold it.
            Assert.Equal(0, await db.Database.ExecuteSqlRawAsync("delete from notes", Ct));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ExecuteSqlRawAsync("delete from notes", Ct));
        Assert.True(await db.Database.CanConnectAsync(Ct));
    }

    [Fact]
    public async Task A_transaction_an_operation_finished_lets_nothing_through_after_it()
    {
        // One pooled connection: Npgsql hands a connection's transaction object to its next transaction, so the operation's vouches for nothing after it.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(postgres.App) { MaxPoolSize = 1 }.ConnectionString));
        builder.Services.AddDbContext<SampleDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<SampleDbContext>();
        builder.Services.AddSingleton<IAccessPolicy, TenantAccess>();
        using var host = builder.Build();
        var (tenant, ada) = NewTenant();
        var now = TimeProvider.System.GetUtcNow();
        Assert.True((await host.Services.GetRequiredService<Dispatcher>().SendAsync(new CreateNote(Guid.CreateVersion7(now), tenant, "inside"), ada, Ct)).IsSuccess);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        await using var own = await db.Database.BeginTransactionAsync(Ct);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ExecuteSqlRawAsync("delete from notes", Ct));

        Assert.Contains("outside an operation's unit of work", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_context_is_refused_at_start()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.App));
        builder.Services.AddDbContext<SampleDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddDbContext<SideDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<SampleDbContext>();
        using var host = builder.Build();

        var refused = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => host.StartAsync(Ct));

        Assert.Contains("SideDbContext is a DbContext besides SampleDbContext", refused.Message, StringComparison.Ordinal);
    }

    private sealed class SideDbContext(DbContextOptions<SideDbContext> options) : DbContext(options);

    [Fact]
    public async Task A_second_context_configured_on_its_own_is_refused_at_start()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.App));
        builder.Services.AddDbContext<SampleDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddScoped<ReportsDb>();
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<SampleDbContext>();
        using var host = builder.Build();

        var refused = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => host.StartAsync(Ct));

        Assert.Contains("ReportsDb is a DbContext besides SampleDbContext", refused.Message, StringComparison.Ordinal);
    }

    private sealed class ReportsDb : DbContext, IReportsDb;

    private interface IReportsDb;

    [Fact]
    public async Task A_second_context_behind_an_interface_is_refused_at_start()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.App));
        builder.Services.AddDbContext<SampleDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddScoped<IReportsDb, ReportsDb>();
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<SampleDbContext>();
        using var host = builder.Build();

        var refused = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => host.StartAsync(Ct));

        Assert.Contains("ReportsDb is a DbContext besides SampleDbContext", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_table_the_context_maps_without_a_policy_fails_closed_until_it_has_one()
    {
        await using (var owner = new NpgsqlConnection(postgres.Owner))
        {
            await owner.OpenAsync(Ct);
            await using var create = new NpgsqlCommand("create table ledger (id uuid primary key, tenant text not null)", owner);
            await create.ExecuteNonQueryAsync(Ct);
        }
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddDbContext<LedgerDb>(o => o.UseNpgsql(postgres.App));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<LedgerDb>();
        using var host = builder.Build();
        async Task Begin()
        {
            await using var scope = host.Services.CreateAsyncScope();
            await using var unit = await scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(new WorkScope("acme", ReadOnly: true), Ct);
        }

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(Begin);
        await using (var owner = new NpgsqlConnection(postgres.Owner))
        {
            await owner.OpenAsync(Ct);
            await using var isolate = new NpgsqlCommand(LeveeSchema.Isolation("ledger", "tenant"), owner);
            await isolate.ExecuteNonQueryAsync(Ct);
        }
        await Begin();

        Assert.Contains("ledger has no row-level security policy", refused.Message, StringComparison.Ordinal);
    }

    private sealed record Ledger(Guid Id, string Tenant);

    [Fact]
    public async Task A_table_reached_by_any_mapping_is_checked()
    {
        await using (var owner = new NpgsqlConnection(postgres.Owner))
        {
            await owner.OpenAsync(Ct);
            await using var create = new NpgsqlCommand("""
                create table split_main (id uuid primary key, tenant text not null);
                create table split_detail (id uuid primary key, note text);
                create table viewed (id uuid primary key, tenant text not null);
                create view viewed_v as select * from viewed;
                create view viewed_invoker_v with (security_invoker = true) as select * from viewed;
                grant select on viewed_v, viewed_invoker_v to app;
                create schema by_path;
                grant usage on schema by_path to app;
                create table by_path.found_by_path (id uuid primary key, tenant text not null);
                grant select on by_path.found_by_path to app;
                """ + LeveeSchema.Isolation("split_main", "tenant") + LeveeSchema.Isolation("viewed", "tenant"), owner);
            await create.ExecuteNonQueryAsync(Ct);
        }

        Assert.Contains("split_detail", await Refusal<SplitDb>(postgres.App), StringComparison.Ordinal);
        // The fixture's owner is a superuser, as a container's is: a plain view reads every row.
        Assert.Contains("viewed_v reads around row-level security", await Refusal<ViewedDb>(postgres.App), StringComparison.Ordinal);
        Assert.Null(await RefusalOrNull<InvokerViewedDb>(postgres.App));
        Assert.Contains("found_by_path", await Refusal<PathDb>(new NpgsqlConnectionStringBuilder(postgres.App) { SearchPath = "by_path,public" }.ConnectionString), StringComparison.Ordinal);
    }

    private async Task<string> Refusal<TContext>(string connection) where TContext : DbContext =>
        await RefusalOrNull<TContext>(connection) ?? throw new InvalidOperationException($"{typeof(TContext).Name} passed.");

    private async Task<string?> RefusalOrNull<TContext>(string connection) where TContext : DbContext
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddDbContext<TContext>(o => o.UseNpgsql(connection));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<TContext>();
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        try
        {
            await using var unit = await scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(new WorkScope("acme", ReadOnly: true), Ct);
            return null;
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
    }

    private sealed class Split
    {
        public Guid Id { get; init; }
        public string Tenant { get; init; } = "";
        public string? Note { get; init; }
    }

    private sealed class SplitDb(DbContextOptions<SplitDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Split>(e =>
            {
                e.ToTable("split_main").SplitToTable("split_detail", t => t.Property(s => s.Note).HasColumnName("note"));
                e.Property(s => s.Id).HasColumnName("id");
                e.Property(s => s.Tenant).HasColumnName("tenant");
            });
    }

    private sealed class ViewedDb(DbContextOptions<ViewedDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Ledger>(e =>
            {
                e.ToTable("viewed").ToView("viewed_v");
                e.Property(l => l.Id).HasColumnName("id");
                e.Property(l => l.Tenant).HasColumnName("tenant");
            });
    }

    private sealed class InvokerViewedDb(DbContextOptions<InvokerViewedDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Ledger>(e =>
            {
                e.ToTable("viewed").ToView("viewed_invoker_v");
                e.Property(l => l.Id).HasColumnName("id");
                e.Property(l => l.Tenant).HasColumnName("tenant");
            });
    }

    [Fact]
    public async Task Postgres_own_refusals_are_classified_as_they_happen()
    {
        await using (var owner = new NpgsqlConnection(postgres.Owner))
        {
            await owner.OpenAsync(Ct);
            await using var create = new NpgsqlCommand("create table plans (id uuid primary key, tenant text not null); "
                + LeveeSchema.Shared("plans") + " insert into plans values ('00000000-0000-0000-0000-000000000001', 'any');", owner);
            await create.ExecuteNonQueryAsync(Ct);
        }
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddDbContext<PlansDb>(o => o.UseNpgsql(postgres.App));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<PlansDb>();
        using var host = builder.Build();
        async Task<Error?> Refused(Func<PlansDb, Task> work)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
            await using var unit = await factory.BeginAsync(new WorkScope("acme", ReadOnly: false), Ct);
            try
            {
                await work(scope.ServiceProvider.GetRequiredService<PlansDb>());
                return null;
            }
            catch (Exception e)
            {
                return factory.Classify(e);
            }
        }

        // A scope's write to a shared row: row-level security hides it from the update, so no row changes.
        var stale = await Refused(async db =>
        {
            db.Update(new Ledger(Guid.Parse("00000000-0000-0000-0000-000000000001"), "mine"));
            await db.SaveChangesAsync(Ct);
        });
        var value = await Refused(db => db.Database.ExecuteSqlRawAsync("select 'not-a-uuid'::uuid", Ct));
        var bug = await Refused(db => db.Database.ExecuteSqlRawAsync("select 1/0", Ct));

        Assert.Equal(LeveeErrors.Stale, stale?.Code);
        Assert.Equal(LeveeErrors.InvalidValue, value?.Code);
        Assert.Null(bug);
    }

    private sealed class PlansDb(DbContextOptions<PlansDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Ledger>(e =>
            {
                e.ToTable("plans");
                e.Property(l => l.Id).HasColumnName("id");
                e.Property(l => l.Tenant).HasColumnName("tenant");
            });
    }

    private sealed class PathDb(DbContextOptions<PathDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Ledger>(e =>
            {
                e.ToTable("found_by_path");
                e.Property(l => l.Id).HasColumnName("id");
                e.Property(l => l.Tenant).HasColumnName("tenant");
            });
    }

    private sealed class LedgerDb(DbContextOptions<LedgerDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Ledger>(e =>
            {
                e.ToTable("ledger");
                e.Property(l => l.Id).HasColumnName("id");
                e.Property(l => l.Tenant).HasColumnName("tenant");
            });
    }

    [Fact]
    public async Task A_context_the_guard_cant_reach_is_refused_at_start()
    {
        var connection = postgres.App;
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddSingleton(new OwnConfiguration(connection));
        builder.Services.AddDbContext<SelfConfigured>();
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<SelfConfigured>();
        using var host = builder.Build();

        var refused = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => host.StartAsync(Ct));

        Assert.Contains("SelfConfigured doesn't carry Levee's guard", refused.Message, StringComparison.Ordinal);
    }

    private sealed record OwnConfiguration(string Connection);

    private sealed class SelfConfigured(OwnConfiguration own) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseNpgsql(own.Connection);
    }

    [Fact]
    public async Task A_retrying_strategy_is_refused_at_start()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddDbContext<SampleDbContext>(o => o.UseNpgsql(postgres.App, n => n.EnableRetryOnFailure()));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<SampleDbContext>();
        using var host = builder.Build();

        var refused = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => host.StartAsync(Ct));

        Assert.Contains("EnableRetryOnFailure", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_work_lasts_until_its_last_run_ends_in_any_order()
    {
        using var host = Build();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();

        var outer = LeveeOwner.Run();
        var inner = LeveeOwner.Run();
        outer.Dispose();
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync("delete from notes where false", Ct));
        inner.Dispose();
        inner.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ExecuteSqlRawAsync("delete from notes where false", Ct));
    }

    [Fact]
    public async Task The_audit_is_stamped_by_the_database_not_its_caller()
    {
        await using var connection = new NpgsqlConnection(postgres.App);
        await connection.OpenAsync(Ct);
        await using var backdate = new NpgsqlCommand(
            "select levee_audit_write(gen_random_uuid(), '2001-01-01'::timestamptz, null, 'someone', 'x', '{}')", connection);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => backdate.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.UndefinedFunction, refused.SqlState);
    }
}
