using System.Text.RegularExpressions;
using Levee.EntityFrameworkCore;
using Levee.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Levee.IntegrationTests;

public sealed class Alpha2UpgradeReviewTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class LegacyDb(DbContextOptions<LegacyDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) => builder.AddLeveeAudit("review_legacy");
    }

    private string Login(string role) => new NpgsqlConnectionStringBuilder(postgres.App)
    {
        Username = role, Password = "audit"
    }.ConnectionString;

    private static IHost Build<T>(string connection, Logs? logs = null) where T : DbContext
    {
        var builder = Host.CreateApplicationBuilder();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddDbContext<T>(o => o.UseNpgsql(connection));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<T>();
        builder.Services.AddSingleton<IAccessPolicy, TenantAccess>();
        return builder.Build();
    }

    [Fact]
    public async Task The_documented_policy_upgrade_preserves_an_existing_audit_and_commits_an_unscoped_write()
    {
        await postgres.ExecuteAsync("create role legacy_owner login password 'audit'; create role legacy_app login password 'audit'; " +
            "create schema review_legacy authorization legacy_owner;");
        await using var owner = NpgsqlDataSource.Create(Login("legacy_owner"));
        await using var install = owner.CreateCommand(Previous);
        await install.ExecuteNonQueryAsync(Ct);
        using var host = Build<LegacyDb>(Login("legacy_app"));
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var principal = new SystemPrincipal("upgrade-review");
        var before = await dispatcher.SendAsync(new EmitPlatformReview(), principal, Ct);
        Assert.True(before.IsSuccess, before.Error?.Message);
        var refused = await dispatcher.SendAsync(new EmitUnscopedReview(), principal, Ct);
        Assert.Equal("scope.violation", refused.Error?.Code);

        var policies = Regex.Matches(LeveeSchema.Create("review_legacy"), @"create policy \w+ on review_legacy\.levee_audit\b[^;]+;");
        Assert.Equal(2, policies.Count);
        await using var migration = owner.CreateCommand("drop policy levee_scope on review_legacy.levee_audit; " +
            string.Join(" ", policies.Select(m => m.Value)) + LeveeSchema.Grants("legacy_app", "review_legacy"));
        await migration.ExecuteNonQueryAsync(Ct);
        var after = await dispatcher.SendAsync(new EmitUnscopedReview(), principal, Ct);
        Assert.True(after.IsSuccess, after.Error?.Message);
        Assert.Equal(2, await postgres.CountAsync("select count(*) from review_legacy.levee_audit where subject=$1", principal.Subject));
    }

    private sealed class PingDb(DbContextOptions<PingDb> options) : DbContext(options);

    [Fact]
    public async Task The_first_dispatch_refuses_bypass_and_inherited_ownership_and_accepts_the_repaired_role()
    {
        await postgres.ExecuteAsync("create role review_guard_app login password 'audit' bypassrls; create role review_guard_owner; " +
            "create schema review_guard; " + LeveeSchema.Create("review_guard") + LeveeSchema.Grants("review_guard_app", "review_guard"));

        async Task Refuses(string connection, string reason)
        {
            using var logs = new Logs();
            using var host = Build<PingDb>(connection, logs);
            var result = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new ReviewPing(), new SystemPrincipal("guard-review"), Ct);
            Assert.False(result.IsSuccess);
            Assert.Contains(logs.Entries, e => e.Exception is InvalidOperationException && e.Exception.Message.Contains(reason, StringComparison.Ordinal));
        }

        await Refuses(postgres.Owner, "is a superuser");
        await Refuses(Login("review_guard_app"), "bypasses row-level security");
        await postgres.ExecuteAsync("alter role review_guard_app nobypassrls; alter table review_guard.levee_audit owner to review_guard_owner; " +
            "grant review_guard_owner to review_guard_app;");
        await Refuses(Login("review_guard_app"), "owns review_guard.levee_audit");
        await postgres.ExecuteAsync("revoke review_guard_owner from review_guard_app;");
        using var fixedHost = Build<PingDb>(Login("review_guard_app"));
        var ok = await fixedHost.Services.GetRequiredService<Dispatcher>().SendAsync(new ReviewPing(), new SystemPrincipal("guard-review"), Ct);
        Assert.True(ok.IsSuccess, ok.Error?.Message);
    }

    // Exact schema and grants emitted by the v0.0.1-alpha.1 CLI for review_legacy and legacy_app.
    private const string Previous = """
        -- Levee: scope functions and the audit
        create function review_legacy.levee_scope() returns text language sql stable
          as $$ select coalesce(current_setting('levee.scope', true), '') $$;
        create function review_legacy.levee_platform() returns boolean language sql stable
          as $$ select coalesce(current_setting('levee.platform', true), '') = 'on' $$;
        create function review_legacy.levee_append_only() returns trigger language plpgsql
          as $$ begin raise exception '% is append-only', tg_table_name; end $$;

        create table review_legacy.levee_audit (
          id uuid primary key,
          at timestamptz not null,
          scope text,
          subject text not null,
          operation text not null,
          input jsonb not null
        );
        create index levee_audit_scope_at on review_legacy.levee_audit (scope, at);
        -- The runtime role adds to the audit only through this, as the owner, stamped with the transaction's own time: it
        -- holds no insert, update or delete of its own, so it can neither backdate a record nor change or remove one.
        create function review_legacy.levee_audit_write(p_id uuid, p_scope text, p_subject text, p_operation text, p_input jsonb)
          returns void language sql security definer set search_path = review_legacy, pg_temp
          as $$ insert into review_legacy.levee_audit (id, at, scope, subject, operation, input)
                values (p_id, now(), p_scope, p_subject, p_operation, p_input) $$;
        create trigger levee_audit_append_only before update or delete on review_legacy.levee_audit for each row execute function review_legacy.levee_append_only();
        alter table review_legacy.levee_audit enable row level security;
        alter table review_legacy.levee_audit force row level security;
        create policy levee_scope on review_legacy.levee_audit
          using (review_legacy.levee_platform() or scope = nullif(review_legacy.levee_scope(), '')::text)
          with check (review_legacy.levee_platform() or scope = nullif(review_legacy.levee_scope(), '')::text);
        grant usage on schema review_legacy to legacy_app;
        grant select, insert, update, delete on all tables in schema review_legacy to legacy_app;
        grant usage, select on all sequences in schema review_legacy to legacy_app;
        grant execute on function review_legacy.levee_scope(), review_legacy.levee_platform() to legacy_app;
        grant execute on function review_legacy.levee_audit_write(uuid, text, text, text, jsonb) to legacy_app;
        alter default privileges in schema review_legacy grant select, insert, update, delete on tables to legacy_app;
        alter default privileges in schema review_legacy grant usage, select on sequences to legacy_app;
        revoke insert, update, delete on review_legacy.levee_audit from legacy_app;
        """;
}
