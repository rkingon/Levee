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

    private IHost Build(string bannedWords = "none")
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = bannedWords;
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.App));
        builder.Services.AddDbContext<SampleDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
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
    public async Task An_installation_from_before_keys_gets_them_from_its_owner()
    {
        await postgres.ExecuteAsync($"""
            create role legacy_app login password 'legacy' nosuperuser nobypassrls;
            create schema legacy_keys;
            {LeveeSchema.Create("legacy_keys")}
            drop function legacy_keys.levee_idempotency_claim(text, text, text, bytea, interval);
            drop function legacy_keys.levee_idempotency_complete(text, text, text, jsonb);
            drop table legacy_keys.levee_idempotency;
            {LeveeSchema.Grants("legacy_app", "legacy_keys")}
            """);

        await postgres.ExecuteAsync(LeveeSchema.Idempotency("legacy_keys"));

        Assert.Equal("True", await postgres.TextAsync(
            "select has_function_privilege('legacy_app', 'legacy_keys.levee_idempotency_claim(text, text, text, bytea, interval)', 'execute')"));
        Assert.Equal("False", await postgres.TextAsync("select has_table_privilege('legacy_app', 'legacy_keys.levee_idempotency', 'select')"));
    }
}
