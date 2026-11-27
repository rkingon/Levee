using Levee.EntityFrameworkCore;
using Levee.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Levee.IntegrationTests;

public sealed class Alpha2AclAuditTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class PingDb(DbContextOptions<PingDb> options) : DbContext(options);

    [Fact]
    public async Task Extension_members_are_exempt_and_an_ordinary_neighbor_still_requires_isolation()
    {
        await postgres.ExecuteAsync("""
            create role review_extension_app login password 'review';
            create schema review_extension;
            create schema review_diagnostics;
            create extension pg_stat_statements with schema review_diagnostics;
            grant usage on schema review_diagnostics to review_extension_app;
            """ + LeveeSchema.Create("review_extension") + LeveeSchema.Grants("review_extension_app", "review_extension"));
        var connection = new NpgsqlConnectionStringBuilder(postgres.App) { Username = "review_extension_app", Password = "review" }.ConnectionString;
        await using var app = NpgsqlDataSource.Create(connection);
        await using var readable = app.CreateCommand("""
            select has_table_privilege('review_diagnostics.pg_stat_statements','select') and exists (
              select from pg_depend where classid='pg_class'::regclass and objid='review_diagnostics.pg_stat_statements'::regclass and deptype='e')
            """);
        Assert.Equal(true, await readable.ExecuteScalarAsync(Ct));
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddDbContext<PingDb>(o => o.UseNpgsql(connection));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<PingDb>();
        builder.Services.AddSingleton<IAccessPolicy, TenantAccess>();
        using var host = builder.Build();
        var ping = await host.Services.GetRequiredService<Dispatcher>().SendAsync(new ReviewPing(), new SystemPrincipal("review"), Ct);
        Assert.True(ping.IsSuccess, ping.Error?.Message);

        await postgres.ExecuteAsync("""
            create table review_diagnostics.review_neighbor (id int);
            grant select on review_diagnostics.review_neighbor to public;
            """);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync(LeveeSchema.Grants("review_extension_app", "review_extension")));
        Assert.Contains("review_diagnostics.review_neighbor", refused.MessageText, StringComparison.Ordinal);
    }
}
