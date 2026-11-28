using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Storage;

namespace Levee.EntityFrameworkCore;

public static class LeveeConventionsExtensions
{
    /// <summary>
    /// Levee's conventions for the app's context, from its <c>ConfigureConventions</c>: every aggregate root's version is
    /// Postgres's <c>xmin</c>, a concurrency token, so two operations changing one aggregate at once can't both commit. A
    /// root that declares <c>uint Version { get; private set; }</c> reads it there; otherwise it's a shadow property. In the
    /// context itself, so every model of it (the host's, a Tool's, a migration's) is the same.
    /// </summary>
    public static ModelConfigurationBuilder AddLeveeConventions(this ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Conventions.Add(_ => new AggregateVersionConvention());
        return configurationBuilder;
    }
}

/// <summary>Maps each root's version onto <c>xmin</c>, a system column migrations leave alone, beside any token of the app's own.</summary>
internal sealed class AggregateVersionConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var root in modelBuilder.Metadata.GetEntityTypes().Where(e => Aggregates.IsRoot(e) && e.BaseType is null).ToList())
        {
            if (root.GetProperties().Any(Aggregates.IsVersion))
            {
                continue;
            }
            var declared = root.ClrType.GetProperty(Aggregates.VersionName);
            var version = declared?.PropertyType == typeof(uint)
                ? root.Builder.Property(declared)
                : root.Builder.Property(typeof(uint), "xmin");
            version?.HasColumnName("xmin");
            version?.HasColumnType("xid");
            version?.IsConcurrencyToken(true);
            version?.ValueGenerated(ValueGenerated.OnAddOrUpdate);
        }
    }
}

/// <summary>
/// Every change to an aggregate is a change to its root: a child added, changed, moved or removed writes its root's version
/// too (every root it belonged to, or now belongs to), so the root's version check covers the whole aggregate. An
/// operation that names the version its caller saw (<see cref="IVersionedOperation"/>) holds the one root it changes to
/// that version, compared by Postgres as it writes, and changes no other aggregate.
/// </summary>
internal sealed class Aggregates : SaveChangesInterceptor
{
    public const string VersionName = "Version";

    public static readonly Aggregates Instance = new();

    private static readonly ConditionalWeakTable<DbContext, Expectation> Expected = [];

    private sealed class Expectation(uint version)
    {
        public uint Version { get; } = version;

        /// <summary>The root the version was held to, once one was written: every later write of the operation is to it alone.</summary>
        public object? Root { get; set; }
    }

    public static bool IsRoot(IReadOnlyEntityType type) => typeof(IAggregateRoot).IsAssignableFrom(type.ClrType) && !type.IsOwned();

    /// <summary>Postgres's own row version: a token nothing but the store sets, so no write can forget to move it.</summary>
    public static bool IsVersion(IReadOnlyProperty property) =>
        property.IsConcurrencyToken && property.ClrType == typeof(uint) && property.ValueGenerated == ValueGenerated.OnAddOrUpdate
        && string.Equals(property.GetColumnName(), "xmin", StringComparison.Ordinal);

    public static void Expect(DbContext db, uint version) => Expected.AddOrUpdate(db, new Expectation(version));

    public static void Forget(DbContext db) => Expected.Remove(db);

    /// <summary>
    /// Before the commit: an expected version no write compared (nothing of the root changed) is compared with the root as it
    /// was loaded. A versioned operation that loaded no root, or several, is a bug.
    /// </summary>
    public static void Verify(DbContext db)
    {
        if (!Expected.TryGetValue(db, out var expected) || expected.Root is not null)
        {
            return;
        }
        var roots = db.ChangeTracker.Entries().Where(e => IsRoot(e.Metadata)).ToList();
        if (roots is not [var root])
        {
            throw new InvalidOperationException(
                $"The operation names the version its caller saw, but loaded {roots.Count} aggregate roots, so there's no one version to hold it to. "
                + "Fix: an IVersionedOperation loads the one root it changes, through its repository.");
        }
        if (!Equals(Token(root).CurrentValue, expected.Version))
        {
            throw new DbUpdateConcurrencyException(
                $"{root.Metadata.ClrType.Name} isn't at version {expected.Version}, the one its caller saw: it changed since.");
        }
    }

    /// <summary>A model whose roots aren't all versioned by the store: the context's conventions don't include Levee's.</summary>
    public static string? Unversioned(IModel model) =>
        model.GetEntityTypes().FirstOrDefault(e => IsRoot(e) && e.BaseType is null && !e.GetProperties().Any(IsVersion)) is { } root
            ? root.ClrType.Name
            : null;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } db)
        {
            foreach (var (root, version) in Prepare(db))
            {
                Written(root, Touch(db, root, version).ExecuteScalar());
            }
        }
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } db)
        {
            foreach (var (root, version) in Prepare(db))
            {
                await using var touch = Touch(db, root, version);
                Written(root, await touch.ExecuteScalarAsync(cancellationToken));
            }
        }
        return result;
    }

    /// <summary>The roots to write that nothing else writes (an unchanged root whose child changed), each with the version it's held to.</summary>
    private static List<(EntityEntry Root, uint Version)> Prepare(DbContext db)
    {
        db.ChangeTracker.DetectChanges();
        var touched = new List<EntityEntry>();
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && !IsRoot(e.Metadata)).ToList())
        {
            foreach (var root in RootsOf(entry).Where(r => r.State == EntityState.Unchanged && !touched.Any(t => ReferenceEquals(t.Entity, r.Entity))))
            {
                touched.Add(root);
            }
        }
        var versions = touched.ToDictionary(t => t.Entity, t => (uint)Token(t).OriginalValue!, ReferenceEqualityComparer.Instance);
        if (Expected.TryGetValue(db, out var expected))
        {
            var changed = db.ChangeTracker.Entries().Where(e => IsRoot(e.Metadata) && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Concat(touched)
                .ToList();
            if (changed.Where(e => !ReferenceEquals(e.Entity, expected.Root)).Select(e => e.Entity).Distinct(ReferenceEqualityComparer.Instance).Count()
                > (expected.Root is null ? 1 : 0))
            {
                throw new InvalidOperationException(
                    $"The operation names one version, the one its caller saw, but changes more than one aggregate ({string.Join(", ", changed.Select(e => e.Metadata.ClrType.Name).Distinct())}). "
                    + "Fix: change one aggregate per operation; what follows in another aggregate is its own operation, enqueued as a job.");
            }
            if (expected.Root is null && changed is [var root, ..])
            {
                if (root.State == EntityState.Added)
                {
                    throw new InvalidOperationException(
                        $"The operation names the version its caller saw, but its one change creates a {root.Metadata.ClrType.Name}, which has no version yet. "
                        + "Fix: an IVersionedOperation changes a root it loaded; one that creates is an operation of its own.");
                }
                // Postgres compares it in the write's own WHERE: no window between the check and the change.
                expected.Root = root.Entity;
                if (versions.ContainsKey(root.Entity))
                {
                    versions[root.Entity] = expected.Version;
                }
                else
                {
                    Token(root).OriginalValue = expected.Version;
                }
            }
        }
        return [.. touched.Select(t => (t, versions[t.Entity]))];
    }

    /// <summary>
    /// The root's own write, setting nothing but held to its version: <c>xmin</c> moves, and a write that read the root at
    /// another version finds no row and is refused as stale. Through the unit of work's own transaction.
    /// </summary>
    private static DbCommand Touch(DbContext db, EntityEntry root, uint version)
    {
        var type = root.Metadata;
        var table = type.GetTableName() ?? throw new InvalidOperationException($"{type.ClrType.Name} maps to no table, so its version can't be written.");
        var store = StoreObjectIdentifier.Table(table, type.GetSchema());
        var sql = db.GetService<ISqlGenerationHelper>();
        var key = type.FindPrimaryKey()!.Properties;
        var columns = key.Select(p => sql.DelimitIdentifier(p.GetColumnName(store)!)).ToList();
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("An aggregate's version is written only inside an operation's unit of work.");
        var conditions = new List<string>();
        for (var i = 0; i < key.Count; i++)
        {
            var mapping = (RelationalTypeMapping)key[i].GetTypeMapping();
            command.Parameters.Add(mapping.CreateParameter(command, $"k{i}", root.Property(key[i].Name).CurrentValue));
            conditions.Add($"{columns[i]} = @k{i}");
        }
        var expected = command.CreateParameter();
        expected.ParameterName = "version";
        expected.Value = version.ToString(CultureInfo.InvariantCulture);
        command.Parameters.Add(expected);
        command.CommandText = $"update {sql.DelimitIdentifier(table, type.GetSchema())} set {columns[0]} = {columns[0]} "
            + $"where {string.Join(" and ", conditions)} and xmin = @version::xid returning xmin::text";
        return command;
    }

    private static void Written(EntityEntry root, object? version)
    {
        if (version is not string written)
        {
            throw new DbUpdateConcurrencyException(
                $"{root.Metadata.ClrType.Name} changed since it was read, or isn't this scope's: its aggregate's change wasn't written.");
        }
        var token = Token(root);
        token.CurrentValue = token.OriginalValue = uint.Parse(written, CultureInfo.InvariantCulture);
        token.IsModified = false;
    }

    private static PropertyEntry Token(EntityEntry root) => root.Property(root.Metadata.GetProperties().First(IsVersion).Name);

    /// <summary>
    /// Every root a changed entry belongs to, or belonged to before it moved: up the relationships its parent navigates to
    /// it by (an owned type's, a root's collection of children), never a reference by id to another aggregate.
    /// </summary>
    private static List<EntityEntry> RootsOf(EntityEntry entry)
    {
        var roots = new List<EntityEntry>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(EntityEntry current)
        {
            if (!seen.Add(current.Entity))
            {
                return;
            }
            foreach (var parent in current.Metadata.GetForeignKeys().Where(fk => fk.IsOwnership || fk.PrincipalToDependent is not null))
            {
                var keys = new[]
                    {
                        parent.Properties.Select(p => current.Property(p.Name).CurrentValue).ToArray(),
                        parent.Properties.Select(p => current.Property(p.Name).OriginalValue).ToArray(),
                    }
                    .Where(k => k.All(v => v is not null))
                    .DistinctBy(k => string.Join("\u001f", k), StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    var principal = current.Context.ChangeTracker.Entries()
                        .FirstOrDefault(e => parent.PrincipalEntityType.IsAssignableFrom(e.Metadata)
                            && parent.PrincipalKey.Properties.Select(p => e.Property(p.Name).CurrentValue).SequenceEqual(key))
                        ?? throw new InvalidOperationException(
                            $"A {entry.Metadata.ClrType.Name} changed without its aggregate's root being loaded, so no version covers the change. "
                            + "Fix: load the root through its repository and change it through its own method.");
                    if (IsRoot(principal.Metadata))
                    {
                        if (!roots.Any(r => ReferenceEquals(r.Entity, principal.Entity)))
                        {
                            roots.Add(principal);
                        }
                    }
                    else
                    {
                        Walk(principal);
                    }
                }
            }
        }
        Walk(entry);
        return roots;
    }
}
