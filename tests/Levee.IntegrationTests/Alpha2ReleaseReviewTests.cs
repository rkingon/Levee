using Levee.EntityFrameworkCore;
using Npgsql;

namespace Levee.IntegrationTests;

public sealed class Alpha2ReleaseReviewTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NpgsqlDataSource Login(string role) => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(postgres.App) { Username = role, Password = "audit" }.ConnectionString);

    [Theory]
    [InlineData("review_lower", "review_lower_schema", "review_lower_app")]
    [InlineData("Review Owner", "review_mixed_schema", "review_mixed_app")]
    public async Task A_migration_owner_can_reconcile_its_own_defaults(string ownerRole, string schema, string runtime)
    {
        await postgres.ExecuteAsync($"create role \"{ownerRole}\" login password 'audit'; create role {runtime}; " +
            $"create schema {schema} authorization \"{ownerRole}\";");
        await using var owner = Login(ownerRole);
        await using var install = owner.CreateCommand(LeveeSchema.Create(schema) +
            $"alter default privileges in schema {schema} grant select on tables to {runtime};");
        await install.ExecuteNonQueryAsync(Ct);
        await using var reset = owner.CreateCommand(LeveeSchema.Grants(runtime, schema));
        await reset.ExecuteNonQueryAsync(Ct);
        await using var remaining = owner.CreateCommand("""
            select count(*) from pg_default_acl a cross join lateral aclexplode(a.defaclacl) g
            where g.grantee = $1::regrole and g.privilege_type = 'SELECT'
            """);
        remaining.Parameters.AddWithValue(runtime);
        Assert.Equal(0L, await remaining.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task An_existing_role_membership_is_accounted_for_by_the_installation_allow_list()
    {
        await postgres.ExecuteAsync("""
            create role review_group_app login password 'audit';
            create role review_catalog_readers;
            grant review_catalog_readers to review_group_app;
            create schema review_catalog;
            create schema review_group_product;
            """ + LeveeSchema.Create("review_catalog") + LeveeSchema.Create("review_group_product") +
            "create table review_catalog.plans(id int);" + LeveeSchema.Shared("review_catalog.plans", "review_catalog") +
            "grant usage on schema review_catalog to review_catalog_readers; grant select on review_catalog.plans to review_catalog_readers;");
        // Refused, naming the role it belongs to: whatever that holds, it holds.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync(LeveeSchema.Grants("review_group_app", "review_group_product")));
        Assert.Contains("belongs to review_catalog_readers", refused.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_remaining_grant_from_an_independent_owner_is_named_and_setup_can_be_retried()
    {
        await postgres.ExecuteAsync("""
            create role review_installer login password 'audit';
            create role review_supplier login password 'audit';
            create role review_retry_app;
            create schema review_product authorization review_installer;
            create schema review_supplier_data authorization review_supplier;
            """);
        await using var supplier = Login("review_supplier");
        await using var legacy = supplier.CreateCommand("""
            create table review_supplier_data.prices(id int);
            grant select(id) on review_supplier_data.prices to review_retry_app;
            alter default privileges in schema review_supplier_data grant select on tables to review_retry_app;
            """);
        await legacy.ExecuteNonQueryAsync(Ct);
        await using var owner = Login("review_installer");
        await using var install = owner.CreateCommand(LeveeSchema.Create("review_product"));
        await install.ExecuteNonQueryAsync(Ct);
        await using var reset = owner.CreateCommand(LeveeSchema.Grants("review_retry_app", "review_product"));
        var error = await Assert.ThrowsAsync<PostgresException>(() => reset.ExecuteNonQueryAsync(Ct));
        Assert.Contains("review_supplier_data.prices", error.MessageText, StringComparison.Ordinal);
        Assert.Contains("default privileges of review_supplier", error.MessageText, StringComparison.Ordinal);
        await using var repair = supplier.CreateCommand("""
            revoke select(id) on review_supplier_data.prices from review_retry_app;
            alter default privileges in schema review_supplier_data revoke select on tables from review_retry_app;
            """);
        await repair.ExecuteNonQueryAsync(Ct);
        await reset.ExecuteNonQueryAsync(Ct);
    }
}
