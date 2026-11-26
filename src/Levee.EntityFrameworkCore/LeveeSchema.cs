using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Levee.EntityFrameworkCore;

/// <summary>One audited operation: who, what, where, with its input as the audit keeps it (secrets redacted). Append-only.</summary>
public sealed class LeveeAuditRecord
{
    public required Guid Id { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? Scope { get; init; }
    public required string Subject { get; init; }
    public required string Operation { get; init; }
    public required string Input { get; init; }
}

/// <summary>
/// Levee's schema in Postgres: the scope functions RLS policies call, and the audit table, append-only and scope-isolated.
/// Everything is qualified by the schema it lives in (<c>public</c> unless an app keeps its tables elsewhere), so policies
/// and grants hold whatever a role's <c>search_path</c> is.
/// </summary>
public static class LeveeSchema
{
    public const string AuditTable = "levee_audit";

    /// <summary>Levee's objects in <c>public</c>: <see cref="Create"/>.</summary>
    public static string Sql { get; } = Create();

    /// <summary>Run once by the schema's owner, before anything uses Levee: in a migration, <see cref="LeveeMigrationBuilderExtensions.AddLeveeSchema"/>.</summary>
    public static string Create(string schema = "public")
    {
        Identifier(schema, nameof(schema));
        return $"""
            create function {schema}.levee_scope() returns text language sql stable
              as $$ select coalesce(current_setting('levee.scope', true), '') $$;
            create function {schema}.levee_platform() returns boolean language sql stable
              as $$ select coalesce(current_setting('levee.platform', true), '') = 'on' $$;
            create function {schema}.levee_append_only() returns trigger language plpgsql
              as $$ begin raise exception '% is append-only', tg_table_name; end $$;

            create table {schema}.{AuditTable} (
              id uuid primary key,
              at timestamptz not null,
              scope text,
              subject text not null,
              operation text not null,
              input jsonb not null
            );
            create index {AuditTable}_scope_at on {schema}.{AuditTable} (scope, at);
            {AppendOnly($"{schema}.{AuditTable}", schema)}
            {Isolation($"{schema}.{AuditTable}", "scope", schema: schema)}
            """;
    }

    /// <summary>
    /// Row-level security on <paramref name="table"/>: an operation sees and writes only rows whose <paramref name="column"/> is
    /// its scope; a platform operation, every row. Forced, so even the table's owner is held to it. <paramref name="schema"/>
    /// is where Levee's functions are. A policy over more than one column is written by hand over <c>levee_scope()</c>.
    /// </summary>
    public static string Isolation(string table, string column, string columnType = "text", string schema = "public")
    {
        Identifier(schema, nameof(schema));
        return $"""
            alter table {table} enable row level security;
            alter table {table} force row level security;
            create policy levee_scope on {table}
              using ({schema}.levee_platform() or {column} = nullif({schema}.levee_scope(), '')::{columnType})
              with check ({schema}.levee_platform() or {column} = nullif({schema}.levee_scope(), '')::{columnType});
            """;
    }

    /// <summary>
    /// What the app's runtime role needs in <paramref name="schema"/>, run by the owner: every table's rows (RLS still holds it
    /// to its scope), now and as migrations add tables, and Levee's functions; never changing the audit.
    /// </summary>
    public static string Grants(string role, string schema = "public")
    {
        Identifier(role, nameof(role));
        Identifier(schema, nameof(schema));
        return $"""
            grant usage on schema {schema} to {role};
            grant select, insert, update, delete on all tables in schema {schema} to {role};
            grant usage, select on all sequences in schema {schema} to {role};
            grant execute on function {schema}.levee_scope(), {schema}.levee_platform() to {role};
            alter default privileges in schema {schema} grant select, insert, update, delete on tables to {role};
            alter default privileges in schema {schema} grant usage, select on sequences to {role};
            revoke update, delete on {schema}.{AuditTable} from {role};
            """;
    }

    /// <summary>Refuses every update and delete on <paramref name="table"/>, whoever asks.</summary>
    public static string AppendOnly(string table, string schema = "public")
    {
        Identifier(schema, nameof(schema));
        var name = table.Contains('.', StringComparison.Ordinal) ? table[(table.LastIndexOf('.') + 1)..] : table;
        return $"create trigger {name}_append_only before update or delete on {table} for each row execute function {schema}.levee_append_only();";
    }

    private static void Identifier(string name, string parameter)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"\"{name}\" isn't a Postgres identifier.", parameter);
        }
    }
}

public static class LeveeModelBuilderExtensions
{
    /// <summary>Maps the audit table. Its DDL is <see cref="LeveeSchema.Sql"/>, not the model's, so migrations leave it alone.</summary>
    public static ModelBuilder AddLeveeAudit(this ModelBuilder modelBuilder, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<LeveeAuditRecord>(e =>
        {
            e.ToTable(LeveeSchema.AuditTable, schema, t => t.ExcludeFromMigrations());
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id");
            e.Property(a => a.At).HasColumnName("at");
            e.Property(a => a.Scope).HasColumnName("scope");
            e.Property(a => a.Subject).HasColumnName("subject");
            e.Property(a => a.Operation).HasColumnName("operation");
            e.Property(a => a.Input).HasColumnName("input").HasColumnType("jsonb");
        });
        return modelBuilder;
    }
}

public static class LeveeMigrationBuilderExtensions
{
    public static MigrationBuilder AddLeveeSchema(this MigrationBuilder migrationBuilder, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(LeveeSchema.Create(schema));
        return migrationBuilder;
    }

    /// <summary>In the migration that creates <paramref name="table"/>: see <see cref="LeveeSchema.Isolation"/>.</summary>
    public static MigrationBuilder EnableScopeIsolation(this MigrationBuilder migrationBuilder, string table, string column, string columnType = "text",
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(LeveeSchema.Isolation(table, column, columnType, schema));
        return migrationBuilder;
    }

    public static MigrationBuilder MakeAppendOnly(this MigrationBuilder migrationBuilder, string table, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(LeveeSchema.AppendOnly(table, schema));
        return migrationBuilder;
    }
}
