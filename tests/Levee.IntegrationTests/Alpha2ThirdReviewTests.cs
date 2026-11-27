using System.Text.Json;
using System.Text.Json.Serialization;
using Levee.EntityFrameworkCore;
using Levee.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Levee.IntegrationTests;

public sealed class Alpha2ThirdReviewTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reapplying_grants_removes_a_legacy_table_grant_in_a_second_schema()
    {
        await postgres.ExecuteAsync("create schema audit_legacy; create table audit_legacy.lookup(id int);" +
            " grant usage on schema audit_legacy to app; grant select on audit_legacy.lookup to app;" +
            LeveeSchema.Grants("app"));
        await using var app = NpgsqlDataSource.Create(postgres.App);
        await using var grants = app.CreateCommand("select has_table_privilege('audit_legacy.lookup', 'select')");
        Assert.Equal(false, await grants.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Both_installations_keep_direct_audit_writes_revoked()
    {
        await postgres.ExecuteAsync("create schema audit_billing; create role audit_billing_app login password 'billing';" +
            LeveeSchema.Create("audit_billing") + LeveeSchema.Grants("audit_billing_app", "audit_billing"));
        var billing = new NpgsqlConnectionStringBuilder(postgres.App) { Username = "audit_billing_app", Password = "billing" };
        await using var app = NpgsqlDataSource.Create(billing.ConnectionString);
        await using var grants = app.CreateCommand("""
            select has_table_privilege('audit_billing_app', 'audit_billing.levee_audit', 'insert'),
                   has_table_privilege('audit_billing_app', 'public.levee_audit', 'insert')
            """);
        await using var row = await grants.ExecuteReaderAsync(Ct);
        Assert.True(await row.ReadAsync(Ct));
        Assert.Equal((false, false), (row.GetBoolean(0), row.GetBoolean(1)));
    }

    [SeparateContext("Levee.IdentityStandIn")]
    private sealed class DirectoryStore(DbContextOptions<DirectoryStore> options) : DbContext(options);

    [Fact]
    public async Task An_extra_unkeyed_registration_of_the_separate_store_is_refused()
    {
        await postgres.ExecuteAsync("create role audit_library login password 'library'");
        var connection = new NpgsqlConnectionStringBuilder(postgres.App) { Username = "audit_library", Password = "library" }.ConnectionString;
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<Sample.Adapters.SampleDbContext>(o => o.UseNpgsql(postgres.App));
        builder.Services.AddLeveeSeparateContext<DirectoryStore>(o => o.UseNpgsql(connection));
        builder.Services.AddScoped<DirectoryStore>();
        builder.Services.AddLeveePostgres<Sample.Adapters.SampleDbContext>();
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var stores = scope.ServiceProvider.GetServices<DirectoryStore>().ToArray();
        Assert.Equal(2, stores.Length);
        Assert.NotSame(stores[0], stores[1]);
        var failure = await Record.ExceptionAsync(() => host.StartAsync(Ct));
        if (failure is null)
        {
            await host.StopAsync(Ct);
        }
        Assert.IsType<OptionsValidationException>(failure);
    }
}
