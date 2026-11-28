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
/// The aggregates a model holds, read once: its roots, and every entity type that belongs to exactly one root's aggregate,
/// reached from it by the navigations the aggregate holds (owned types, its collections of children), each with the
/// relationships that lead up to its parent. A small language on purpose: a root outside any inheritance hierarchy, on a
/// table, with a key Levee can write and Postgres's own version; a child of one aggregate only. Anything else is refused
/// where the model is first used, since a version Levee can't prove it moves is no version.
/// </summary>
internal sealed class AggregateMap
{
    private static readonly ConditionalWeakTable<IModel, AggregateMap> Maps = [];

    private readonly Dictionary<IEntityType, List<IForeignKey>> _parents = [];

    private AggregateMap(IModel model)
    {
        var owners = new Dictionary<IEntityType, IEntityType>();
        foreach (var root in model.GetEntityTypes().Where(Aggregates.IsRoot).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            Problem ??= Unsupported(root);
            var pending = new Queue<IEntityType>([root]);
            while (pending.TryDequeue(out var type))
            {
                if (type.GetSkipNavigations().FirstOrDefault() is { } skip)
                {
                    Problem ??= $"{type.ClrType.Name}.{skip.Name} is a many-to-many link in {root.ClrType.Name}'s aggregate, written to a join table "
                        + "no version covers. Fix: make the link a child of the aggregate (an entity holding the other side's id), "
                        + "or reference the other aggregate by its id.";
                }
                foreach (var navigation in type.GetNavigations().Where(n => !n.IsOnDependent))
                {
                    foreach (var member in navigation.TargetEntityType.GetDerivedTypesInclusive().Where(t => !Aggregates.IsRoot(t)))
                    {
                        if (owners.TryGetValue(member, out var owner) && owner != root)
                        {
                            Problem ??= $"{member.ClrType.Name} is a child of both {owner.ClrType.Name}'s aggregate and {root.ClrType.Name}'s, so no one "
                                + $"version covers it. Fix: a child belongs to one aggregate; what both refer to is an aggregate of its own, referenced by its id.";
                            continue;
                        }
                        owners[member] = root;
                        if (!_parents.TryGetValue(member, out var parents))
                        {
                            _parents[member] = parents = [];
                            pending.Enqueue(member);
                        }
                        if (!parents.Contains(navigation.ForeignKey))
                        {
                            parents.Add(navigation.ForeignKey);
                        }
                    }
                }
            }
        }
    }

    public static AggregateMap Of(IModel model) => Maps.GetValue(model, m => new AggregateMap(m));

    /// <summary>What in the model's aggregates Levee can't hold to a version, with the fix; null when it holds them all.</summary>
    public string? Problem { get; }

    /// <summary>The relationships up to a member's parent in its aggregate; none for a type that's in no aggregate.</summary>
    public IReadOnlyList<IForeignKey> ParentsOf(IReadOnlyEntityType type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t is IEntityType entity && _parents.TryGetValue(entity, out var parents))
            {
                return parents;
            }
        }
        return [];
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
        if (!root.GetProperties().Any(Aggregates.IsVersion))
        {
            return $"{name} is an aggregate root without a version, so two operations changing it at once would both commit and one change "
                + "would be lost. Fix: in the context, override ConfigureConventions(ModelConfigurationBuilder configurationBuilder) and call "
                + "configurationBuilder.AddLeveeConventions(), which maps every root's version to xmin (declare uint Version { get; private set; } "
                + "on the root to read it); a token of the app's own, which nothing but the app moves, isn't one.";
        }
        if (root.GetTableName() is null || root.GetMappingFragments().Any())
        {
            return $"{name} is an aggregate root mapped to no table, or split across several, where its version is one row's. "
                + "Fix: map the root to one table of its own; what it holds apart is an owned type or a child.";
        }
        if (root.FindPrimaryKey()?.Properties.FirstOrDefault(p => p.GetValueGenerationStrategy() == NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
            || p.GetComputedColumnSql() is not null) is { } key)
        {
            return $"{name}'s key {key.Name} is one Postgres won't let a write set, so a change to one of its children can't move its version. "
                + "Fix: give the root a key the application makes (context.NewId()), or an identity by default (UseIdentityByDefaultColumn).";
        }
        return null;
    }
}

/// <summary>
/// Every change to an aggregate is a change to its root: a child added, changed, moved or removed writes its root's version
/// too (every root it belonged to, or now belongs to), so the root's version check covers the whole aggregate. An
/// operation that names the version its caller saw (<see cref="IVersionedOperation"/>) holds the one root it changes to
/// that version, compared by Postgres as it writes, and changes no other aggregate. Every save of a unit of work goes
/// through <see cref="SaveAsync"/>, and the first that fails fails the unit: nothing it wrote, nor anything after it, commits.
/// </summary>
internal sealed class Aggregates : SaveChangesInterceptor
{
    public const string VersionName = "Version";

    public static readonly Aggregates Instance = new();

    private static readonly ConditionalWeakTable<DbContext, Unit> Units = [];

    /// <summary>A root by its type and key, as EF compares keys: the same row, however often it's loaded.</summary>
    private sealed record Identity(IEntityType Type, object?[] Key)
    {
        public static Identity Of(EntityEntry root) =>
            new((IEntityType)root.Metadata, [.. root.Metadata.FindPrimaryKey()!.Properties.Select(p => root.Property(p.Name).CurrentValue)]);

        public bool Is(Identity other) => Type == other.Type && Same(Type.FindPrimaryKey()!.Properties, Key, other.Key);
    }

    /// <summary>One unit of work's saves: the version its operation names, the root held to it, and the save that failed it.</summary>
    private sealed class Unit
    {
        public uint? Expected { get; set; }

        /// <summary>The root the expected version was held to, once a save wrote it: every later save is to it alone.</summary>
        public Identity? Held { get; set; }

        public System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure { get; set; }

        /// <summary>Set while <see cref="SaveAsync"/> runs EF's save: any other is a save around the unit of work.</summary>
        public bool Saving { get; set; }
    }

    public static bool IsRoot(IReadOnlyEntityType type) => typeof(IAggregateRoot).IsAssignableFrom(type.ClrType) && !type.IsOwned();

    /// <summary>Postgres's own row version: a token nothing but the store sets, so no write can forget to move it.</summary>
    public static bool IsVersion(IReadOnlyProperty property) =>
        property.IsConcurrencyToken && property.ClrType == typeof(uint) && property.ValueGenerated == ValueGenerated.OnAddOrUpdate
        && string.Equals(property.GetColumnName(), "xmin", StringComparison.Ordinal);

    public static void Begin(DbContext db) => Units.AddOrUpdate(db, new Unit());

    public static void Expect(DbContext db, uint version) => UnitOf(db).Expected = version;

    public static void End(DbContext db) => Units.Remove(db);

    private static Unit UnitOf(DbContext db) =>
        Units.TryGetValue(db, out var unit) ? unit : throw new InvalidOperationException("Changes are saved only inside an operation's unit of work.");

    /// <summary>
    /// Writes what the unit of work holds: first the roots whose children changed and nothing of their own did, held to their
    /// versions, then EF's changes. A save that fails, however it's caught, fails the unit: each later save, and the commit,
    /// throws the same failure again, so nothing written before it, or after, commits.
    /// </summary>
    public static async Task SaveAsync(DbContext db, CancellationToken ct)
    {
        var unit = UnitOf(db);
        unit.Failure?.Throw();
        try
        {
            var (touches, held) = Prepare(db, unit);
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
            unit.Held ??= held;
        }
        catch (Exception e)
        {
            unit.Failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
            throw;
        }
    }

    /// <summary>
    /// Before the commit: an expected version no save held a root to (nothing of the root changed) is compared with the root
    /// as it was loaded. A versioned operation that loaded no root, or several, is a bug.
    /// </summary>
    public static void Verify(DbContext db)
    {
        if (!Units.TryGetValue(db, out var unit) || unit.Expected is not { } expected || unit.Held is not null)
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

    /// <summary>A save inside an operation that isn't the unit of work's own would write no root's version (LEVEE004 refuses its spelling).</summary>
    private static void Refuse(DbContext? db)
    {
        if (db is not null && Units.TryGetValue(db, out var unit) && !unit.Saving)
        {
            throw new InvalidOperationException(
                $"{db.GetType().Name} saved its changes itself, inside an operation, where the unit of work writes them. Fix: take IFlush to write pending "
                + "changes before a later query; the commit writes the rest.");
        }
    }

    /// <summary>
    /// The roots to write that nothing else writes (an unchanged root whose child changed), each with the version it's held to,
    /// and the root an expected version is held to by this save, if it's the first to write one.
    /// </summary>
    private static (List<(EntityEntry Root, uint Version)> Touches, Identity? Held) Prepare(DbContext db, Unit unit)
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
        Identity? held = null;
        if (unit.Expected is { } expected)
        {
            var changed = db.ChangeTracker.Entries().Where(e => IsRoot(e.Metadata) && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Concat(touched)
                .ToList();
            var others = new List<(EntityEntry Entry, Identity Identity)>();
            foreach (var entry in changed)
            {
                var identity = Identity.Of(entry);
                if (unit.Held?.Is(identity) != true && !others.Any(o => o.Identity.Is(identity)))
                {
                    others.Add((entry, identity));
                }
            }
            if (others.Count > (unit.Held is null ? 1 : 0))
            {
                throw new InvalidOperationException(
                    $"The operation names one version, the one its caller saw, but changes more than one aggregate ({string.Join(", ", changed.Select(e => e.Metadata.ClrType.Name).Distinct())}). "
                    + "Fix: change one aggregate per operation; what follows in another aggregate is its own operation, enqueued as a job.");
            }
            if (unit.Held is null && others is [var (root, first)])
            {
                if (root.State == EntityState.Added)
                {
                    throw new InvalidOperationException(
                        $"The operation names the version its caller saw, but its one change creates a {root.Metadata.ClrType.Name}, which has no version yet. "
                        + "Fix: an IVersionedOperation changes a root it loaded; one that creates is an operation of its own.");
                }
                // Postgres compares it in the write's own WHERE: no window between the check and the change.
                held = first;
                if (versions.ContainsKey(root.Entity))
                {
                    versions[root.Entity] = expected;
                }
                else
                {
                    Token(root).OriginalValue = expected;
                }
            }
        }
        return ([.. touched.Select(t => (t, versions[t.Entity]))], held);
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
    /// Every root a changed entry belongs to, or belonged to before it moved: up the relationships its aggregate holds it by,
    /// keys compared as EF compares them. An entity in no aggregate (a lookup row) belongs to none.
    /// </summary>
    private static List<EntityEntry> RootsOf(AggregateMap map, EntityEntry entry)
    {
        var roots = new List<EntityEntry>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(EntityEntry current)
        {
            if (!seen.Add(current.Entity))
            {
                return;
            }
            foreach (var parent in map.ParentsOf(current.Metadata))
            {
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
        }
        Walk(entry);
        return roots;
    }

    private static bool Same(IReadOnlyList<IProperty> key, object?[] left, object?[] right) =>
        key.Select((p, i) => p.GetKeyValueComparer().Equals(left[i], right[i])).All(e => e);
}
