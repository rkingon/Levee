using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

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
/// The aggregates a model holds, in the small language Levee and Postgres can hold to one version: a root (a class of its
/// own on one table, its key one column a write may set, Postgres's <c>xmin</c> its version) and its children (each on a
/// table of its own holding the root's key, or on its parent's row as an owned type). An aggregate refers to anything else
/// by its id, never by a navigation it holds the key of. Anything outside the language is refused where the model is first
/// used, with its fix.
/// </summary>
internal sealed class AggregateMap
{
    private static readonly ConditionalWeakTable<IModel, AggregateMap> Maps = [];

    private readonly Dictionary<IEntityType, IForeignKey> _parents = [];
    private readonly List<Root> _roots = [];
    private readonly List<Child> _children = [];

    /// <param name="Key">The root's one key column: what each of its child tables holds.</param>
    public sealed record Root(IEntityType Type, StoreObjectIdentifier Table, string Key);

    /// <param name="Column">The column, on the child's own table, holding its root's key.</param>
    public sealed record Child(IEntityType Type, StoreObjectIdentifier Table, Root Of, string Column);

    private AggregateMap(IModel model)
    {
        var owners = new Dictionary<IEntityType, IEntityType>();
        foreach (var type in model.GetEntityTypes().Where(Aggregates.IsRoot).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (Unsupported(type) is { } problem)
            {
                Problem ??= problem;
                continue;
            }
            var table = Aggregates.TableOf(type);
            var root = new Root(type, table, type.FindPrimaryKey()!.Properties[0].GetColumnName(table)!);
            _roots.Add(root);
            var pending = new Queue<IEntityType>([type]);
            while (pending.TryDequeue(out var parent))
            {
                Problem ??= Outward(parent);
                foreach (var navigation in parent.GetNavigations().Where(n => !n.IsOnDependent))
                {
                    foreach (var member in navigation.TargetEntityType.GetDerivedTypesInclusive().Where(t => !Aggregates.IsRoot(t)))
                    {
                        if (owners.TryGetValue(member, out var owner) && owner != type)
                        {
                            Problem ??= $"{member.ClrType.Name} is a child of both {owner.ClrType.Name}'s aggregate and {type.ClrType.Name}'s, so no one version "
                                + "covers it. Fix: a child belongs to one aggregate; what both refer to is an aggregate of its own, referenced by its id.";
                            continue;
                        }
                        if (owners.TryAdd(member, type))
                        {
                            _parents[member] = navigation.ForeignKey;
                            Place(member, parent, root, navigation.ForeignKey);
                            pending.Enqueue(member);
                        }
                    }
                }
            }
        }
    }

    public static AggregateMap Of(IModel model) => Maps.GetValue(model, m => new AggregateMap(m));

    /// <summary>What in the model's aggregates Levee can't hold to a version, with the fix; null when it holds them all.</summary>
    public string? Problem { get; private set; }

    public IReadOnlyList<Root> Roots => _roots;

    /// <summary>The children on tables of their own, each held by Postgres to its root's version.</summary>
    public IReadOnlyList<Child> Children => _children;

    /// <summary>The relationship up to a member's parent in its aggregate; null for a type that's in no aggregate.</summary>
    public IForeignKey? ParentOf(IReadOnlyEntityType type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t is IEntityType entity && _parents.TryGetValue(entity, out var parent))
            {
                return parent;
            }
        }
        return null;
    }

    /// <summary>
    /// Where a member's rows are: on its parent's row (an owned type), which the parent's write covers; or on a table of its
    /// own, holding the root's key, which Postgres holds to the root's version.
    /// </summary>
    private void Place(IEntityType member, IEntityType parent, Root root, IForeignKey parentKey)
    {
        if (member.GetTableName() is not { } name)
        {
            Problem ??= $"{member.ClrType.Name} is part of {root.Type.ClrType.Name}'s aggregate but maps to no table. Fix: map it to its parent's row (an owned type) "
                + "or to a table of its own holding the root's key.";
            return;
        }
        var table = StoreObjectIdentifier.Table(name, member.GetSchema());
        if (table == Aggregates.TableOf(parent) || member.BaseType is { } baseType && table == Aggregates.TableOf(baseType))
        {
            return;
        }
        if (member.BaseType is not null || parent != root.Type || parentKey.Properties is not [var column])
        {
            Problem ??= $"{member.ClrType.Name} is part of {root.Type.ClrType.Name}'s aggregate on a table of its own, but isn't one of its children holding its key: "
                + "Postgres tells which aggregate a row is part of by the root's key the row holds. Fix: a child of "
                + $"{root.Type.ClrType.Name} holds {root.Type.ClrType.Name}'s key, is reached from it directly, and keeps its subtypes on its own table; what "
                + "a child holds inside it is an owned type on the child's own row.";
            return;
        }
        _children.Add(new Child(member, table, root, column.GetColumnName(table)!));
    }

    private static string? Unsupported(IEntityType root)
    {
        var name = root.ClrType.Name;
        if (root.BaseType is not null || root.GetDerivedTypes().Any())
        {
            return $"{name} is an aggregate root in an inheritance hierarchy, whose rows span tables or types one version doesn't cover. "
                + "Fix: a root is a class of its own, mapped to one table; a variation of it is a value of the root (a kind, an owned type), "
                + "or an aggregate of its own.";
        }
        if (root.GetTableName() is null || root.GetMappingFragments().Any())
        {
            return $"{name} is an aggregate root mapped to no table, or split across several, where its version is one row's. "
                + "Fix: map the root to one table of its own; what it holds apart is an owned type or a child.";
        }
        var table = Aggregates.TableOf(root);
        if (!root.GetProperties().Any(p => Aggregates.IsVersion(p, p.GetColumnName(table))))
        {
            return $"{name} is an aggregate root without a version, so two operations changing it at once would both commit and one change "
                + "would be lost. Fix: in the context, override ConfigureConventions(ModelConfigurationBuilder configurationBuilder) and call "
                + "configurationBuilder.AddLeveeConventions(), which maps every root's version to xmin (declare uint Version { get; private set; } "
                + "on the root to read it); a token of the app's own, or a version mapped to another column, isn't one.";
        }
        if (root.FindPrimaryKey()?.Properties is not [var key])
        {
            return $"{name}'s key is more than one column, and a child tells Postgres which aggregate it's part of by the one column it holds. "
                + "Fix: give the root a key of one column (an id the application makes: context.NewId()).";
        }
        if (key.GetValueGenerationStrategy() == NpgsqlValueGenerationStrategy.IdentityAlwaysColumn || key.GetComputedColumnSql() is not null)
        {
            return $"{name}'s key {key.Name} is one Postgres won't let a write set, so a change to one of its children can't move its version. "
                + "Fix: give the root a key the application makes (context.NewId()), or an identity by default (UseIdentityByDefaultColumn).";
        }
        return null;
    }

    /// <summary>A navigation from inside an aggregate to a part outside it: what changes there is part of no aggregate's version.</summary>
    private static string? Outward(IEntityType type) =>
        type.GetNavigations().FirstOrDefault(n => n.IsOnDependent && !Aggregates.IsRoot(n.TargetEntityType) && !n.ForeignKey.IsOwnership
            && n.ForeignKey.PrincipalToDependent is null) is { } outward
            ? $"{type.ClrType.Name}.{outward.Name} navigates to {outward.TargetEntityType.ClrType.Name}, which is no aggregate's root, by a key "
                + $"{type.ClrType.Name} holds: a change to it is part of no aggregate's version. Fix: what an aggregate holds is a child holding its root's "
                + "key, or an owned type; what it refers to is an aggregate of its own, referenced by its id."
            : type.GetSkipNavigations().FirstOrDefault() is { } skip
                ? $"{type.ClrType.Name}.{skip.Name} is a many-to-many link, written to a join table no version covers. Fix: make the link a child of "
                    + "the aggregate (an entity holding the other side's id), or reference the other aggregate by its id."
                : null;
}

/// <summary>
/// The aggregate an operation changes is written with its version, every time: a child added, changed, moved or removed
/// writes its root's version too, and an operation that names the version its caller saw (<see cref="IVersionedOperation"/>)
/// holds its root to it, compared by Postgres as it writes. Every save of a unit of work goes through <see cref="SaveAsync"/>,
/// and the first that fails fails the unit: nothing it wrote, nor anything after it, commits. What no save here can see (a
/// cascade, raw SQL, a callback's change) Postgres holds instead: one aggregate per transaction, and no child changed
/// without its root (<see cref="LeveeSchema.AggregateRoot"/>, <see cref="LeveeSchema.AggregateChild"/>).
/// </summary>
internal sealed class Aggregates : SaveChangesInterceptor
{
    public const string VersionName = "Version";

    public static readonly Aggregates Instance = new();

    private static readonly ConditionalWeakTable<DbContext, Unit> Units = [];

    /// <summary>One unit of work's saves: the version its operation names, whether a save held its root to it yet, and the save that failed it.</summary>
    private sealed class Unit
    {
        public uint? Expected { get; set; }

        public bool Held { get; set; }

        public ExceptionDispatchInfo? Failure { get; set; }

        /// <summary>Set while <see cref="SaveAsync"/> runs EF's save: any other is a save around the unit of work.</summary>
        public bool Saving { get; set; }
    }

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
    /// Writes what the unit of work holds: first the root of a child that changed when nothing of the root's own did, held to
    /// its version, then EF's changes. A save that fails, however it's caught, fails the unit: each later save, and the
    /// commit, throws the same failure again.
    /// </summary>
    public static async Task SaveAsync(DbContext db, CancellationToken ct)
    {
        var unit = UnitOf(db);
        unit.Failure?.Throw();
        try
        {
            var (touches, holds) = Prepare(db, unit);
            foreach (var (root, version) in touches)
            {
                await using var touch = Touch(db, root, version);
                Written(root, await touch.ExecuteScalarAsync(ct));
            }
            unit.Saving = true;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                unit.Saving = false;
            }
            unit.Held |= holds;
        }
        catch (Exception e)
        {
            unit.Failure ??= ExceptionDispatchInfo.Capture(e);
            throw;
        }
    }

    /// <summary>
    /// Before the commit: an expected version no save held its root to (nothing of the root changed) is compared with the
    /// root as it was loaded. A versioned operation that loaded no root, or several, is a bug.
    /// </summary>
    public static void Verify(DbContext db)
    {
        if (!Units.TryGetValue(db, out var unit) || unit.Expected is not { } expected || unit.Held)
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
        if (!Equals(Token(root).CurrentValue, expected))
        {
            throw new DbUpdateConcurrencyException(
                $"{root.Metadata.ClrType.Name} isn't at version {expected}, the one its caller saw: it changed since.");
        }
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Refuse(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Refuse(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// A save inside an operation that isn't the unit of work's own would write no root's version (LEVEE004 refuses its
    /// spelling): it fails the unit, as a failed save does.
    /// </summary>
    private static void Refuse(DbContext? db)
    {
        if (db is not null && Units.TryGetValue(db, out var unit) && !unit.Saving)
        {
            var refused = new InvalidOperationException(
                $"{db.GetType().Name} saved its changes itself, inside an operation, where the unit of work writes them. Fix: take IFlush to write pending "
                + "changes before a later query; the commit writes the rest.");
            unit.Failure ??= ExceptionDispatchInfo.Capture(refused);
            throw refused;
        }
    }

    /// <summary>
    /// The roots to write that nothing else writes (an unchanged root whose child changed), each with the version it's held
    /// to; and whether this save holds the operation's root to its expected version.
    /// </summary>
    private static (List<(EntityEntry Root, uint Version)> Touches, bool Holds) Prepare(DbContext db, Unit unit)
    {
        db.ChangeTracker.DetectChanges();
        var map = AggregateMap.Of(db.Model);
        var touched = new List<EntityEntry>();
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && !IsRoot(e.Metadata)).ToList())
        {
            foreach (var root in RootsOf(map, entry).Where(r => r.State == EntityState.Unchanged && !touched.Any(t => ReferenceEquals(t.Entity, r.Entity))))
            {
                touched.Add(root);
            }
        }
        var versions = touched.ToDictionary(t => t.Entity, t => (uint)Token(t).OriginalValue!, ReferenceEqualityComparer.Instance);
        // One aggregate per transaction is Postgres's to hold, so the root this save writes is the operation's one root.
        if (unit is not { Expected: { } expected, Held: false }
            || db.ChangeTracker.Entries().Where(e => IsRoot(e.Metadata) && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Concat(touched).FirstOrDefault() is not { } changed)
        {
            return ([.. touched.Select(t => (t, versions[t.Entity]))], false);
        }
        if (changed.State == EntityState.Added)
        {
            throw new InvalidOperationException(
                $"The operation names the version its caller saw, but its change creates a {changed.Metadata.ClrType.Name}, which has no version yet. "
                + "Fix: an IVersionedOperation changes a root it loaded; one that creates is an operation of its own.");
        }
        // Postgres compares it in the write's own WHERE: no window between the check and the change.
        if (versions.ContainsKey(changed.Entity))
        {
            versions[changed.Entity] = expected;
        }
        else
        {
            Token(changed).OriginalValue = expected;
        }
        return ([.. touched.Select(t => (t, versions[t.Entity]))], true);
    }

    /// <summary>
    /// The root's own write, setting nothing but held to its version: <c>xmin</c> moves, and a write that read the root at
    /// another version finds no row and is refused as stale. Through the unit of work's own transaction.
    /// </summary>
    private static DbCommand Touch(DbContext db, EntityEntry root, uint version)
    {
        var table = TableOf(root.Metadata);
        var sql = db.GetService<ISqlGenerationHelper>();
        var key = root.Metadata.FindPrimaryKey()!.Properties[0];
        var column = sql.DelimitIdentifier(key.GetColumnName(table)!);
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("An aggregate's version is written only inside an operation's unit of work.");
        command.Parameters.Add(((RelationalTypeMapping)key.GetTypeMapping()).CreateParameter(command, "key", root.Property(key.Name).CurrentValue));
        var expected = command.CreateParameter();
        expected.ParameterName = "version";
        expected.Value = version.ToString(CultureInfo.InvariantCulture);
        command.Parameters.Add(expected);
        command.CommandText = $"update {sql.DelimitIdentifier(table.Name, table.Schema)} set {column} = {column} where {column} = @key and xmin = @version::xid "
            + "returning xmin::text";
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

    private static PropertyEntry Token(EntityEntry root)
    {
        var table = TableOf(root.Metadata);
        return root.Property(root.Metadata.GetProperties().First(p => IsVersion(p, p.GetColumnName(table))).Name);
    }

    /// <summary>
    /// Every root a changed entry belongs to, or belonged to before it moved: up the relationships its aggregate holds it by,
    /// keys compared as EF compares them. An entity in no aggregate belongs to none.
    /// </summary>
    private static List<EntityEntry> RootsOf(AggregateMap map, EntityEntry entry)
    {
        var roots = new List<EntityEntry>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(EntityEntry current)
        {
            if (!seen.Add(current.Entity) || map.ParentOf(current.Metadata) is not { } parent)
            {
                return;
            }
            var keys = new List<object?[]>();
            foreach (var key in new[]
                     {
                         parent.Properties.Select(p => current.Property(p.Name).CurrentValue).ToArray(),
                         parent.Properties.Select(p => current.Property(p.Name).OriginalValue).ToArray(),
                     })
            {
                if (key.All(v => v is not null) && !keys.Any(k => Same(parent.PrincipalKey.Properties, k, key)))
                {
                    keys.Add(key);
                }
            }
            foreach (var key in keys)
            {
                var principal = current.Context.ChangeTracker.Entries()
                    .FirstOrDefault(e => parent.PrincipalEntityType.IsAssignableFrom(e.Metadata)
                        && Same(parent.PrincipalKey.Properties, [.. parent.PrincipalKey.Properties.Select(p => e.Property(p.Name).CurrentValue)], key))
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
        Walk(entry);
        return roots;
    }

    private static bool Same(IReadOnlyList<IProperty> key, object?[] left, object?[] right) =>
        key.Select((p, i) => p.GetKeyValueComparer().Equals(left[i], right[i])).All(e => e);
}
