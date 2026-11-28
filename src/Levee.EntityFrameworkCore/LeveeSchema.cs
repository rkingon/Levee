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
            alter table {schema}.{AuditTable} enable row level security;
            alter table {schema}.{AuditTable} force row level security;
            -- Read as any isolated table is. Added to with the transaction's own scope, or none for an unscoped operation's
            -- record, which no tenant reads: forced, so the writer function's owner is held to it too.
            create policy levee_scope on {schema}.{AuditTable} for select
              using ({schema}.levee_platform() or scope = nullif({schema}.levee_scope(), ''));
            create policy levee_audit_insert on {schema}.{AuditTable} for insert
              with check ({schema}.levee_platform() or scope is not distinct from nullif({schema}.levee_scope(), ''));
            {Admit($"{schema}.{AuditTable}", schema, "select")}
            {Idempotency(schema)}
            """;
    }

    public const string IdempotencyTable = "levee_idempotency";

    /// <summary>
    /// The keys retried writes are sent with, and the first result each one committed: part of <see cref="Create"/>. An
    /// installation from before alpha.3 runs it alone, as the owner (<see cref="LeveeMigrationBuilderExtensions.AddLeveeIdempotency"/>).
    /// The runtime role reaches it only through its two functions, which read the transaction's own scope, so a key is
    /// claimed, and a result replayed, only in the scope and for the subject that sent it.
    /// </summary>
    public static string Idempotency(string schema = "public")
    {
        Identifier(schema, nameof(schema));
        return $"""
            create table {schema}.{IdempotencyTable} (
              scope text not null,
              subject text not null,
              operation text not null,
              key text not null,
              input_hash bytea not null,
              result jsonb,
              kept boolean not null default false,
              expires_at timestamptz not null,
              primary key (scope, subject, operation, key)
            );
            create index {IdempotencyTable}_expires_at on {schema}.{IdempotencyTable} (expires_at);
            alter table {schema}.{IdempotencyTable} enable row level security;
            alter table {schema}.{IdempotencyTable} force row level security;
            create policy levee_idempotency_scope on {schema}.{IdempotencyTable}
              using ({schema}.levee_platform() or scope = {schema}.levee_scope())
              with check (scope = {schema}.levee_scope());
            -- Claims the key for this transaction, or says what an earlier request with it committed. An equal claim still in
            -- flight is waited for: if it commits, this one reads its row; if it rolls back, this one takes the key.
            create function {schema}.levee_idempotency_claim(p_subject text, p_operation text, p_key text, p_hash bytea, p_lifetime interval)
              returns jsonb language plpgsql security definer set search_path = {schema}, pg_temp
              as $$
              declare v_scope text := {schema}.levee_scope();
              declare r record;
              begin
                delete from {schema}.{IdempotencyTable} i
                  where i.scope = v_scope and i.subject = p_subject and i.operation = p_operation and i.key = p_key and i.expires_at <= now();
                insert into {schema}.{IdempotencyTable} (scope, subject, operation, key, input_hash, expires_at)
                  values (v_scope, p_subject, p_operation, p_key, p_hash, now() + p_lifetime)
                  on conflict do nothing;
                if found then
                  return jsonb_build_object('state', 'claimed');
                end if;
                select i.input_hash, i.result, i.kept into r from {schema}.{IdempotencyTable} i
                  where i.scope = v_scope and i.subject = p_subject and i.operation = p_operation and i.key = p_key;
                return case when r.input_hash <> p_hash then jsonb_build_object('state', 'mismatch')
                            when not r.kept then jsonb_build_object('state', 'replayed')
                            else jsonb_build_object('state', 'kept', 'result', r.result) end;
              end $$;
            -- The result the claimed key's request commits with; null when it isn't kept (it carries a secret, shown once).
            create function {schema}.levee_idempotency_complete(p_subject text, p_operation text, p_key text, p_result jsonb)
              returns void language sql security definer set search_path = {schema}, pg_temp
              as $$ update {schema}.{IdempotencyTable} set result = p_result, kept = p_result is not null
                    where scope = {schema}.levee_scope() and subject = p_subject and operation = p_operation and key = p_key $$;
            revoke all on function {schema}.levee_idempotency_claim(text, text, text, bytea, interval) from public;
            revoke all on function {schema}.levee_idempotency_complete(text, text, text, jsonb) from public;
            do $levee$
            begin
              if to_regprocedure('{schema}.levee_runtime()') is not null then
                execute format('grant execute on function {schema}.levee_idempotency_claim(text, text, text, bytea, interval), '
                  || '{schema}.levee_idempotency_complete(text, text, text, jsonb) to %I', {schema}.levee_runtime());
              end if;
            end $levee$;
            """;
    }

    /// <summary>
    /// Removes the keys whose lifetime has passed, every scope's: run by the owner, on a schedule of the app's own. A key
    /// past its lifetime is claimed afresh whether or not this has run; this only keeps the table small.
    /// </summary>
    public static string SweepExpiredKeys(string schema = "public")
    {
        Identifier(schema, nameof(schema));
        return $"""
            do $levee$
            begin
              perform set_config('levee.platform', 'on', true);
              delete from {schema}.{IdempotencyTable} where expires_at <= now();
            end $levee$;
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
    /// What the app's runtime role may reach, run by the owner: exactly the tables row-level security holds, a policy of this
    /// installation's on each (<see cref="Isolation"/>, <see cref="Shared"/>), and nothing else. A table without one is unreachable, not
    /// refused later: whatever names it (an unmapped table, raw SQL, another schema) is denied by Postgres. It resets the
    /// role to that list (a grant of the role's from before, in any schema, is revoked) and names the role, so a table isolated afterwards,
    /// by a later migration, is granted as its policy is made. The audit it reads, and adds to only through
    /// <c>levee_audit_write</c>, at the transaction's time. Run before any grant of the app's own (Oxbow's, a table with a
    /// policy of its own), which it would revoke.
    /// </summary>
    public static string Grants(string role, string schema = "public") => Grants(role, schema, then: null);

    /// <param name="then">The grants of a package beside Levee (Oxbow's queue), run last in the same statement, after the
    /// allow-list is checked: both hold or neither does, standalone or inside a caller's transaction.</param>
    internal static string Grants(string role, string schema, string? then)
    {
        Identifier(role, nameof(role));
        Identifier(schema, nameof(schema));
        if (then?.Contains("$levee_then$", StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("The grants to run after Levee's can't contain $levee_then$.", nameof(then));
        }
        // One statement, so all or nothing under any client: a refusal leaves the role, and its registration, as they were.
        return $"""
            do $levee$
            declare r record;
            declare t regclass;
            declare residue text;
            declare reached text;
            declare belongs text;
            begin
              -- A role it belongs to lends it everything that role holds, a predefined one (pg_read_all_data) included, none of it
              -- in any ACL: the runtime role belongs to none.
              select string_agg(granted.rolname, ', ') into belongs from pg_auth_members m join pg_roles granted on granted.oid = m.roleid
              where m.member = '{role}'::regrole;
              if belongs is not null then
                raise exception '{role} belongs to %, and holds whatever that holds', belongs
                  using hint = 'Revoke each from {role}: the runtime role holds Levee''s grants and nothing else. Then run Levee''s grants again.';
              end if;
              -- Exactly what the role holds, wherever it is: by table or column, and the defaults that would grant it more.
              for r in select distinct held.rel from (
                         select c.oid::regclass as rel, (aclexplode(c.relacl)).grantee from pg_class c where c.relacl is not null
                         union all
                         select a.attrelid::regclass, (aclexplode(a.attacl)).grantee from pg_attribute a where a.attacl is not null
                       ) held where held.grantee = '{role}'::regrole loop
                begin
                  execute format('revoke all on %s from {role}', r.rel);
                exception when insufficient_privilege then null;
                end;
              end loop;
              for r in select distinct o.rolname as grantor, n.nspname, d.defaclobjtype from pg_default_acl d
                       join pg_roles o on o.oid = d.defaclrole
                       left join pg_namespace n on n.oid = d.defaclnamespace
                       cross join lateral aclexplode(d.defaclacl) g
                       where g.grantee = '{role}'::regrole and pg_has_role(d.defaclrole, 'member') loop
                execute format('alter default privileges for role %I %s revoke all on %s from {role}', r.grantor,
                  case when r.nspname is null then '' else format('in schema %I', r.nspname) end,
                  case r.defaclobjtype when 'r' then 'tables' when 'S' then 'sequences' when 'f' then 'functions' when 'T' then 'types' else 'schemas' end);
              end loop;
              -- What another role granted, this one can't take back: named, so whoever granted it does.
              select string_agg(what, ', ') into residue from (
                select distinct held.rel::text || ' (granted by ' || (held.acl).grantor::regrole || ')' as what from (
                  select c.oid::regclass as rel, aclexplode(c.relacl) as acl from pg_class c where c.relacl is not null
                  union all
                  select a.attrelid::regclass, aclexplode(a.attacl) from pg_attribute a where a.attacl is not null
                ) held where (held.acl).grantee = '{role}'::regrole
                union
                select distinct 'default privileges of ' || d.defaclrole::regrole from pg_default_acl d cross join lateral aclexplode(d.defaclacl) g
                where g.grantee = '{role}'::regrole) held;
              if residue is not null then
                raise exception 'Levee can''t reset {role} to its allow-list: it still holds %', residue
                  using hint = 'Run, as the role that granted each: revoke all on <table> from {role}; or alter default privileges revoke ... from {role}. Then run Levee''s grants again.';
              end if;
              execute 'grant usage on schema {schema} to {role}';
              execute 'grant execute on function {schema}.levee_scope(), {schema}.levee_platform() to {role}';
              execute 'grant execute on function {schema}.levee_audit_write(uuid, text, text, text, jsonb) to {role}';
              -- An installation from before alpha.3 has no idempotency keys until the owner adds them (LeveeSchema.Idempotency).
              if to_regprocedure('{schema}.levee_idempotency_claim(text, text, text, bytea, interval)') is not null then
                execute 'grant execute on function {schema}.levee_idempotency_claim(text, text, text, bytea, interval), '
                  || '{schema}.levee_idempotency_complete(text, text, text, jsonb) to {role}';
              end if;
              execute $ddl$create or replace function {schema}.levee_runtime() returns text language sql immutable as $body$ select '{role}' $body$$ddl$;
              execute $ddl$create or replace function {schema}.levee_runtime_sequences(t regclass) returns void language plpgsql as $body$
                declare s regclass;
                begin
                  for s in select d.objid::regclass from pg_depend d join pg_class c on c.oid = d.objid
                           where c.relkind = 'S' and d.refobjid = t and d.deptype in ('a', 'i') loop
                    execute format('grant usage, select on sequence %s to %I', s, {schema}.levee_runtime());
                  end loop;
                end $body$$ddl$;
              -- This installation's policies only: each reads this schema's functions, so another installation's tables (and its
              -- audit) stay its own role's.
              for t in select distinct p.polrelid::regclass from pg_policy p
                       join pg_depend d on d.classid = 'pg_policy'::regclass and d.objid = p.oid and d.refclassid = 'pg_proc'::regclass
                       where p.polname in ('levee_scope', 'levee_shared_read', 'levee_shared_write')
                         and d.refobjid in ('{schema}.levee_scope()'::regprocedure, '{schema}.levee_platform()'::regprocedure) loop
                execute format('grant usage on schema %I to {role}', (select n.nspname from pg_class c join pg_namespace n on n.oid = c.relnamespace where c.oid = t));
                execute format(case when t = '{schema}.{AuditTable}'::regclass then 'grant select on %s to {role}'
                                    else 'grant select, insert, update, delete on %s to {role}' end, t);
                perform {schema}.levee_runtime_sequences(t);
              end loop;
              -- What the role holds in effect, through itself, a role it belongs to, PUBLIC or ownership, against what each
              -- relation allows: an allow-list over the ACLs, so a privilege a later Postgres adds is refused, not missed.
              with held as (
                select c.oid, x.grantee, x.privilege_type from pg_class c cross join lateral aclexplode(c.relacl) x where c.relacl is not null
                union all
                select a.attrelid, x.grantee, x.privilege_type from pg_attribute a cross join lateral aclexplode(a.attacl) x where a.attacl is not null
              ), ours as (
                select distinct p.polrelid as oid from pg_policy p
                join pg_depend d on d.classid = 'pg_policy'::regclass and d.objid = p.oid and d.refclassid = 'pg_proc'::regclass
                where p.polname in ('levee_scope', 'levee_shared_read', 'levee_shared_write')
                  and d.refobjid in ('{schema}.levee_scope()'::regprocedure, '{schema}.levee_platform()'::regprocedure)
              )
              select string_agg(distinct c.oid::regclass::text, ', ') into reached from pg_class c
              join pg_namespace s on s.oid = c.relnamespace
              where c.relkind in ('r', 'p', 'v', 'm', 'f') and s.nspname <> 'information_schema' and s.nspname not like 'pg\_%'
                -- An extension's own relations (PostGIS's spatial_ref_sys, granted to PUBLIC) are the extension's, not the app's.
                and not exists (select 1 from pg_depend e where e.classid = 'pg_class'::regclass and e.objid = c.oid and e.deptype = 'e')
                and (pg_has_role('{role}', c.relowner, 'member')
                  or exists (select 1 from held h
                    where h.oid = c.oid and (h.grantee = 0 or pg_has_role('{role}', h.grantee, 'member'))
                      and h.privilege_type <> all (case
                        -- Its audit: read, and added to through levee_audit_write alone.
                        when c.oid = '{schema}.{AuditTable}'::regclass then array['SELECT']
                        -- Its own tables: what row-level security holds (truncate, say, empties every scope's rows).
                        when c.oid in (select oid from ours) then array['SELECT', 'INSERT', 'UPDATE', 'DELETE']
                        -- Another's: nothing.
                        else array[]::text[] end)));
              if reached is not null then
                raise exception '{role} reaches more than this installation''s tables allow: %', reached
                  using hint = 'It reaches them through a role it belongs to, or PUBLIC: revoke that role from {role} (or the grant from it). Then run Levee''s grants again.';
              end if;
            {(then is null ? "" : $"  execute $levee_then${then}$levee_then$;")}
            end $levee$;
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

    /// <summary>For an installation from before alpha.3: <see cref="LeveeSchema.Idempotency"/>.</summary>
    public static MigrationBuilder AddLeveeIdempotency(this MigrationBuilder migrationBuilder, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(LeveeSchema.Idempotency(schema));
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
