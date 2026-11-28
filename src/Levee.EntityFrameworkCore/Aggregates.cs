using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

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

/// <summary>Maps each root's version onto <c>xmin</c>, a system column migrations leave alone, unless the app mapped a token of its own.</summary>
internal sealed class AggregateVersionConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var root in modelBuilder.Metadata.GetEntityTypes().Where(e => Aggregates.IsRoot(e) && e.BaseType is null).ToList())
        {
            if (root.GetProperties().Any(p => p.IsConcurrencyToken))
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
/// Every change to an aggregate is a change to its root: a child added, changed or removed marks its root modified, so
/// the root's version check covers the whole aggregate. An operation that names the version its caller saw
/// (<see cref="IVersionedOperation"/>) holds the one root it changes to that version, compared by Postgres as it writes.
/// </summary>
internal sealed class Aggregates : SaveChangesInterceptor
{
    public const string VersionName = "Version";

    public static readonly Aggregates Instance = new();

    private static readonly ConditionalWeakTable<DbContext, Expectation> Expected = [];

    private sealed class Expectation(uint version)
    {
        public uint Version { get; } = version;

        public bool Applied { get; set; }
    }

    public static bool IsRoot(IReadOnlyEntityType type) => typeof(IAggregateRoot).IsAssignableFrom(type.ClrType) && !type.IsOwned();

    public static void Expect(DbContext db, uint version) => Expected.AddOrUpdate(db, new Expectation(version));

    public static void Forget(DbContext db) => Expected.Remove(db);

    /// <summary>
    /// Before the commit: an expected version no write compared (nothing of the root changed) is compared with the root as it
    /// was loaded. A versioned operation that loaded no root, or several, is a bug.
    /// </summary>
    public static void Verify(DbContext db)
    {
        if (!Expected.TryGetValue(db, out var expected) || expected.Applied)
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

    /// <summary>A model whose roots aren't all versioned: the context's conventions don't include Levee's.</summary>
    public static string? Unversioned(IModel model) =>
        model.GetEntityTypes().FirstOrDefault(e => IsRoot(e) && e.BaseType is null && !e.GetProperties().Any(p => p.IsConcurrencyToken)) is { } root
            ? root.ClrType.Name
            : null;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Prepare(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Prepare(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Prepare(DbContext? db)
    {
        if (db is null)
        {
            return;
        }
        db.ChangeTracker.DetectChanges();
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && !IsRoot(e.Metadata)).ToList())
        {
            if (RootOf(entry) is { State: EntityState.Unchanged } root)
            {
                root.State = EntityState.Modified;
            }
        }
        if (Expected.TryGetValue(db, out var expected) && !expected.Applied)
        {
            var changed = db.ChangeTracker.Entries().Where(e => IsRoot(e.Metadata) && e.State is EntityState.Modified or EntityState.Deleted).ToList();
            if (changed.Count > 1)
            {
                throw new InvalidOperationException(
                    $"The operation names one version, the one its caller saw, but changes {changed.Count} aggregates ({string.Join(", ", changed.Select(e => e.Metadata.ClrType.Name))}). "
                    + "Fix: change one aggregate per operation; what follows in another aggregate is its own operation, enqueued as a job.");
            }
            if (changed is [var root])
            {
                // Postgres compares it in the write's own WHERE: no window between the check and the change.
                Token(root).OriginalValue = expected.Version;
                expected.Applied = true;
            }
        }
    }

    private static PropertyEntry Token(EntityEntry root) =>
        root.Property(root.Metadata.GetProperties().First(p => p.IsConcurrencyToken).Name);

    /// <summary>
    /// The root a changed entry belongs to: up the relationships its parent navigates to it by (an owned type's, a root's
    /// collection of children), never a reference by id to another aggregate. Null when it belongs to none.
    /// </summary>
    private static EntityEntry? RootOf(EntityEntry entry)
    {
        for (var current = entry; ;)
        {
            if (current.Metadata.GetForeignKeys().FirstOrDefault(fk => fk.IsOwnership || fk.PrincipalToDependent is not null) is not { } parent)
            {
                return null;
            }
            var key = parent.Properties.Select(p => current.State == EntityState.Deleted ? current.Property(p.Name).OriginalValue : current.Property(p.Name).CurrentValue).ToArray();
            var principal = current.Context.ChangeTracker.Entries()
                .FirstOrDefault(e => parent.PrincipalEntityType.IsAssignableFrom(e.Metadata)
                    && parent.PrincipalKey.Properties.Select(p => e.Property(p.Name).CurrentValue).SequenceEqual(key));
            if (principal is null)
            {
                throw new InvalidOperationException(
                    $"A {entry.Metadata.ClrType.Name} changed without its aggregate's root being loaded, so no version covers the change. "
                    + "Fix: load the root through its repository and change it through its own method.");
            }
            if (IsRoot(principal.Metadata))
            {
                return principal;
            }
            current = principal;
        }
    }
}
