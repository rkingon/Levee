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

    [Fact]
    public async Task A_signal_reaches_its_scopes_watchers_when_its_operation_commits_and_never_when_it_rolls_back()
    {
        using var host = Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (acme, ada) = NewTenant();
        var (globex, hank) = NewTenant();
        var descriptor = dispatcher.Catalog.Find(typeof(SignalProbe))!;
        await using var scope = host.Services.CreateAsyncScope();
        var signals = scope.ServiceProvider.GetRequiredService<IChangeSignals>();
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var acmeWatch = signals.WatchAsync("notes.changed", new SubscriptionContext(ada, descriptor, new WorkScope(acme, true)), watching.Token)
            .GetAsyncEnumerator(watching.Token);
        var globexWatch = signals.WatchAsync("notes.changed", new SubscriptionContext(hank, descriptor, new WorkScope(globex, true)), watching.Token)
            .GetAsyncEnumerator(watching.Token);
        async Task<ChangeSignal> Next(IAsyncEnumerator<ChangeSignal> watch)
        {
            Assert.True(await watch.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct));
            return watch.Current;
        }

        // The listening connection opens with the first watch: raise until it's heard, then drain what was raised before.
        var heard = acmeWatch.MoveNextAsync().AsTask();
        while (!heard.IsCompleted)
        {
            Assert.True((await dispatcher.SendAsync(new SignalProbe(acme, "warm", Refuse: false), ada, Ct)).IsSuccess);
            await Task.WhenAny(heard, Task.Delay(200, Ct));
        }
        Assert.True(await heard);
        Assert.False((await dispatcher.SendAsync(new SignalProbe(acme, "rolled_back", Refuse: true), ada, Ct)).IsSuccess);
        Assert.True((await dispatcher.SendAsync(new SignalProbe(acme, "committed", Refuse: false), ada, Ct)).IsSuccess);
        Assert.True((await dispatcher.SendAsync(new SignalProbe(globex, "theirs", Refuse: false), hank, Ct)).IsSuccess);

        ChangeSignal seen;
        do
        {
            seen = await Next(acmeWatch);
        }
        while (seen.Key == "warm");
        Assert.Equal(new ChangeSignal("notes.changed", "committed", acme), seen);
        Assert.Equal(new ChangeSignal("notes.changed", "theirs", globex), await Next(globexWatch));
        await watching.CancelAsync();
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
    public async Task A_job_whose_caller_is_restored_elsewhere_is_cancelled_not_moved()
    {
        using var host = Build(work: true);
        await host.StartAsync(Ct);
        var (acme, _) = NewTenant();
        var subject = $"moved_1@{acme}";

        var sent = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new NoteMineLater(Guid.CreateVersion7(), "milk"), new ApiKey(subject, acme), Ct);

        Assert.True(sent.IsSuccess, sent.Error?.Message);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await postgres.CountAsync("select count(*) from oxbow_job where state = 'cancelled' and args::text like '%' || $1 || '%'", subject) == 0
            && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
        }
        Assert.Equal(1, await postgres.CountAsync("select count(*) from oxbow_job where state = 'cancelled' and args::text like '%' || $1 || '%'", subject));
        Assert.Equal(0, await postgres.CountAsync("select count(*) from notes where text = 'milk' and tenant in ($1, 'elsewhere')", acme));
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
    public async Task The_migration_history_is_the_owners_alone()
    {
        await postgres.ExecuteAsync("""create table if not exists "__EFMigrationsHistory" ("MigrationId" text primary key, "ProductVersion" text not null);"""
            + LeveeSchema.Grants("app"));
        await using var connection = new NpgsqlConnection(postgres.App);
        await connection.OpenAsync(Ct);
        await using var forge = new NpgsqlCommand("""insert into "__EFMigrationsHistory" values ('20990101000000_Forged', '10.0.0')""", connection);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => forge.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
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

    [Theory]
    [InlineData("identity", "clients", null)]
    [InlineData("public", "notes", "IdentityDb is a separate bounded context's, but maps public.notes")]
    public async Task A_separate_bounded_contexts_store_starts_beside_the_app_over_tables_of_its_own(string schema, string table, string? refusal)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.App));
        builder.Services.AddDbContext<SampleDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddDbContext<IdentityDb>(o => o.UseNpgsql(postgres.Owner).ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, PerTable>());
        builder.Services.AddSingleton(new IdentityTable(schema, table));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<SampleDbContext>();
        using var host = builder.Build();

        if (refusal is null)
        {
            await host.StartAsync(Ct);
            await host.StopAsync(Ct);
        }
        else
        {
            var refused = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => host.StartAsync(Ct));
            Assert.Contains(refusal, refused.Message, StringComparison.Ordinal);
        }
    }

    private sealed record IdentityTable(string Schema, string Table);

    [SeparateContext]
    private sealed class IdentityDb(DbContextOptions<IdentityDb> options, IdentityTable table) : DbContext(options)
    {
        public IdentityTable Table => table;

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Ledger>(e =>
            {
                e.ToTable(table.Table, table.Schema);
                e.Property(l => l.Id).HasColumnName("id");
                e.Property(l => l.Tenant).HasColumnName("tenant");
            });
    }

    // The two cases map different tables with one context type: each its own model.
    private sealed class PerTable : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), (context as IdentityDb)?.Table, designTime);
    }

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

        Assert.Contains("ledger: no forced row-level security with a policy", refused.Message, StringComparison.Ordinal);
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
        Assert.Contains("viewed_v: a view that reads with its owner's rights", await Refusal<ViewedDb>(postgres.App), StringComparison.Ordinal);
        Assert.Null(await RefusalOrNull<InvokerViewedDb>(postgres.App));
        Assert.Contains("found_by_path", await Refusal<PathDb>(new NpgsqlConnectionStringBuilder(postgres.App) { SearchPath = "by_path,public" }.ConnectionString), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_view_is_judged_by_what_it_reads_down_to_the_tables()
    {
        // An owner that is neither a superuser nor bypasses row-level security: the shape production should have.
        await using (var owner = new NpgsqlConnection(postgres.Owner))
        {
            await owner.OpenAsync(Ct);
            await using var create = new NpgsqlCommand("""
                create role reader_owner nologin nosuperuser nobypassrls;
                create table raw_rows (id uuid primary key, tenant text not null);
                create table hidden_rows (id uuid primary key, tenant text not null);
                create table held_rows (id uuid primary key, tenant text not null);
                create table loose_rows (id uuid primary key, tenant text not null);
                alter table loose_rows enable row level security;
                create view over_hidden with (security_invoker = true) as select * from hidden_rows;
                create view over_held with (security_invoker = on) as select * from held_rows;
                create materialized view frozen_rows as select * from held_rows;
                create view over_loose as select * from loose_rows;
                alter table loose_rows owner to reader_owner;
                alter view over_loose owner to reader_owner;
                grant select on raw_rows, hidden_rows, held_rows, loose_rows, over_hidden, over_held, frozen_rows, over_loose to app;
                """ + LeveeSchema.Isolation("held_rows", "tenant"), owner);
            await create.ExecuteNonQueryAsync(Ct);
        }

        Assert.Contains("raw_rows: no forced row-level security", await Refusal<ReadsRawRows>(postgres.App), StringComparison.Ordinal);
        Assert.Contains("hidden_rows: no forced row-level security", await Refusal<ReadsOverHidden>(postgres.App), StringComparison.Ordinal);
        Assert.Null(await RefusalOrNull<ReadsOverHeld>(postgres.App));
        Assert.Contains("frozen_rows: a materialized view", await Refusal<ReadsFrozen>(postgres.App), StringComparison.Ordinal);
        Assert.Contains("loose_rows: no forced row-level security", await Refusal<ReadsOverLoose>(postgres.App), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_target_a_context_reaches_is_checked_on_its_own()
    {
        await using (var owner = new NpgsqlConnection(postgres.Owner))
        {
            await owner.OpenAsync(Ct);
            await using var create = new NpgsqlCommand("""
                create schema target_a; create schema target_b;
                grant usage on schema target_a, target_b to app;
                create table target_a.things (id uuid primary key, tenant text not null);
                create table target_b.things (id uuid primary key, tenant text not null);
                grant select on target_a.things, target_b.things to app;
                """ + LeveeSchema.Isolation("target_a.things", "tenant"), owner);
            await create.ExecuteNonQueryAsync(Ct);
        }
        string On(string schema) => new NpgsqlConnectionStringBuilder(postgres.App) { SearchPath = schema }.ConnectionString;

        // The same context type, checked once against target_a, then sent to target_b by its options.
        Assert.Null(await RefusalOrNull<ThingsDb>(On("target_a")));
        Assert.Contains("things: no forced row-level security", await Refusal<ThingsDb>(On("target_b")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_server_with_the_same_names_is_checked_on_its_own()
    {
        const string Table = "create table twin_things (id uuid primary key, tenant text not null);";
        await postgres.ExecuteAsync(Table + LeveeSchema.Isolation("twin_things", "tenant") + "grant select on twin_things to app;");
        await using var twin = new Testcontainers.PostgreSql.PostgreSqlBuilder(Environment.GetEnvironmentVariable("LEVEE_POSTGRES_IMAGE") ?? "postgres:17-alpine").Build();
        await twin.StartAsync(Ct);
        await using (var owner = new NpgsqlConnection(twin.GetConnectionString()))
        {
            await owner.OpenAsync(Ct);
            // The same database, role and search path as the fixture's; the table without its policy.
            await using var create = new NpgsqlCommand("create role app login password 'app' nosuperuser nobypassrls;" + LeveeSchema.Sql + Table
                + LeveeSchema.Grants("app"), owner);
            await create.ExecuteNonQueryAsync(Ct);
        }
        var twinApp = new NpgsqlConnectionStringBuilder(twin.GetConnectionString()) { Username = "app", Password = "app" }.ConnectionString;

        Assert.Null(await RefusalOrNull<TwinDb>(postgres.App));
        Assert.Contains("twin_things: no forced row-level security", await Refusal<TwinDb>(twinApp), StringComparison.Ordinal);
    }

    private sealed class TwinDb(DbContextOptions<TwinDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Ledger>(e =>
            {
                e.ToTable("twin_things");
                e.Property(l => l.Id).HasColumnName("id");
                e.Property(l => l.Tenant).HasColumnName("tenant");
            });
    }

    private sealed class ThingsDb(DbContextOptions<ThingsDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Ledger>(e =>
            {
                e.ToTable("things");
                e.Property(l => l.Id).HasColumnName("id");
                e.Property(l => l.Tenant).HasColumnName("tenant");
            });
    }

    private abstract class ReadsDb(DbContextOptions options, string view) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Ledger>(e =>
            {
                e.ToView(view);
                e.Property(l => l.Id).HasColumnName("id");
                e.Property(l => l.Tenant).HasColumnName("tenant");
            });
    }

    private sealed class ReadsRawRows(DbContextOptions<ReadsRawRows> options) : ReadsDb(options, "raw_rows");

    private sealed class ReadsOverHidden(DbContextOptions<ReadsOverHidden> options) : ReadsDb(options, "over_hidden");

    private sealed class ReadsOverHeld(DbContextOptions<ReadsOverHeld> options) : ReadsDb(options, "over_held");

    private sealed class ReadsFrozen(DbContextOptions<ReadsFrozen> options) : ReadsDb(options, "frozen_rows");

    private sealed class ReadsOverLoose(DbContextOptions<ReadsOverLoose> options) : ReadsDb(options, "over_loose");

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
