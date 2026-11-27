using Levee.Testing;
using Npgsql;

namespace Levee.IntegrationTests;

/// <summary>Levee.Testing's Postgres: the app's role held as production holds it.</summary>
public sealed class TestingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_app_whose_migrations_add_Levees_schema_runs_them_alone()
    {
        await using var postgres = await LeveePostgres.StartAsync(new LeveePostgresOptions
        {
            Image = Environment.GetEnvironmentVariable("LEVEE_POSTGRES_IMAGE") ?? "postgres:17-alpine",
            LeveeSchema = false,
            Migrate = async (owner, ct) =>
            {
                await using var connection = new NpgsqlConnection(owner);
                await connection.OpenAsync(ct);
                await using var create = new NpgsqlCommand(Levee.EntityFrameworkCore.LeveeSchema.Sql, connection);
                await create.ExecuteNonQueryAsync(ct);
            },
        }, Ct);

        await using var app = new NpgsqlConnection(postgres.App);
        await app.OpenAsync(Ct);
        await using var scope = new NpgsqlCommand("select levee_scope()", app);
        Assert.Equal("", await scope.ExecuteScalarAsync(Ct) as string ?? "");
    }

    [Fact]
    public async Task The_app_role_is_held_to_row_level_security_and_writes_the_audit_only_through_Levee()
    {
        var migrated = false;
        await using var postgres = await LeveePostgres.StartAsync(new LeveePostgresOptions
        {
            Image = Environment.GetEnvironmentVariable("LEVEE_POSTGRES_IMAGE") ?? "postgres:17-alpine",
            Migrate = async (owner, ct) =>
            {
                migrated = true;
                await using var connection = new NpgsqlConnection(owner);
                await connection.OpenAsync(ct);
                await using var create = new NpgsqlCommand("create table notes (id uuid primary key, tenant text not null);", connection);
                await create.ExecuteNonQueryAsync(ct);
            },
            Sql = Levee.EntityFrameworkCore.LeveeSchema.Isolation("notes", "tenant") + "insert into notes values (gen_random_uuid(), 'acme');",
        }, Ct);

        await using var app = new NpgsqlConnection(postgres.App);
        await app.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("select count(*) from notes", app);
        await using var forge = new NpgsqlCommand("insert into levee_audit (id, at, scope, subject, operation, input) values (gen_random_uuid(), now(), null, 'x', 'x', '{}')", app);

        Assert.True(migrated);
        Assert.Equal(0L, await count.ExecuteScalarAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => forge.ExecuteNonQueryAsync(Ct))).SqlState);
    }
}
