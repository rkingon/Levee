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
            -- The runtime role adds to the audit only through this, as the owner, stamped with the transaction's own time: it
            -- holds no insert, update or delete of its own, so it can neither backdate a record nor change or remove one.
            create function {schema}.levee_audit_write(p_id uuid, p_scope text, p_subject text, p_operation text, p_input jsonb)
              returns void language sql security definer set search_path = {schema}, pg_temp
              as $$ insert into {schema}.{AuditTable} (id, at, scope, subject, operation, input)
                    values (p_id, now(), p_scope, p_subject, p_operation, p_input) $$;
            {AppendOnly($"{schema}.{AuditTable}", schema)}
            {Isolated($"{schema}.{AuditTable}", "scope", "text", schema, "select")}
            """;
    }

    /// <summary>
    /// Row-level security on <paramref name="table"/>: an operation sees and writes only rows whose <paramref name="column"/> is
    /// its scope; a platform operation, every row. Forced, so even the table's owner is held to it. <paramref name="schema"/>
    /// is where Levee's functions are. A policy over more than one column is written by hand over <c>levee_scope()</c>.
    /// </summary>
    public static string Isolation(string table, string column, string columnType = "text", string schema = "public") =>
        Isolated(table, column, columnType, schema, "select, insert, update, delete");

    private static string Isolated(string table, string column, string columnType, string schema, string privileges)
    {
        Identifier(schema, nameof(schema));
        return $"""
            alter table {table} enable row level security;
            alter table {table} force row level security;
            create policy levee_scope on {table}
              using ({schema}.levee_platform() or {column} = nullif({schema}.levee_scope(), '')::{columnType})
              with check ({schema}.levee_platform() or {column} = nullif({schema}.levee_scope(), '')::{columnType});
            {Admit(table, schema, privileges)}
            """;
    }

    /// <summary>
    /// Row-level security on a table every scope reads (plans, countries, feature flags), which only a platform operation
    /// writes. Every table the app's context maps has a policy, this one or <see cref="Isolation"/>: one with none is refused
    /// before the first operation, so a forgotten policy fails closed instead of opening the table to every tenant.
    /// </summary>
    public static string Shared(string table, string schema = "public")
    {
        Identifier(schema, nameof(schema));
        return $"""
            alter table {table} enable row level security;
            alter table {table} force row level security;
            create policy levee_shared_read on {table} for select using (true);
            create policy levee_shared_write on {table} for all
              using ({schema}.levee_platform()) with check ({schema}.levee_platform());
            {Admit(table, schema, "select, insert, update, delete")}
            """;
    }

    /// <summary>
    /// The runtime role's grant on a table Levee has just put a policy on, once <see cref="Grants"/> has named the role: a
    /// table is reachable by the app only once row-level security holds it, so one without a policy is never reachable.
    /// </summary>
    private static string Admit(string table, string schema, string privileges) => $"""
        do $levee$
        declare runtime text;
        begin
          if to_regprocedure('{schema}.levee_runtime()') is null then return; end if;
          runtime := {schema}.levee_runtime();
          execute format('grant usage on schema %I to %I', (select n.nspname from pg_class c join pg_namespace n on n.oid = c.relnamespace where c.oid = '{table}'::regclass), runtime);
          execute format('grant {privileges} on %s to %I', '{table}'::regclass, runtime);
          perform {schema}.levee_runtime_sequences('{table}'::regclass);
        end $levee$;
        """;

    /// <summary>
    /// What the app's runtime role may reach, run by the owner: exactly the tables row-level security holds, a Levee policy on
    /// each (<see cref="Isolation"/>, <see cref="Shared"/>), and nothing else. A table without one is unreachable, not
    /// refused later: whatever names it (an unmapped table, raw SQL, another schema) is denied by Postgres. It resets the
    /// role to that list (a grant of the role's from before is revoked) and names the role, so a table isolated afterwards,
    /// by a later migration, is granted as its policy is made. The audit it reads, and adds to only through
    /// <c>levee_audit_write</c>, at the transaction's time. Run before any grant of the app's own (Oxbow's, a table with a
    /// policy of its own), which it would revoke.
    /// </summary>
    public static string Grants(string role, string schema = "public")
    {
        Identifier(role, nameof(role));
        Identifier(schema, nameof(schema));
        return $"""
            revoke all on all tables in schema {schema} from {role};
            revoke all on all sequences in schema {schema} from {role};
            alter default privileges in schema {schema} revoke all on tables from {role};
            alter default privileges in schema {schema} revoke all on sequences from {role};
            grant usage on schema {schema} to {role};
            grant execute on function {schema}.levee_scope(), {schema}.levee_platform() to {role};
            grant execute on function {schema}.levee_audit_write(uuid, text, text, text, jsonb) to {role};
            create or replace function {schema}.levee_runtime() returns text language sql immutable as $$ select '{role}' $$;
            create or replace function {schema}.levee_runtime_sequences(t regclass) returns void language plpgsql as $$
            declare s regclass;
            begin
              for s in select d.objid::regclass from pg_depend d join pg_class c on c.oid = d.objid
                       where c.relkind = 'S' and d.refobjid = t and d.deptype in ('a', 'i') loop
                execute format('grant usage, select on sequence %s to %I', s, {schema}.levee_runtime());
              end loop;
            end $$;
            do $levee$
            declare t regclass;
            begin
              for t in select distinct p.polrelid::regclass from pg_policy p
                       where p.polname in ('levee_scope', 'levee_shared_read', 'levee_shared_write') loop
                execute format('grant usage on schema %I to {role}', (select n.nspname from pg_class c join pg_namespace n on n.oid = c.relnamespace where c.oid = t));
                execute format('grant select, insert, update, delete on %s to {role}', t);
                perform {schema}.levee_runtime_sequences(t);
              end loop;
            end $levee$;
            revoke insert, update, delete on {schema}.{AuditTable} from {role};
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

    /// <summary>In the migration that creates <paramref name="table"/>: see <see cref="LeveeSchema.Shared"/>.</summary>
    public static MigrationBuilder ShareAcrossScopes(this MigrationBuilder migrationBuilder, string table, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(LeveeSchema.Shared(table, schema));
        return migrationBuilder;
    }

    public static MigrationBuilder MakeAppendOnly(this MigrationBuilder migrationBuilder, string table, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(LeveeSchema.AppendOnly(table, schema));
        return migrationBuilder;
    }
}
