using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
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
        return services;
    }
}

/// <summary>
/// Work as the database's owner, outside any operation: a Tool's migrations and role bootstrap. Everything else reaches the
/// database only inside an operation's unit of work (LEVEE018 holds this to Tools and tests).
/// </summary>
public static class LeveeOwner
{
    private static readonly AsyncLocal<bool> Active = new();

    internal static bool IsActive => Active.Value;

    /// <summary>Lets the context run commands outside an operation until disposed.</summary>
    public static IDisposable Run()
    {
        Active.Value = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Active.Value = false;
    }
}

/// <summary>
/// Refuses a command run outside a transaction: on the app's context, that's outside every operation (a seeder, a context
/// a Host resolved), so not authorized, not scoped, not read-only when it should be, not audited, and committed on its own.
/// </summary>
internal sealed class OutsideOperationGuard : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    public static readonly OutsideOperationGuard Instance = new();

    private static void Check(System.Data.Common.DbCommand command)
    {
        if (command.Transaction is null && !LeveeOwner.IsActive)
        {
            throw new InvalidOperationException(
                "The app's DbContext ran a command outside an operation's unit of work. Send an operation through the Dispatcher; "
                + "a Tool's owner work runs inside LeveeOwner.Run().");
        }
    }

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

internal sealed class PostgresUnitOfWorkFactory<TContext>(TContext db, AmbientTransaction ambient) : IUnitOfWorkFactory
    where TContext : DbContext
{
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
                        PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected =>
                            Error.Unavailable(LeveeErrors.Contention, "It collided with another change; try again."),
                        _ when pg.IsTransient => Error.Unavailable(LeveeErrors.StoreUnavailable, "The database is busy; try again."),
                        _ => null,
                    };
                case NpgsqlException { IsTransient: true }:
                case TimeoutException:
                    return Error.Unavailable(LeveeErrors.StoreUnavailable, "The database couldn't be reached; try again.");
            }
        }
        return null;
    }

    public async Task<IUnitOfWork> BeginAsync(WorkScope scope, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException($"{typeof(TContext).Name} already has a transaction: an operation's unit of work opens its own.");
        }
        var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            if (scope.ReadOnly)
            {
                await db.Database.ExecuteSqlRawAsync("set transaction read only", ct);
            }
            var key = scope.Key ?? "";
            var platform = scope.Platform ? "on" : "off";
            await db.Database.ExecuteSqlAsync($"select set_config('levee.scope', {key}, true), set_config('levee.platform', {platform}, true)", ct);
            ambient.Current = (db.Database.GetDbConnection(), tx.GetDbTransaction());
            return new PostgresUnitOfWork(db, tx, scope, ambient);
        }
        catch
        {
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
