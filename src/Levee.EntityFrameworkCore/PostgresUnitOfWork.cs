using System.Reflection;
using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Levee.EntityFrameworkCore;

public static class LeveePostgresServiceCollectionExtensions
{
    /// <summary>
    /// Each operation's unit of work is the scoped <typeparamref name="TContext"/> and a transaction on it: read-only if the
    /// operation declares only reads, with its scope in <c>levee.scope</c> and <c>levee.platform</c> for row-level security,
    /// both local to the transaction. The context must connect as a role that owns nothing and doesn't bypass RLS.
    /// </summary>
    public static IServiceCollection AddLeveePostgres<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IUnitOfWorkFactory, PostgresUnitOfWorkFactory<TContext>>();
        services.AddScoped<IFlush, EfFlush<TContext>>();
        services.AddScoped<AmbientTransaction>();
        services.AddScoped<IAmbientTransaction>(provider => provider.GetRequiredService<AmbientTransaction>());
        services.AddSingleton<SignalHub<TContext>>();
        services.AddScoped<IChangeSignals, PostgresSignals<TContext>>();
        services.ConfigureDbContext<TContext>(o => o.AddInterceptors(OutsideOperationGuard.Instance, Aggregates.Instance));
        // The guard is on TContext alone: a second context would reach the database around every operation.
        services.AddSingleton<IValidateOptions<OneContext>>(provider =>
            new OneContextValidation(services, typeof(TContext), provider.GetRequiredService<IServiceScopeFactory>()));
        services.AddOptions<OneContext>().ValidateOnStart();
        return services;
    }
}

/// <summary>
/// A DbContext of a separate bounded context: a generic subdomain the app doesn't model (identity: OpenIddict's stores),
/// its tables apart from the app's, used by the library it serves and never by the app's own code (LEVEE018). Levee's
/// unit of work, guard and row-level security check are the app's context's alone; this one is outside every operation.
/// It names the library it serves, and maps that library's entities alone (or the app's subclasses of them): startup
/// refuses it if it maps anything else, or a table the app's context maps, since the app's own data is the app's context's.
/// </summary>
/// <param name="library">The library whose store it is, as its assemblies are named: <c>OpenIddict</c>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SeparateContextAttribute(string library) : Attribute
{
    public string Library { get; } = library;
}

public static class LeveeSeparateContextServiceCollectionExtensions
{
    /// <summary>
    /// The one way a separate bounded context's store (<see cref="SeparateContextAttribute"/>) is registered: as itself, by
    /// <paramref name="configure"/>, connecting as a role of its own. Postgres keeps it apart from the app's data: the app's
    /// runtime role is granted only the tables Levee's policies hold, and this role none of them.
    /// </summary>
    public static IServiceCollection AddLeveeSeparateContext<TContext>(this IServiceCollection services, Action<DbContextOptionsBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!typeof(TContext).IsDefined(typeof(SeparateContextAttribute), inherit: false))
        {
            throw new InvalidOperationException($"{typeof(TContext).Name} isn't marked [SeparateContext(library)]: only a separate bounded context's store is registered this way.");
        }
        services.AddDbContext<TContext>(configure);
        services.AddSingleton(new SeparateContextRegistration(typeof(TContext)));
        return services;
    }
}

internal sealed record SeparateContextRegistration(Type Context);

/// <summary>
/// Work as the database's owner, outside any operation: a Tool's migrations and role bootstrap. Everything else reaches the
/// database only inside an operation's unit of work (LEVEE018 holds this to Tools and tests).
/// </summary>
public static class LeveeOwner
{
    // A count, not a flag: runs may nest and end in any order, and owner work lasts until the last has ended.
    private static readonly AsyncLocal<StrongBox<int>> Depth = new();

    internal static bool IsActive => Depth.Value is { Value: > 0 };

    /// <summary>Lets the context run commands outside an operation until disposed.</summary>
    public static IDisposable Run()
    {
        var depth = Depth.Value ??= new StrongBox<int>();
        depth.Value++;
        return new Scope(depth);
    }

    private sealed class Scope(StrongBox<int> depth) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                depth.Value--;
            }
        }
    }
}

internal sealed class OneContext;

internal sealed class OneContextValidation(IServiceCollection services, Type context, IServiceScopeFactory scopes) : IValidateOptions<OneContext>
{
    public ValidateOptionsResult Validate(string? name, OneContext options)
    {
        // By its options (AddDbContext, AddDbContextFactory), as itself or behind an interface (configured in OnConfiguring).
        var registered = services
            .SelectMany(d => Contexts(d).Select(t => (Context: t, Descriptor: d)))
            .Where(r => r.Context != context)
            .ToList();
        var sanctioned = services.Select(d => d.ImplementationInstance).OfType<SeparateContextRegistration>().Select(r => r.Context).ToHashSet();
        // A separate store registered any way but its own (keyed, behind an interface, through a factory) could connect as
        // anyone: it's one more context, refused as one.
        var others = registered
            .Where(r => !sanctioned.Contains(r.Context) || r.Descriptor.IsKeyedService || !Own(r.Descriptor, r.Context))
            .Select(r => r.Context)
            .Distinct()
            .ToList();
        if (others.Count > 0)
        {
            return ValidateOptionsResult.Fail(
                $"{string.Join(", ", others.Select(t => t.Name))} is a DbContext besides {context.Name}, Levee's unit of work: it reaches the database outside "
                + $"every operation's transaction. Map its entities in {context.Name}; a Tool's owner work builds its own context; "
                + "a separate bounded context the app doesn't model (identity) is marked [SeparateContext(library)] and registered with "
                + "AddLeveeSeparateContext<T>, and nothing else.");
        }
        // Resolved once at start, so a second registration of the same context would be an instance nothing here checked.
        if (sanctioned.Append(context).FirstOrDefault(t => services.Count(d => d.ServiceType == t) > 1) is { } twice)
        {
            return ValidateOptionsResult.Fail(
                $"{twice.Name} is registered more than once, so it resolves as more than one context and only one is checked. "
                + $"Fix: register it once, with {(twice == context ? "AddDbContext" : "AddLeveeSeparateContext")}<{twice.Name}>, and nothing else.");
        }
        using var scope = scopes.CreateScope();
        var app = (DbContext)scope.ServiceProvider.GetRequiredService(context);
        if (Unfit(app) is { } problem)
        {
            return ValidateOptionsResult.Fail(problem);
        }
        var appRole = Role(app);
        foreach (var type in sanctioned)
        {
            var other = (DbContext)scope.ServiceProvider.GetRequiredService(type);
            var library = type.GetCustomAttribute<SeparateContextAttribute>()!.Library;
            if (other.Model.GetEntityTypes().FirstOrDefault(e => !e.HasSharedClrType && !Of(e.ClrType, library)) is { } foreign)
            {
                return ValidateOptionsResult.Fail(foreign.ClrType.Assembly.IsDefined(typeof(LeveeRoleAttribute))
                    ? $"{type.Name} is {library}'s store ([SeparateContext]), but maps {foreign.ClrType.Name}, the app's own (its assembly is built with Levee): "
                        + $"that would be the app's data outside every operation. Fix: map it in {context.Name}; a separate context holds a library's entities alone, "
                        + "and names that library, not the app."
                    : $"{type.Name} is {library}'s store ([SeparateContext]), but maps {foreign.ClrType.Name}, which isn't {library}'s: that would be "
                        + $"the app's own data outside every operation. Fix: map it in {context.Name}; a separate context holds its library's entities alone.");
            }
            if (Role(other) is { } role && string.Equals(role, appRole, StringComparison.Ordinal))
            {
                return ValidateOptionsResult.Fail(
                    $"{type.Name} connects as {role}, the app's own role, so nothing in the database keeps the two apart. Fix: connect it as a role of its own, "
                    + "granted only its library's schema; the app's role is granted only the tables Levee's policies hold.");
            }
        }
        return ValidateOptionsResult.Success;
    }

    /// <summary>The role a context connects as, from its connection's settings: nothing is sent to find out.</summary>
    private static string? Role(DbContext db) => (db.Database.GetDbConnection() as Npgsql.NpgsqlConnection)?.UserName;

    /// <summary>A registration AddDbContext makes for the context itself.</summary>
    private static bool Own(ServiceDescriptor d, Type context) =>
        d.ServiceType == context && (d.ImplementationType is null || d.ImplementationType == context)
        || d.ServiceType.IsGenericType && d.ServiceType.GenericTypeArguments is [var argument] && argument == context
            && d.ServiceType.GetGenericTypeDefinition() != typeof(IDbContextFactory<>);

    /// <summary>The contexts a registration provides: itself, its implementation, or the context its options or factory are for.</summary>
    private static IEnumerable<Type> Contexts(ServiceDescriptor d) =>
        new[] { d.ServiceType, d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType,
                (d.IsKeyedService ? d.KeyedImplementationInstance : d.ImplementationInstance)?.GetType() }
            .OfType<Type>()
            // EF's own generic registrations name the context they're for; a library's (a store generic over it) doesn't make one.
            .SelectMany(t => t.IsGenericType && t.Assembly == typeof(DbContext).Assembly ? t.GenericTypeArguments.Prepend(t) : [t])
            .Where(t => typeof(DbContext).IsAssignableFrom(t))
            .Distinct();

    /// <summary>
    /// The library's own type, or the app's subclass of one: its assembly named for the library. An assembly built with Levee
    /// (it carries a role) is the app's, never a library's, whatever the attribute names.
    /// </summary>
    private static bool Of(Type type, string library)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            var assembly = t.Assembly.GetName().Name ?? "";
            if ((assembly == library || assembly.StartsWith(library + ".", StringComparison.Ordinal)) && !t.Assembly.IsDefined(typeof(LeveeRoleAttribute)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>What the context's own configuration would undo: the guard, or a transaction the caller begins.</summary>
    public static string? Unfit(DbContext db)
    {
        var name = db.GetType().Name;
        var interceptors = db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors;
        if (interceptors?.Contains(OutsideOperationGuard.Instance) != true || !interceptors.Contains(Aggregates.Instance))
        {
            return $"{name} doesn't carry Levee's guard, so it would run commands outside every operation. Fix: take "
                + $"DbContextOptions<{name}> in its constructor and configure it in AddDbContext, not OnConfiguring.";
        }
        if (AggregateMap.Of(db.Model).Problem is { } aggregates)
        {
            return $"{name}: {aggregates}";
        }
        return db.Database.CreateExecutionStrategy().RetriesOnFailure
            ? $"{name} retries on failure (EnableRetryOnFailure), which can't run a transaction the caller began. Fix: remove it; a "
                + "transient failure answers Unavailable, which a client may repeat and a job retries."
            : null;
    }
}

/// <summary>
/// The transactions Levee's units of work hold open: a command in any other (EF's own around SaveChanges) is outside every
/// operation. Each leaves as its unit of work ends, since Npgsql hands the same transaction object to the connection's next.
/// </summary>
internal static class LeveeTransactions
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Data.Common.DbTransaction, object> Began = [];

    public static void Add(System.Data.Common.DbTransaction transaction) => Began.AddOrUpdate(transaction, Began);

    public static void Remove(System.Data.Common.DbTransaction transaction) => Began.Remove(transaction);

    public static bool Contains(System.Data.Common.DbTransaction? transaction) => transaction is not null && Began.TryGetValue(transaction, out _);
}

/// <summary>
/// Refuses a command run outside a transaction the unit of work began: on the app's context, that's outside every operation (a
/// seeder, a context a Host resolved, SaveChanges in EF's own transaction), so not authorized, not scoped, not read-only when it
/// should be, not audited, and committed on its own.
/// </summary>
internal sealed class OutsideOperationGuard : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    public static readonly OutsideOperationGuard Instance = new();

    private static void Check(System.Data.Common.DbCommand command)
    {
        if (!LeveeTransactions.Contains(command.Transaction) && !LeveeOwner.IsActive && !IsProbe(command))
        {
            throw new InvalidOperationException(
                $"The app's DbContext ran a command outside an operation's unit of work ({command.CommandText.Split('\n')[0]}). Send an operation through the Dispatcher; "
                + "a Tool's owner work runs inside LeveeOwner.Run().");
        }
    }

    /// <summary>EF's connection probe (CanConnect, a health check): it reads nothing and can change nothing.</summary>
    private static bool IsProbe(System.Data.Common.DbCommand command) =>
        command.Transaction is null && command.CommandText.Trim().Equals("SELECT 1", StringComparison.OrdinalIgnoreCase) && command.Parameters.Count == 0;

    public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
        System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> NonQueryExecuting(
        System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
        System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> ScalarExecuting(
        System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object>> ScalarExecutingAsync(
        System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }
}

internal sealed class EfFlush<TContext>(TContext db) : IFlush
    where TContext : DbContext
{
    public Task FlushAsync(CancellationToken ct) =>
        db.Database.CurrentTransaction is null
            ? throw new InvalidOperationException("A flush outside an operation's unit of work would commit on its own; flush only inside an operation.")
            : Aggregates.SaveAsync(db, ct);
}

internal sealed class AmbientTransaction : IAmbientTransaction
{
    public (System.Data.Common.DbConnection Connection, System.Data.Common.DbTransaction Transaction)? Current { get; set; }
}

internal sealed class PostgresUnitOfWorkFactory<TContext>(TContext db, AmbientTransaction ambient, IOptions<OneContext> one,
    IEnumerable<OwnedRelations> owned) : IUnitOfWorkFactory
    where TContext : DbContext
{
    // Outside a host nothing validates on start: the first operation does.
    private readonly OneContext _one = one.Value;

    // Once a process per model and target (server, database, role, search path): a table's policy comes with its migration, which runs
    // before the app does, and the same context type may reach another database or schema by its options.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Microsoft.EntityFrameworkCore.Metadata.IModel, string), byte> Isolated = new();

    /// <summary>
    /// Row-level security holds everything the context reads and writes: a migration that forgot a policy, or a view that reads
    /// around one, would otherwise open its rows to every tenant, since the runtime role is granted every table.
    /// </summary>
    private async Task VerifyIsolationAsync(CancellationToken ct)
    {
        // Every relation the model reads or writes, however it's mapped (split, as a view, through the search path), every one
        // in any schema the runtime role may read or write at all (raw SQL reaches by name what the model doesn't map), and
        // every relation a view among them reads, down to the tables. The tables a Levee package installs (Oxbow's queue,
        // named one by one) and EF's migration history are their owners', not the app's.
        // Each is judged by what it is, not by how EF maps it:
        // - a table: forced row-level security and a policy;
        // - a view: security_invoker, or an owner row-level security holds (neither a superuser nor bypassing it);
        // - a materialized view or a foreign table: refused, since row-level security can't hold either.
        // One that doesn't resolve can't be judged, so it fails too.
        static string Quote(string name) => $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        var model = db.Model.GetRelationalModel();
        var names = model.Tables.Select(t => (t.Schema, t.Name)).Concat(model.Views.Select(v => (v.Schema, v.Name)))
            .Select(r => r.Schema is { } schema ? $"{Quote(schema)}.{Quote(r.Name)}" : Quote(r.Name))
            .Distinct()
            .ToArray();
        // Row-level security binds neither a superuser nor a role that bypasses it, and a table's owner can switch it off: the
        // app's role is none of these, or every policy below holds nothing.
        var unbound = await db.Database.SqlQuery<string>($"""
            select current_user || case when r.rolsuper then ' is a superuser' else ' bypasses row-level security' end as "Value"
            from pg_catalog.pg_roles r where r.rolname = current_user and (r.rolsuper or r.rolbypassrls)
            union all
            select current_user || ' owns ' || c.oid::regclass::text
            from pg_catalog.pg_class c join pg_catalog.pg_namespace s on s.oid = c.relnamespace
            where c.relrowsecurity and s.nspname <> 'information_schema' and s.nspname not like 'pg\_%'
              and pg_catalog.pg_has_role(c.relowner, 'member')
            """).ToListAsync(ct);
        if (unbound.Count > 0)
        {
            throw new InvalidOperationException(
                $"Row-level security doesn't bind the role the app connects as: {string.Join("; ", unbound.Order(StringComparer.Ordinal))}, so every tenant's "
                + "operations could see every row. Fix: connect as a role that is no superuser, doesn't bypass row-level security and owns nothing, "
                + "granted only LeveeSchema.Grants(role); the owner (or a superuser) runs migrations and grants, never the app.");
        }
        var theirs = owned.SelectMany(o => o.Names).ToArray();
        var open = await db.Database.SqlQuery<string>($"""
            with recursive mapped as (
              select n, to_regclass(n)::oid as oid from unnest({names}) n
              union
              select c.oid::regclass::text, c.oid
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace s on s.oid = c.relnamespace
              where c.relkind in ('r', 'p', 'v', 'm', 'f')
                and s.nspname <> 'information_schema' and s.nspname not like 'pg\_%' and has_schema_privilege(s.oid, 'usage')
                and not (s.nspname || '.' || c.relname = any ({theirs})) and c.relname <> '__EFMigrationsHistory'
                and not exists (select 1 from pg_catalog.pg_depend e where e.classid = 'pg_catalog.pg_class'::regclass and e.objid = c.oid and e.deptype = 'e')
                and (has_table_privilege(c.oid, 'select') or has_table_privilege(c.oid, 'insert')
                  or has_table_privilege(c.oid, 'update') or has_table_privilege(c.oid, 'delete')
                  or has_any_column_privilege(c.oid, 'select') or has_any_column_privilege(c.oid, 'insert')
                  or has_any_column_privilege(c.oid, 'update'))
            ), reached(oid) as (
              select oid from mapped where oid is not null
              union
              select d.refobjid
              from reached
              join pg_catalog.pg_rewrite w on w.ev_class = reached.oid
              join pg_catalog.pg_depend d on d.classid = 'pg_catalog.pg_rewrite'::regclass and d.objid = w.oid
                and d.refclassid = 'pg_catalog.pg_class'::regclass and d.refobjid <> reached.oid
            )
            select n || ': doesn''t exist yet' as "Value" from mapped where oid is null
            union all
            select c.oid::regclass::text || case c.relkind
                when 'v' then ': a view that reads with its owner''s rights, and its owner is a superuser or bypasses row-level security'
                when 'm' then ': a materialized view, which row-level security can''t hold'
                when 'f' then ': a foreign table, which row-level security can''t hold'
                else ': no forced row-level security with a policy' end
            from reached
            join pg_catalog.pg_class c on c.oid = reached.oid
            left join pg_catalog.pg_roles r on r.oid = c.relowner
            where (c.relkind in ('r', 'p')
                and not (c.relrowsecurity and c.relforcerowsecurity and exists (select 1 from pg_catalog.pg_policy p where p.polrelid = c.oid)))
              or (c.relkind = 'v'
                and not exists (select 1 from pg_catalog.pg_options_to_table(c.reloptions) o
                                where o.option_name = 'security_invoker' and o.option_value::boolean)
                and (r.rolsuper or r.rolbypassrls))
              or c.relkind in ('m', 'f')
            """).ToListAsync(ct);
        if (open.Count > 0)
        {
            throw new InvalidOperationException(
                $"Row-level security doesn't hold what the app's role can read and write, so every tenant's operations would see all of it: "
                + $"{string.Join("; ", open.Select(t => t.Replace("\"", "", StringComparison.Ordinal)).Order(StringComparer.Ordinal))}. Fix: in the migration "
                + "that creates a table, EnableScopeIsolation(table, column) for one scope's rows, or ShareAcrossScopes(table) for rows every "
                + "scope reads and only platform operations write; create a view with (security_invoker = true); read a materialized view's "
                + "data through a table instead.");
        }
    }

    private void Verify()
    {
        if (OneContextValidation.Unfit(db) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
    }

    public Error? Classify(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case PostgresException pg:
                    return pg.SqlState switch
                    {
                        PostgresErrorCodes.ReadOnlySqlTransaction => Error.Forbidden(LeveeErrors.UndeclaredWrite, "It tried to write, but its contract declares only reads."),
                        PostgresErrorCodes.UniqueViolation => Error.Conflict(LeveeErrors.Conflict, $"That already exists ({pg.ConstraintName})."),
                        PostgresErrorCodes.InsufficientPrivilege when pg.MessageText.Contains("row-level security", StringComparison.Ordinal) =>
                            Error.Forbidden(LeveeErrors.ScopeViolation, "It tried to write outside its scope."),
                        // Any other integrity rule, or a trigger's refusal, at a statement or at COMMIT (deferred): not transient.
                        _ when pg.SqlState.StartsWith("23", StringComparison.Ordinal) || pg.SqlState == PostgresErrorCodes.RaiseException =>
                            Error.Conflict(LeveeErrors.ConstraintViolation, $"The database refused it ({pg.ConstraintName ?? pg.MessageText})."),
                        // A value its column's type refuses, such as a scope key that isn't the policy's uuid. The rest of class 22
                        // (division by zero, a bad regex) is the SQL's own bug.
                        "22P02" or "22003" or "22001" or "22007" or "22008" or "22021" or "22P05" =>
                            Error.Validation(LeveeErrors.InvalidValue, $"The database refused a value: {pg.MessageText}."),
                        PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected =>
                            Error.Unavailable(LeveeErrors.Contention, "It collided with another change; try again."),
                        _ when pg.IsTransient => Error.Unavailable(LeveeErrors.StoreUnavailable, "The database is busy; try again."),
                        _ => null,
                    };
                case DbUpdateConcurrencyException:
                    return Error.Conflict(LeveeErrors.Stale, "There was no row to change: another write changed it first, or it isn't this scope's.");
                // Npgsql's own timeouts come wrapped in it; a bare timeout is the handler's dependency's, not the store's.
                case NpgsqlException { IsTransient: true }:
                    return Error.Unavailable(LeveeErrors.StoreUnavailable, "The database couldn't be reached; try again.");
            }
        }
        return null;
    }

    public async Task<IUnitOfWork> BeginAsync(WorkScope scope, CancellationToken ct)
    {
        Verify();
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException($"{typeof(TContext).Name} already has a transaction: an operation's unit of work opens its own.");
        }
        var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        LeveeTransactions.Add(tx.GetDbTransaction());
        try
        {
            if (scope.ReadOnly)
            {
                await db.Database.ExecuteSqlRawAsync("set transaction read only", ct);
            }
            var key = scope.Key ?? "";
            var platform = scope.Platform ? "on" : "off";
            // The same round trip says where the transaction resolves names: the check below holds for that target alone. The
            // server is named by its address and its start, since two clusters may share every name in it.
            var target = await db.Database.SqlQuery<string>($"""
                select concat_ws(':', inet_server_addr(), inet_server_port(), pg_postmaster_start_time(), current_database(), current_user,
                  current_setting('search_path')) as "Value"
                from (select set_config('levee.scope', {key}, true), set_config('levee.platform', {platform}, true)) as scoped
                """).SingleAsync(ct);
            if (!Isolated.ContainsKey((db.Model, target)))
            {
                await VerifyIsolationAsync(ct);
                Isolated.TryAdd((db.Model, target), 0);
            }
            ambient.Current = (db.Database.GetDbConnection(), tx.GetDbTransaction());
            Aggregates.Begin(db);
            return new PostgresUnitOfWork(db, tx, scope, ambient);
        }
        catch
        {
            LeveeTransactions.Remove(tx.GetDbTransaction());
            await tx.DisposeAsync();
            throw;
        }
    }
}

internal sealed class PostgresUnitOfWork(DbContext db, IDbContextTransaction tx, WorkScope scope, AmbientTransaction ambient) : IUnitOfWork
{
    private bool _committed;

    public WorkScope Scope => scope;

    public void Expect(uint version) => Aggregates.Expect(db, version);

    private string Schema => db.Model.FindEntityType(typeof(LeveeAuditRecord))?.GetSchema() ?? "public";

    public async Task<IdempotencyClaim> ClaimAsync(IdempotencyKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        var answer = await db.Database.SqlQuery<string>(System.Runtime.CompilerServices.FormattableStringFactory.Create(
            $"select {Schema}.levee_idempotency_claim({{0}}, {{1}}, {{2}}, {{3}})::text as \"Value\"",
            key.Subject, key.Operation, key.Key, key.InputHash)).SingleAsync(ct);
        using var claim = System.Text.Json.JsonDocument.Parse(answer);
        return claim.RootElement.GetProperty("state").GetString() switch
        {
            "claimed" => new IdempotencyClaim.Claimed(),
            "mismatch" => new IdempotencyClaim.Mismatch(),
            "replayed" => new IdempotencyClaim.Replayed(),
            "kept" => new IdempotencyClaim.Kept(claim.RootElement.GetProperty("result").GetString()!),
            var state => throw new InvalidOperationException($"levee_idempotency_claim answered {state}, which isn't a claim's state."),
        };
    }

    private (IdempotencyKey Key, string? Result)? _kept;

    public void Keep(IdempotencyKey key, string? result) => _kept = (key, result);

    /// <summary>Through <c>levee_audit_write</c>, the one way the runtime role may add to the audit, in this transaction, at its time.</summary>
    public async Task AuditAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var schema = Schema;
        await db.Database.ExecuteSqlAsync(System.Runtime.CompilerServices.FormattableStringFactory.Create(
            $"select {schema}.levee_audit_write({{0}}, {{1}}, {{2}}, {{3}}, {{4}}::jsonb)",
            Guid.CreateVersion7(entry.At), entry.Scope, entry.Subject, entry.Operation, entry.Input), ct);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        await Aggregates.SaveAsync(db, ct);
        Aggregates.Verify(db);
        // Last before COMMIT, so a key's lifetime starts as its write lands, not as its handler returned.
        if (_kept is var (key, result))
        {
            await db.Database.ExecuteSqlAsync(System.Runtime.CompilerServices.FormattableStringFactory.Create(
                $"select {Schema}.levee_idempotency_complete({{0}}, {{1}}, {{2}}, {{3}}, {{4}})",
                key.Subject, key.Operation, key.Key, new NpgsqlParameter<string?>("result", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = result }, key.Lifetime), ct);
        }
        await tx.CommitAsync(ct);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        ambient.Current = null;
        Aggregates.End(db);
        LeveeTransactions.Remove(tx.GetDbTransaction());
        if (!_committed)
        {
            try
            {
                await tx.RollbackAsync();
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
            {
                // The connection broke or the transaction already ended: there's nothing left to roll back.
            }
        }
        await tx.DisposeAsync();
    }
}
