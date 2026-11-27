using System.Data.Common;
using Levee.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Levee.Testing;

/// <summary>What <see cref="LeveePostgres.StartAsync"/> sets up, beyond Levee's own schema and grants.</summary>
public sealed record LeveePostgresOptions
{
    /// <summary>The image, as production runs it.</summary>
    public string Image { get; init; } = "postgres:17-alpine";

    /// <summary>The role the app connects as: no superuser, no bypassing row-level security, owning nothing.</summary>
    public string Role { get; init; } = "app";

    /// <summary>
    /// Whether Levee's schema is run before <see cref="Migrate"/>. Off when the app's own migrations add it
    /// (<c>migrationBuilder.AddLeveeSchema()</c>), as they do in production.
    /// </summary>
    public bool LeveeSchema { get; init; } = true;

    /// <summary>The schema Levee's objects are in, as production has them (<c>LeveeSchema.Create(schema)</c>): its grants follow.</summary>
    public string Schema { get; init; } = "public";

    /// <summary>The app's schema, run as the owner after Levee's: its migrations (<c>Database.MigrateAsync</c>), given the owner's connection string.</summary>
    public Func<string, CancellationToken, Task>? Migrate { get; init; }

    /// <summary>SQL the owner runs after <see cref="Migrate"/>: each table's isolation policy, seed rows.</summary>
    public string? Sql { get; init; }

    /// <summary>SQL the owner runs after Levee's grants: another package's (Oxbow's), or the app's own narrowing.</summary>
    public string? Grants { get; init; }
}

/// <summary>
/// Postgres for pipeline tests as production has it. The dispatcher's guarantees (row-level security, the guard on the
/// context, the audit) live in the database, so a test of the pipeline runs against one, not a fake.
/// </summary>
public sealed class LeveePostgres : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;

    private LeveePostgres(PostgreSqlContainer container, string app)
    {
        _container = container;
        App = app;
    }

    /// <summary>The owner's connection string: schema, seeds, and reading past row-level security.</summary>
    public string Owner => _container.GetConnectionString();

    /// <summary>The app's connection string, as the role it runs as.</summary>
    public string App { get; }

    public static async Task<LeveePostgres> StartAsync(LeveePostgresOptions? options = null, CancellationToken ct = default)
    {
        options ??= new LeveePostgresOptions();
        var container = new PostgreSqlBuilder(options.Image).Build();
        await container.StartAsync(ct).ConfigureAwait(false);
        try
        {
            var owner = container.GetConnectionString();
            var role = options.Role;
            // The role's password is the test's alone: the container lives as long as the run.
            await ExecuteAsync(owner, $"""
                create role {Quote(role)} login password 'levee' nosuperuser nobypassrls nocreaterole nocreatedb;
                revoke create on schema public from public;
                do $$ begin execute format('revoke temporary on database %I from public', current_database()); end $$;
                {(options.LeveeSchema ? $"create schema if not exists {Quote(options.Schema)};\n{EntityFrameworkCore.LeveeSchema.Create(options.Schema)}" : "")}
                """, ct).ConfigureAwait(false);
            if (options.Migrate is { } migrate)
            {
                await migrate(owner, ct).ConfigureAwait(false);
            }
            await ExecuteAsync(owner, $"{options.Sql}\n{EntityFrameworkCore.LeveeSchema.Grants(role, options.Schema)}\n{options.Grants}", ct).ConfigureAwait(false);
            var app = new NpgsqlConnectionStringBuilder(owner) { Username = role, Password = "levee" }.ConnectionString;
            return new LeveePostgres(container, app);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs <paramref name="sql"/> as the owner.</summary>
    public Task ExecuteAsync(string sql, CancellationToken ct = default) => ExecuteAsync(Owner, sql, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return;
        }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using DbCommand command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Quote(string identifier) =>
        System.Text.RegularExpressions.Regex.IsMatch(identifier, "^[a-z_][a-z0-9_]{0,62}$")
            ? identifier
            : throw new ArgumentException($"\"{identifier}\" isn't a Postgres identifier.", nameof(identifier));
}
