using System.Text;
using Levee.EntityFrameworkCore;
using Npgsql;
using Oxbow;
using Sample.Adapters;
using Testcontainers.PostgreSql;

namespace Levee.IntegrationTests;

/// <summary>
/// Postgres as production has it: the owner runs the schema, the app connects as a role that owns nothing, can create
/// nothing in the app's schema and doesn't bypass row-level security.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder(Environment.GetEnvironmentVariable("LEVEE_POSTGRES_IMAGE") ?? "postgres:17-alpine").Build();

    public string Owner => _postgres.GetConnectionString();

    public string App { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        var sql = new StringBuilder()
            .AppendLine("create role app login password 'app' nosuperuser nobypassrls nocreaterole nocreatedb;")
            .AppendLine("revoke create on schema public from public;")
            // Not even a temporary table: what Oxbow's setup revokes too.
            .AppendLine("do $$ begin execute format('revoke temporary on database %I from public', current_database()); end $$;")
            .AppendLine(LeveeSchema.Sql)
            .AppendLine(SampleDbContext.Sql);
        foreach (var migration in OxbowSchema.GetMigrations(0))
        {
            sql.AppendLine(migration.Sql);
        }
        sql.AppendLine(Grants);
        await ExecuteAsync(sql.ToString());
        App = new NpgsqlConnectionStringBuilder(Owner) { Username = "app", Password = "app" }.ConnectionString;
    }

    /// <summary>What `dotnet levee db grants app --jobs` prints: the shipped grants, held as the real role, Levee's first.</summary>
    public static string Grants { get; } = LeveeSchema.Grants("app") + "\n" + OxbowSchema.GetGrants("app")
        + "\nrevoke insert, update, delete on oxbow_migration from app;";

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>As the owner: one text value, for a failure's message.</summary>
    public async Task<string> TextAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(Owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters)
        {
            command.Parameters.AddWithValue(p);
        }
        return (await command.ExecuteScalarAsync())?.ToString() ?? "";
    }

    /// <summary>As the owner, past row-level security.</summary>
    public async Task<long> CountAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(Owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters)
        {
            command.Parameters.AddWithValue(p);
        }
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
