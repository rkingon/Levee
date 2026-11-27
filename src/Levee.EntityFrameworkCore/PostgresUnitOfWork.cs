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
        services.ConfigureDbContext<TContext>(o => o.AddInterceptors(OutsideOperationGuard.Instance));
        // The guard is on TContext alone: a second context would reach the database around every operation.
        services.AddSingleton<IValidateOptions<OneContext>>(provider =>
            new OneContextValidation(services, typeof(TContext), provider.GetRequiredService<IServiceScopeFactory>()));
        services.AddOptions<OneContext>().ValidateOnStart();
        return services;
    }
}

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
        var others = services
            .SelectMany(d => new[] { d.ServiceType, d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType,
                (d.IsKeyedService ? d.KeyedImplementationInstance : d.ImplementationInstance)?.GetType() })
            .OfType<Type>()
            .Select(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DbContextOptions<>) ? t.GenericTypeArguments[0] : t)
            .Where(t => typeof(DbContext).IsAssignableFrom(t) && t != context)
            .Select(t => t.Name)
            .Distinct()
            .ToList();
        if (others.Count > 0)
        {
            return ValidateOptionsResult.Fail(
                $"{string.Join(", ", others)} is a DbContext besides {context.Name}, Levee's unit of work: it reaches the database outside "
                + $"every operation's transaction. Map its entities in {context.Name}; a Tool's owner work builds its own context.");
        }
        using var scope = scopes.CreateScope();
        return Unfit((DbContext)scope.ServiceProvider.GetRequiredService(context)) is { } problem
            ? ValidateOptionsResult.Fail(problem)
            : ValidateOptionsResult.Success;
    }

    /// <summary>What the context's own configuration would undo: the guard, or a transaction the caller begins.</summary>
    public static string? Unfit(DbContext db)
    {
        var name = db.GetType().Name;
        var interceptors = db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors;
        if (interceptors?.Contains(OutsideOperationGuard.Instance) != true)
        {
            return $"{name} doesn't carry Levee's guard, so it would run commands outside every operation. Fix: take "
                + $"DbContextOptions<{name}> in its constructor and configure it in AddDbContext, not OnConfiguring.";
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
            : db.SaveChangesAsync(ct);
}

internal sealed class AmbientTransaction : IAmbientTransaction
{
    public (System.Data.Common.DbConnection Connection, System.Data.Common.DbTransaction Transaction)? Current { get; set; }
}

internal sealed class PostgresUnitOfWorkFactory<TContext>(TContext db, AmbientTransaction ambient, IOptions<OneContext> one) : IUnitOfWorkFactory
    where TContext : DbContext
{
    // Outside a host nothing validates on start: the first operation does.
    private readonly OneContext _one = one.Value;

    // Once a process per model and target (database, role, search path): a table's policy comes with its migration, which runs
    // before the app does, and the same context type may reach another database or schema by its options.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Microsoft.EntityFrameworkCore.Metadata.IModel, string), byte> Isolated = new();

    /// <summary>
    /// Row-level security holds everything the context reads and writes: a migration that forgot a policy, or a view that reads
    /// around one, would otherwise open its rows to every tenant, since the runtime role is granted every table.
    /// </summary>
    private async Task VerifyIsolationAsync(CancellationToken ct)
    {
        // Every relation the model reads or writes, however it's mapped (split, as a view, through the search path), and every
        // relation a view among them reads, down to the tables. Each is judged by what it is, not by how EF maps it:
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
        var open = await db.Database.SqlQuery<string>($"""
            with recursive mapped as (
              select n, to_regclass(n)::oid as oid from unnest({names}) n
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
                $"Row-level security doesn't hold what the context reads and writes, so every tenant's operations would see all of it: "
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
                case NpgsqlException { IsTransient: true }:
                case TimeoutException:
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
            // The same round trip says where the transaction resolves names: the check below holds for that target alone.
            var target = await db.Database.SqlQuery<string>($"""
                select concat_ws(':', current_database(), current_user, current_setting('search_path')) as "Value"
                from (select set_config('levee.scope', {key}, true), set_config('levee.platform', {platform}, true)) as scoped
                """).SingleAsync(ct);
            if (!Isolated.ContainsKey((db.Model, target)))
            {
                await VerifyIsolationAsync(ct);
                Isolated.TryAdd((db.Model, target), 0);
            }
            ambient.Current = (db.Database.GetDbConnection(), tx.GetDbTransaction());
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

    /// <summary>Through <c>levee_audit_write</c>, the one way the runtime role may add to the audit, in this transaction, at its time.</summary>
    public async Task AuditAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var schema = db.Model.FindEntityType(typeof(LeveeAuditRecord))?.GetSchema() ?? "public";
        await db.Database.ExecuteSqlAsync(System.Runtime.CompilerServices.FormattableStringFactory.Create(
            $"select {schema}.levee_audit_write({{0}}, {{1}}, {{2}}, {{3}}, {{4}}::jsonb)",
            Guid.CreateVersion7(entry.At), entry.Scope, entry.Subject, entry.Operation, entry.Input), ct);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        ambient.Current = null;
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
