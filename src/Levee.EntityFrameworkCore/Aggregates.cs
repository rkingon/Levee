using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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

/// <summary>Maps each root's version onto <c>xmin</c>, a system column migrations leave alone, beside any token of the app's own.</summary>
internal sealed class AggregateVersionConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var root in modelBuilder.Metadata.GetEntityTypes().Where(e => Aggregates.IsRoot(e) && e.BaseType is null).ToList())
        {
            if (root.GetProperties().Any(p => Aggregates.IsVersion(p, p.GetColumnName())))
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
/// The shape an aggregate is mapped in: its root, a class of its own on one table with Postgres's <c>xmin</c> its version,
/// and every part of it on the root's row (a complex type, or an owned type on the root's table or in a JSON column of
/// it). A change anywhere in the aggregate is then a write of that one row, which EF holds to its version like any other.
/// An entity of its own is an aggregate of its own, referenced by its id. Anything else is refused where the model is
/// first used, with its fix.
/// </summary>
internal static class AggregateLanguage
{
    private static readonly ConditionalWeakTable<IModel, StrongBox<string?>> Problems = [];

    /// <summary>What in the model's aggregates one row's version can't cover, with the fix; null when it covers them all.</summary>
    public static string? Problem(IModel model) => Problems.GetValue(model, m => new(Find(m))).Value;

    private static string? Find(IModel model) =>
        model.GetEntityTypes().Where(Aggregates.IsRoot).OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(root => Unsupported(root) ?? OffTheRow(root, root) ?? Reaching(root)).FirstOrDefault(p => p is not null);

    /// <summary>
    /// Another aggregate referring to <paramref name="root"/> by its id with a key whose delete writes it (EF's default for a
    /// required one is a cascade): deleting this aggregate would change that one too, unseen and unversioned.
    /// </summary>
    private static string? Reaching(IEntityType root) =>
        root.GetReferencingForeignKeys().FirstOrDefault(k => Aggregates.IsRoot(k.DeclaringEntityType)
            && k.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.SetNull) is { } reaching
            ? $"{reaching.DeclaringEntityType.ClrType.Name} refers to {root.ClrType.Name} by a key whose delete {(reaching.DeleteBehavior == DeleteBehavior.Cascade ? "deletes" : "changes")} "
                + "it: deleting one aggregate would write another, which no version covers. Fix: OnDelete(DeleteBehavior.Restrict) on that key; "
                + "what follows from the delete is an operation on the other aggregate of its own."
            : null;

    private static string? Unsupported(IEntityType root)
    {
        var name = root.ClrType.Name;
        if (root.BaseType is not null || root.GetDerivedTypes().Any())
        {
            return $"{name} is an aggregate root in an inheritance hierarchy, whose rows span tables or types one version doesn't cover. "
                + "Fix: a root is a class of its own, mapped to one table; a variation of it is a value of the root (a kind, a complex type), "
                + "or an aggregate of its own.";
        }
        if (root.GetTableName() is null || root.GetMappingFragments().Any())
        {
            return $"{name} is an aggregate root mapped to no table, or split across several, where its version is one row's. "
                + "Fix: map the root to one table of its own; what it holds is a complex type on its row.";
        }
        var table = Aggregates.TableOf(root);
        return root.GetProperties().Any(p => Aggregates.IsVersion(p, p.GetColumnName(table)))
            ? null
            : $"{name} is an aggregate root without a version, so two operations changing it at once would both commit and one change "
                + "would be lost. Fix: in the context, override ConfigureConventions(ModelConfigurationBuilder configurationBuilder) and call "
                + "configurationBuilder.AddLeveeConventions(), which maps every root's version to xmin (declare uint Version { get; private set; } "
                + "on the root to read it); a token of the app's own, or a version mapped to another column, isn't one.";
    }

    /// <summary>A part of <paramref name="root"/>'s aggregate held anywhere but its row: a change there writes no version.</summary>
    private static string? OffTheRow(IEntityType root, IEntityType holder)
    {
        const string fix = "hold it on the root's row (ComplexCollection(...).ToJson() or OwnsMany(...).ToJson() for a collection, "
            + "ComplexProperty for one), or make it an aggregate of its own, referenced by its id";
        foreach (var navigation in holder.GetNavigations())
        {
            var target = navigation.TargetEntityType;
            if (navigation.ForeignKey.IsOwnership && navigation.IsOnDependent)
            {
                continue;
            }
            if (!navigation.ForeignKey.IsOwnership)
            {
                return $"{holder.ClrType.Name}.{navigation.Name} reaches {target.ClrType.Name}, an entity of its own, from {root.ClrType.Name}'s aggregate: "
                    + $"a change to it is no write of {root.ClrType.Name}'s row, so the aggregate's version doesn't cover it. Fix: {fix}.";
            }
            if (!target.IsMappedToJson() && (target.GetTableName() != root.GetTableName() || target.GetSchema() != root.GetSchema()
                || target.GetMappingFragments().Any()))
            {
                return $"{holder.ClrType.Name}.{navigation.Name} owns {target.ClrType.Name} on a table of its own, off {root.ClrType.Name}'s row, so the "
                    + $"aggregate's version doesn't cover it. Fix: {fix}.";
            }
            if (OffTheRow(root, target) is { } deeper)
            {
                return deeper;
            }
        }
        return holder.GetSkipNavigations().FirstOrDefault() is { } skip
            ? $"{holder.ClrType.Name}.{skip.Name} is a many-to-many link, written to a join table no version covers. Fix: hold the other side's ids "
                + "on the root's row (a complex collection), or make the link an aggregate of its own."
            : null;
    }
}

/// <summary>
/// The unit of work's saves, held to the aggregates they write. EF holds each root's row to the version it was read at, and
/// the whole aggregate is that row. An operation that names the version its caller saw (<see cref="IVersionedOperation"/>)
/// writes one aggregate, the one its caller saw at that version: told by whose version each save actually moved, so
/// whatever made the change (the handler, a callback) is counted. One that writes nothing is held to nothing: a write that
/// changes nothing can't lose an update. Every save of a unit of work goes through
/// <see cref="SaveAsync"/>, each with one ticket for EF's save, and the first that fails fails the unit: nothing it wrote,
/// nor anything after it, commits.
/// </summary>
internal sealed class Aggregates : SaveChangesInterceptor
{
    public const string VersionName = "Version";

    public static readonly Aggregates Instance = new();

    private static readonly ConditionalWeakTable<DbContext, Unit> Units = [];

    /// <summary>One unit of work's saves: the version its operation names, the aggregate a save held to it, and the save that failed it.</summary>
    private sealed class Unit
    {
        public uint? Expected { get; set; }

        /// <summary>The aggregate written at <see cref="Expected"/>: the same root loaded again is the same aggregate.</summary>
        public Identity? Held { get; set; }

        public ExceptionDispatchInfo? Failure { get; set; }

        /// <summary>
        /// Granted by <see cref="SaveAsync"/> for its one EF save, and spent as that save begins: a save inside it (a callback's)
        /// finds none, as any save around the unit of work does.
        /// </summary>
        public bool Ticket { get; set; }
    }

    /// <summary>An aggregate, by its root's type and key, compared as EF compares keys.</summary>
    private sealed record Identity(IEntityType Type, object?[] Key)
    {
        public bool Same(Identity other) =>
            Type == other.Type && Type.FindPrimaryKey()!.Properties.Select((p, i) => p.GetKeyValueComparer().Equals(Key[i], other.Key[i])).All(e => e);
    }

    /// <summary>A root as a save found it: what it was, at which version.</summary>
    private sealed record Seen(EntityEntry Entry, Identity Identity, uint Version, EntityState State);

    public static bool IsRoot(IReadOnlyEntityType type) => typeof(IAggregateRoot).IsAssignableFrom(type.ClrType) && !type.IsOwned();

    /// <summary>Postgres's own row version, as the column it's mapped to: a token nothing but the store sets, so no write can forget to move it.</summary>
    public static bool IsVersion(IReadOnlyProperty property, string? column) =>
        property.IsConcurrencyToken && property.ClrType == typeof(uint) && property.ValueGenerated == ValueGenerated.OnAddOrUpdate
        && string.Equals(column, "xmin", StringComparison.Ordinal);

    public static StoreObjectIdentifier TableOf(IReadOnlyEntityType type) => StoreObjectIdentifier.Table(type.GetTableName()!, type.GetSchema());

    public static void Begin(DbContext db) => Units.AddOrUpdate(db, new Unit());

    public static void Expect(DbContext db, uint version) => UnitOf(db).Expected = version;

    public static void End(DbContext db) => Units.Remove(db);

    private static Unit UnitOf(DbContext db) =>
        Units.TryGetValue(db, out var unit) ? unit : throw new InvalidOperationException("Changes are saved only inside an operation's unit of work.");

    /// <summary>
    /// Writes what the unit of work holds, and holds a versioned operation to the one aggregate its caller saw. A save that
    /// fails, however it's caught, fails the unit: each later save, and the commit, throws the same failure again.
    /// </summary>
    public static async Task SaveAsync(DbContext db, CancellationToken ct)
    {
        var unit = UnitOf(db);
        unit.Failure?.Throw();
        try
        {
            var before = unit.Expected is null ? null : Roots(db);
            unit.Ticket = true;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                unit.Ticket = false;
            }
            // A save a callback made inside this one, refused and caught there, fails this one too.
            unit.Failure?.Throw();
            if (before is not null)
            {
                Hold(db, unit, before);
            }
        }
        catch (Exception e)
        {
            unit.Failure ??= ExceptionDispatchInfo.Capture(e);
            throw;
        }
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Admit(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Admit(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Spends the unit of work's ticket. A save inside an operation without one isn't the unit of work's own (LEVEE004 refuses
    /// its spelling): it fails the unit, as a failed save does.
    /// </summary>
    private static void Admit(DbContext? db)
    {
        if (db is null || !Units.TryGetValue(db, out var unit))
        {
            return;
        }
        if (unit.Ticket)
        {
            unit.Ticket = false;
            return;
        }
        var refused = new InvalidOperationException(
            $"{db.GetType().Name} saved its changes itself, inside an operation, where the unit of work writes them. Fix: take IFlush to write pending "
            + "changes before a later query; the commit writes the rest.");
        unit.Failure ??= ExceptionDispatchInfo.Capture(refused);
        throw refused;
    }

    private static List<Seen> Roots(DbContext db) =>
        [.. db.ChangeTracker.Entries().Where(e => IsRoot(e.Metadata))
            .Select(e => new Seen(e, new Identity(e.Metadata, [.. e.Metadata.FindPrimaryKey()!.Properties.Select(p => e.Property(p.Name).CurrentValue)]),
                (uint)Token(e).OriginalValue!, e.State))];

    /// <summary>
    /// The aggregates a save wrote, told by the store: a root inserted or deleted, or whose version moved. A versioned
    /// operation writes one, which it doesn't create, at the version its caller saw. A root the save itself began tracking
    /// (a callback's) is one whose write can't be told apart from a read, so it's refused.
    /// </summary>
    private static void Hold(DbContext db, Unit unit, List<Seen> before)
    {
        if (db.ChangeTracker.Entries().FirstOrDefault(e => IsRoot(e.Metadata) && !before.Any(b => ReferenceEquals(b.Entry.Entity, e.Entity))) is { } arrived)
        {
            throw new InvalidOperationException(
                $"The operation names the version its caller saw, but its save began tracking a {arrived.Metadata.ClrType.Name} (a callback's), so whose "
                + "aggregate it wrote can't be told. Fix: an IVersionedOperation changes the one root it loaded, in its handler.");
        }
        foreach (var written in before.Where(b => b.State is EntityState.Added or EntityState.Deleted || b.Entry.State == EntityState.Detached
                     || !Equals(Token(b.Entry).CurrentValue, b.Version)))
        {
            if (written.State == EntityState.Added)
            {
                throw new InvalidOperationException(
                    $"The operation names the version its caller saw, but its change creates a {written.Entry.Metadata.ClrType.Name}, which has no version yet. "
                    + "Fix: an IVersionedOperation changes a root it loaded; one that creates is an operation of its own.");
            }
            if (unit.Held is { } held)
            {
                if (!held.Same(written.Identity))
                {
                    throw new InvalidOperationException(
                        "The operation names the version its caller saw, but changes more than one aggregate, so there's no one version to hold it to. "
                        + "Fix: an IVersionedOperation changes the one root it loaded; what follows in another aggregate is an operation of its own, "
                        + "enqueued as a job in this one.");
                }
                continue;
            }
            if (written.Version != unit.Expected)
            {
                throw new DbUpdateConcurrencyException(
                    $"{written.Entry.Metadata.ClrType.Name} was at version {written.Version} when it was read, not {unit.Expected}, the one its caller saw: it changed since.");
            }
            unit.Held = written.Identity;
        }
    }

    private static PropertyEntry Token(EntityEntry root)
    {
        var table = TableOf(root.Metadata);
        return root.Property(root.Metadata.GetProperties().First(p => IsVersion(p, p.GetColumnName(table))).Name);
    }
}
