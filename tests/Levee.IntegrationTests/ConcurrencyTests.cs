using System.Collections.Concurrent;
using Levee.EntityFrameworkCore;
using Levee.Generated;
using Levee.Oxbow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Sample.Adapters;
using Sample.Application;
using Sample.Contracts;

namespace Levee.IntegrationTests;

public sealed class ConcurrencyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost Build<TContext>()
        where TContext : DbContext
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.App));
        builder.Services.AddDbContext<TContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<TContext>();
        builder.Services.AddLeveeOxbow();
        builder.Services.AddSingleton<IAccessPolicy, TenantAccess>();
        return builder.Build();
    }

    /// <summary>The orders tables, once per database: every test in a class shares its fixture's.</summary>
    private async Task OrdersAsync()
    {
        if (await postgres.CountAsync("select count(*) from pg_class where relname = 'orders'") == 0)
        {
            await postgres.ExecuteAsync($"""
                create table orders (id uuid primary key, tenant text not null);
                create table order_lines (id uuid primary key, order_id uuid not null references orders(id), tenant text not null, amount int not null);
                {LeveeSchema.Isolation("orders", "tenant")}
                {LeveeSchema.Isolation("order_lines", "tenant")}
                {LeveeSchema.AggregateRoot("orders", "id")}
                {LeveeSchema.AggregateChild("order_lines", "orders", "order_id")}
                """);
        }
    }

    private static (string Tenant, Member Member) NewTenant()
    {
        var tenant = $"t{Guid.CreateVersion7():N}";
        return (tenant, new Member($"ada@{tenant}", tenant));
    }

    [Fact]
    public async Task Overlapping_edits_of_one_aggregate_commit_one_and_refuse_the_other_as_stale()
    {
        using var host = Build<SampleDbContext>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var note = (await dispatcher.SendAsync(new CreateNote(Guid.CreateVersion7(), tenant, "first"), ada, Ct)).Value;

        // Both read the same version before either writes: no ExpectedVersion, only the overlap itself.
        var edits = await Task.WhenAll(
            dispatcher.SendAsync(new EditHeld(tenant, note.Id, "mine"), ada, Ct),
            dispatcher.SendAsync(new EditHeld(tenant, note.Id, "theirs"), ada, Ct));

        Assert.Single(edits, e => e.IsSuccess);
        Assert.Equal(LeveeErrors.Stale, Assert.Single(edits, e => !e.IsSuccess).Error!.Code);
        var won = edits.Single(e => e.IsSuccess).Value;
        Assert.Equal(won, Assert.Single((await dispatcher.SendAsync(new ListNotes(tenant), ada, Ct)).Value).Text);
    }

    [Fact]
    public async Task An_edit_made_on_a_version_someone_else_changed_since_is_refused()
    {
        using var host = Build<SampleDbContext>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var seen = (await dispatcher.SendAsync(new CreateNote(Guid.CreateVersion7(), tenant, "first"), ada, Ct)).Value;

        var edited = await dispatcher.SendAsync(new EditNote(seen.Id, tenant, "second", seen.Version), ada, Ct);
        var stale = await dispatcher.SendAsync(new EditNote(seen.Id, tenant, "third", seen.Version), ada, Ct);
        // The same text writes nothing, and is held to the version all the same.
        var unchanged = await dispatcher.SendAsync(new EditNote(seen.Id, tenant, "second", seen.Version), ada, Ct);
        var current = await dispatcher.SendAsync(new EditNote(seen.Id, tenant, "fourth", edited.Value.Version), ada, Ct);

        Assert.True(edited.IsSuccess);
        Assert.NotEqual(seen.Version, edited.Value.Version);
        Assert.Equal(LeveeErrors.Stale, stale.Error!.Code);
        Assert.Equal(LeveeErrors.Stale, unchanged.Error!.Code);
        Assert.Equal("fourth", current.Value.Text);
    }

    [Fact]
    public async Task A_child_added_to_an_aggregate_is_a_change_to_its_root()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var order = Guid.CreateVersion7();
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);

        // Each adds a row of its own and changes no column of the order: only the aggregate's version can tell them apart.
        var added = await Task.WhenAll(
            dispatcher.SendAsync(new AddLineHeld(tenant, order, 1), ada, Ct),
            dispatcher.SendAsync(new AddLineHeld(tenant, order, 2), ada, Ct));

        Assert.Single(added, a => a.IsSuccess);
        Assert.Equal(LeveeErrors.Stale, Assert.Single(added, a => !a.IsSuccess).Error!.Code);
        Assert.Equal(1, await postgres.CountAsync("select count(*) from order_lines where order_id = $1", order));
    }

    [Fact]
    public async Task A_root_without_a_version_is_refused_at_start()
    {
        using var host = Build<UnversionedOrdersDb>();

        var refused = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => host.StartAsync(Ct));

        Assert.Contains("Order is an aggregate root without a version", refused.Message, StringComparison.Ordinal);
        Assert.Contains("configurationBuilder.AddLeveeConventions()", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_of_the_apps_own_is_no_version()
    {
        using var host = Build<StampedOrdersDb>();

        var refused = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => host.StartAsync(Ct));

        Assert.Contains("Order is an aggregate root without a version", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_versioned_edit_changes_one_aggregate_however_often_it_flushes()
    {
        using var host = Build<SampleDbContext>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var first = (await dispatcher.SendAsync(new CreateNote(Guid.CreateVersion7(), tenant, "first"), ada, Ct)).Value;
        var second = (await dispatcher.SendAsync(new CreateNote(Guid.CreateVersion7(), tenant, "second"), ada, Ct)).Value;

        var both = await dispatcher.SendAsync(new EditTwo(tenant, first.Id, second.Id, first.Version), ada, Ct);

        Assert.Equal(ErrorKind.Internal, both.Error!.Kind);
        Assert.Equal(["first", "second"], (await dispatcher.SendAsync(new ListNotes(tenant), ada, Ct)).Value.Select(n => n.Text));
    }

    [Fact]
    public async Task A_child_moved_between_aggregates_is_two_aggregates_changed_and_refused()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var (from, to, line) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, from), ada, Ct)).IsSuccess);
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, to), ada, Ct)).IsSuccess);
        Assert.True((await dispatcher.SendAsync(new AddLine(tenant, from, line), ada, Ct)).IsSuccess);
        Task<string> Version(Guid order) => postgres.TextAsync("select xmin::text from orders where id = $1", order);
        var (fromBefore, toBefore) = (await Version(from), await Version(to));

        var moved = await dispatcher.SendAsync(new MoveLine(tenant, from, to, line), ada, Ct);

        Assert.Equal(ErrorKind.Internal, moved.Error!.Kind);
        Assert.Equal((fromBefore, toBefore), (await Version(from), await Version(to)));
        Assert.Equal(1, await postgres.CountAsync("select count(*) from order_lines where order_id = $1", from));
    }

    [Fact]
    public async Task Postgres_refuses_a_child_written_around_its_root()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var (order, line) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);
        Assert.True((await dispatcher.SendAsync(new AddLine(tenant, order, line), ada, Ct)).IsSuccess);

        // As a cascade, raw SQL or a callback's change would: past every save Levee makes.
        var removed = await dispatcher.SendAsync(new RemoveLineBySql(tenant, line), ada, Ct);

        Assert.Equal(ErrorKind.Internal, removed.Error!.Kind);
        Assert.Equal(1, await postgres.CountAsync("select count(*) from order_lines where id = $1", line));
    }

    [Fact]
    public async Task Postgres_refuses_two_aggregates_in_one_operation_however_they_are_written()
    {
        using var host = Build<SampleDbContext>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();

        var both = await dispatcher.SendAsync(new CreateTwoNotes(tenant), ada, Ct);

        Assert.Equal(ErrorKind.Internal, both.Error!.Kind);
        Assert.Equal(0, await postgres.CountAsync("select count(*) from notes where tenant = $1", tenant));
    }

    [Fact]
    public async Task A_context_whose_aggregates_postgres_doesnt_hold_is_refused_at_start()
    {
        await postgres.ExecuteAsync($"""
            create table if not exists bare_orders (id uuid primary key, tenant text not null);
            create table if not exists bare_order_lines (id uuid primary key, order_id uuid not null references bare_orders(id), tenant text not null, amount int not null);
            {LeveeSchema.Isolation("bare_orders", "tenant")}
            {LeveeSchema.Isolation("bare_order_lines", "tenant")}
            """);
        try
        {
            using var host = Build<BareOrdersDb>();

            var refused = await dispatcher(host).SendAsync(new OpenOrder("t", Guid.CreateVersion7()), new Member("ada@t", "t"), Ct);

            Assert.Equal(ErrorKind.Internal, refused.Error!.Kind);
        }
        finally
        {
            await postgres.ExecuteAsync("drop table bare_order_lines; drop table bare_orders;");
        }

        static Dispatcher dispatcher(IHost host) => host.Services.GetRequiredService<Dispatcher>();
    }

    [Fact]
    public async Task A_save_refused_as_stale_stays_refused_however_often_it_is_tried()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var order = Guid.CreateVersion7();
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);
        var stale = uint.Parse(await postgres.TextAsync("select xmin::text from orders where id = $1", order), System.Globalization.CultureInfo.InvariantCulture) - 1;

        var added = await dispatcher.SendAsync(new AddLineVersioned(tenant, order, stale, "after a failed save"), ada, Ct);

        Assert.Equal(LeveeErrors.Stale, added.Error!.Code);
        Assert.Equal(0, await postgres.CountAsync("select count(*) from order_lines where order_id = $1", order));
    }

    [Fact]
    public async Task A_stale_save_caught_and_reloaded_around_stays_refused()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var order = Guid.CreateVersion7();
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);
        var stale = uint.Parse(await postgres.TextAsync("select xmin::text from orders where id = $1", order), System.Globalization.CultureInfo.InvariantCulture) - 1;

        var added = await dispatcher.SendAsync(new AddLineVersioned(tenant, order, stale, "reloaded after a failed save"), ada, Ct);

        Assert.Equal(LeveeErrors.Stale, added.Error!.Code);
        Assert.Equal(0, await postgres.CountAsync("select count(*) from levee_audit where scope = $1 and operation = 'probe.order_add_line_versioned'", tenant));
    }

    [Fact]
    public async Task A_failed_save_caught_by_its_handler_fails_the_operation_and_moves_no_version()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var (order, other, line) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, other), ada, Ct)).IsSuccess);
        // The line's id is another order's, so the database refuses it after the order's version was written.
        Assert.True((await dispatcher.SendAsync(new AddLine(tenant, other, line), ada, Ct)).IsSuccess);
        var before = await postgres.TextAsync("select xmin::text from orders where id = $1", order);

        var caught = await dispatcher.SendAsync(new AddLineCaught(tenant, order, line), ada, Ct);

        Assert.Equal(LeveeErrors.Conflict, caught.Error!.Code);
        Assert.Equal(before, await postgres.TextAsync("select xmin::text from orders where id = $1", order));
    }

    [Fact]
    public async Task A_save_around_the_unit_of_work_is_refused()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var order = Guid.CreateVersion7();
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);

        var saved = await dispatcher.SendAsync(new AddLineSavedItself(tenant, order), ada, Ct);

        Assert.Equal(ErrorKind.Internal, saved.Error!.Kind);
        Assert.Equal(0, await postgres.CountAsync("select count(*) from order_lines where order_id = $1", order));
    }

    [Fact]
    public async Task The_same_root_loaded_again_is_the_same_aggregate()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var order = Guid.CreateVersion7();
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);
        var version = uint.Parse(await postgres.TextAsync("select xmin::text from orders where id = $1", order), System.Globalization.CultureInfo.InvariantCulture);

        var added = await dispatcher.SendAsync(new AddLineVersioned(tenant, order, version, "reloaded"), ada, Ct);

        Assert.True(added.IsSuccess, added.Error?.Message);
        Assert.Equal(2, await postgres.CountAsync("select count(*) from order_lines where order_id = $1", order));
    }

    public static TheoryData<string, string?> Mappings() => new()
    {
        { nameof(OrdersDb), null },
        { nameof(TptOrdersDb), "aggregate root in an inheritance hierarchy" },
        { nameof(DerivedRootDb), "aggregate root in an inheritance hierarchy" },
        { nameof(IdentityAlwaysDb), "is one Postgres won't let a write set" },
        { nameof(IdentityByDefaultDb), null },
        { nameof(SharedChildDb), "is a child of both" },
        { nameof(SplitOrdersDb), "split across several" },
        { nameof(LinkedOrdersDb), "is a many-to-many link" },
        { nameof(HeldDetailDb), "by a key Desk holds" },
        { nameof(CompositeDb), "key is more than one column" },
        { nameof(GrandchildDb), "isn't one of its children holding its key" },
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void An_aggregate_is_mapped_in_the_shapes_one_version_can_cover(string context, string? refused)
    {
        using var db = context switch
        {
            nameof(OrdersDb) => (DbContext)Model<OrdersDb>(),
            nameof(TptOrdersDb) => Model<TptOrdersDb>(),
            nameof(DerivedRootDb) => Model<DerivedRootDb>(),
            nameof(IdentityAlwaysDb) => Model<IdentityAlwaysDb>(),
            nameof(IdentityByDefaultDb) => Model<IdentityByDefaultDb>(),
            nameof(SplitOrdersDb) => Model<SplitOrdersDb>(),
            nameof(HeldDetailDb) => Model<HeldDetailDb>(),
            nameof(CompositeDb) => Model<CompositeDb>(),
            nameof(GrandchildDb) => Model<GrandchildDb>(),
            nameof(LinkedOrdersDb) => Model<LinkedOrdersDb>(),
            _ => Model<SharedChildDb>(),
        };

        var problem = Levee.EntityFrameworkCore.AggregateMap.Of(db.Model).Problem;

        if (refused is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(refused, problem, StringComparison.Ordinal);
        }
    }

    /// <summary>A context's model alone, built without a database.</summary>
    private static TContext Model<TContext>()
        where TContext : DbContext =>
        (TContext)Activator.CreateInstance(typeof(TContext), new DbContextOptionsBuilder<TContext>().UseNpgsql("Host=unused").Options)!;

    [Fact]
    public void A_lookup_that_navigates_to_a_child_isnt_part_of_its_aggregate()
    {
        using var db = Model<LookupDb>();
        var map = Levee.EntityFrameworkCore.AggregateMap.Of(db.Model);

        Assert.Null(map.Problem);
        Assert.Null(map.ParentOf(db.Model.FindEntityType(typeof(Shelf))!));
        Assert.Equal(typeof(Order), map.ParentOf(db.Model.FindEntityType(typeof(OrderLine))!)!.PrincipalEntityType.ClrType);
    }

    [Fact]
    public void The_version_is_Postgres_s_own_and_no_migration_creates_it()
    {
        using var db = new SampleDbContext(new DbContextOptionsBuilder<SampleDbContext>().UseNpgsql("Host=unused").Options);

        var version = db.Model.FindEntityType(typeof(Sample.Domain.Note))!.FindProperty(nameof(Sample.Domain.Note.Version))!;

        Assert.Equal("xmin", version.GetColumnName());
        Assert.True(version.IsConcurrencyToken);
        Assert.DoesNotContain("xmin", db.Database.GenerateCreateScript(), StringComparison.Ordinal);
    }
}

/// <summary>Two calls that meet here have both read before either writes.</summary>
internal static class Overlap
{
    private static readonly ConcurrentDictionary<Guid, (TaskCompletionSource Both, int[] Arrived)> Gates = new();

    public static async Task MeetAsync(Guid key, CancellationToken ct)
    {
        var gate = Gates.GetOrAdd(key, _ => (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), [0]));
        if (Interlocked.Increment(ref gate.Arrived[0]) == 2)
        {
            gate.Both.SetResult();
        }
        await gate.Both.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
}

/// <summary>An edit with no expected version, held until another has read the same note.</summary>
[Operation("probe.edit_held", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record EditHeld(string Tenant, Guid Id, string Text) : IOperation<string>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class EditHeldHandler(INotes notes) : IOperationHandler<EditHeld, string>
{
    public async Task<Result<string>> HandleAsync(EditHeld operation, OperationContext context, CancellationToken ct)
    {
        var note = (await notes.FindAsync(operation.Id, ct))!;
        await Overlap.MeetAsync(operation.Id, ct);
        note.Edit(operation.Text);
        await notes.SaveAsync(ct);
        return note.Text;
    }
}

public sealed class Order : IAggregateRoot
{
    private readonly List<OrderLine> _lines = [];

    private Order(Guid id, string tenant)
    {
        Id = id;
        Tenant = tenant;
    }

    public Guid Id { get; private set; }

    public string Tenant { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    public static Order Open(Guid id, string tenant) => new(id, tenant);

    public void Add(Guid line, int amount) => _lines.Add(new OrderLine(line, Tenant, amount));

    public void Take(Order from, Guid line)
    {
        var taken = from._lines.Single(l => l.Id == line);
        from._lines.Remove(taken);
        _lines.Add(taken);
    }
}

public sealed class OrderLine(Guid id, string tenant, int amount)
{
    public Guid Id { get; private set; } = id;

    public string Tenant { get; private set; } = tenant;

    public int Amount { get; private set; } = amount;
}

internal class OrdersDb : DbContext
{
    public OrdersDb(DbContextOptions<OrdersDb> options)
        : base(options)
    {
    }

    protected OrdersDb(DbContextOptions options)
        : base(options)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddLeveeAudit();
        modelBuilder.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.Property(o => o.Id).HasColumnName("id");
            e.Property(o => o.Tenant).HasColumnName("tenant");
            e.HasMany(o => o.Lines).WithOne().HasForeignKey("order_id");
            e.Navigation(o => o.Lines).HasField("_lines");
        });
        modelBuilder.Entity<OrderLine>(e =>
        {
            e.ToTable("order_lines");
            e.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(l => l.Tenant).HasColumnName("tenant");
            e.Property(l => l.Amount).HasColumnName("amount");
        });
    }
}

/// <summary>Its root versioned by a token of the app's own, which nothing but the app moves.</summary>
internal sealed class StampedOrdersDb(DbContextOptions<StampedOrdersDb> options) : OrdersDb(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Order>().Property<string>("Stamp").IsConcurrencyToken();
    }
}

internal sealed class UnversionedOrdersDb(DbContextOptions<UnversionedOrdersDb> options) : OrdersDb(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }
}

[Operation("probe.order_open", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record OpenOrder(string Tenant, Guid Id) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

/// <summary>Adds a line, held until another has read the same order.</summary>
[Operation("probe.order_add_line_held", "member", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record AddLineHeld(string Tenant, Guid Order, int Amount) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

[Operation("probe.order_add_line", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record AddLine(string Tenant, Guid Order, Guid Line) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

[Operation("probe.order_move_line", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record MoveLine(string Tenant, Guid From, Guid To, Guid Line) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

/// <summary>Adds a line whose id is taken, catches the save's refusal, drops the line, and goes on as if it had written.</summary>
[Operation("probe.order_add_line_caught", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record AddLineCaught(string Tenant, Guid Order, Guid Line) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class AddLineCaughtHandler(IOrders orders) : IOperationHandler<AddLineCaught, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddLineCaught operation, OperationContext context, CancellationToken ct)
    {
        (await orders.FindAsync(operation.Order, ct)).Add(operation.Line, 1);
        try
        {
            await orders.SaveAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Swallowed, and the line let go of: the order's version was written before the line failed, and the commit has
            // nothing left to write.
            orders.Forget();
        }
        return Unit.Value;
    }
}

/// <summary>Adds a line and saves through EF itself.</summary>
[Operation("probe.order_add_line_saved_itself", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record AddLineSavedItself(string Tenant, Guid Order) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class AddLineSavedItselfHandler(IOrders orders) : IOperationHandler<AddLineSavedItself, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddLineSavedItself operation, OperationContext context, CancellationToken ct)
    {
        (await orders.FindAsync(operation.Order, ct)).Add(context.NewId(), 1);
        await orders.SaveItselfAsync(ct);
        return Unit.Value;
    }
}

/// <summary>Held to one note's version, then edits that note and another, saving between.</summary>
[Operation("probe.edit_two", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record EditTwo(string Tenant, Guid First, Guid Second, uint Version) : IOperation<Unit>, IScopedOperation, IVersionedOperation
{
    string IScopedOperation.ScopeKey => Tenant;

    uint IVersionedOperation.ExpectedVersion => Version;
}

internal sealed class EditTwoHandler(INotes notes) : IOperationHandler<EditTwo, Unit>
{
    public async Task<Result<Unit>> HandleAsync(EditTwo operation, OperationContext context, CancellationToken ct)
    {
        (await notes.FindAsync(operation.First, ct))!.Edit("first, edited");
        await notes.SaveAsync(ct);
        (await notes.FindAsync(operation.Second, ct))!.Edit("second, edited");
        await notes.SaveAsync(ct);
        return Unit.Value;
    }
}

internal sealed class AddLineHandler(IOrders orders) : IOperationHandler<AddLine, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddLine operation, OperationContext context, CancellationToken ct)
    {
        (await orders.FindAsync(operation.Order, ct)).Add(operation.Line, 1);
        return Unit.Value;
    }
}

internal sealed class MoveLineHandler(IOrders orders) : IOperationHandler<MoveLine, Unit>
{
    public async Task<Result<Unit>> HandleAsync(MoveLine operation, OperationContext context, CancellationToken ct)
    {
        var from = await orders.FindAsync(operation.From, ct);
        (await orders.FindAsync(operation.To, ct)).Take(from, operation.Line);
        return Unit.Value;
    }
}

public interface IOrders : IRepository<Order>
{
    void Add(Order order);

    Task<Order> FindAsync(Guid id, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);

    /// <summary>Lets go of what's loaded, as a repository that reads afresh would.</summary>
    void Forget();

    /// <summary>Saves through EF itself, around the unit of work: what LEVEE004 refuses the spelling of.</summary>
    Task SaveItselfAsync(CancellationToken ct);

    /// <summary>Removes a line by SQL of its own, past the order: what a cascade would do.</summary>
    Task RemoveLineBySqlAsync(Guid line, CancellationToken ct);
}

[Adapter]
internal sealed class EfOrders(OrdersDb db, IFlush flush) : IOrders
{
    public void Add(Order order) => db.Add(order);

    public Task<Order> FindAsync(Guid id, CancellationToken ct) => db.Set<Order>().Include(o => o.Lines).SingleAsync(o => o.Id == id, ct);

    public Task SaveAsync(CancellationToken ct) => flush.FlushAsync(ct);

    public void Forget() => db.ChangeTracker.Clear();

    public Task SaveItselfAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public Task RemoveLineBySqlAsync(Guid line, CancellationToken ct) => db.Database.ExecuteSqlAsync($"delete from order_lines where id = {line}", ct);
}

[Operation("probe.order_remove_line_by_sql", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record RemoveLineBySql(string Tenant, Guid Line) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class RemoveLineBySqlHandler(IOrders orders) : IOperationHandler<RemoveLineBySql, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RemoveLineBySql operation, OperationContext context, CancellationToken ct)
    {
        await orders.RemoveLineBySqlAsync(operation.Line, ct);
        return Unit.Value;
    }
}

/// <summary>Writes two notes in one operation: two aggregates.</summary>
[Operation("probe.notes_create_two", "member", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record CreateTwoNotes(string Tenant) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class CreateTwoNotesHandler(Sample.Application.INotes notes) : IOperationHandler<CreateTwoNotes, Unit>
{
    public Task<Result<Unit>> HandleAsync(CreateTwoNotes operation, OperationContext context, CancellationToken ct)
    {
        notes.Add(Sample.Domain.Note.Create(context.NewId(), operation.Tenant, "one", context.Now));
        notes.Add(Sample.Domain.Note.Create(context.NewId(), operation.Tenant, "two", context.Now));
        return Task.FromResult<Result<Unit>>(Unit.Value);
    }
}

/// <summary>Held to a version, adds a line and saves; then, as <see cref="Again"/> says, saves again after a failed save, or reloads and adds another.</summary>
[Operation("probe.order_add_line_versioned", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record AddLineVersioned(string Tenant, Guid Order, uint Version, string Again) : IOperation<Unit>, IScopedOperation, IVersionedOperation
{
    string IScopedOperation.ScopeKey => Tenant;

    uint IVersionedOperation.ExpectedVersion => Version;
}

internal sealed class AddLineVersionedHandler(IOrders orders) : IOperationHandler<AddLineVersioned, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddLineVersioned operation, OperationContext context, CancellationToken ct)
    {
        (await orders.FindAsync(operation.Order, ct)).Add(context.NewId(), 1);
        if (operation.Again is "after a failed save" or "reloaded after a failed save")
        {
            try
            {
                await orders.SaveAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Caught, and gone on from: the commit must still refuse what this save refused.
            }
            if (operation.Again == "reloaded after a failed save")
            {
                orders.Forget();
                await orders.FindAsync(operation.Order, ct);
                await orders.SaveAsync(ct);
            }
            return Unit.Value;
        }
        await orders.SaveAsync(ct);
        orders.Forget();
        (await orders.FindAsync(operation.Order, ct)).Add(context.NewId(), 2);
        await orders.SaveAsync(ct);
        return Unit.Value;
    }
}

internal sealed class OpenOrderHandler(IOrders orders) : IOperationHandler<OpenOrder, Unit>
{
    public Task<Result<Unit>> HandleAsync(OpenOrder operation, OperationContext context, CancellationToken ct)
    {
        orders.Add(Order.Open(operation.Id, operation.Tenant));
        return Task.FromResult<Result<Unit>>(Unit.Value);
    }
}

internal sealed class AddLineHeldHandler(IOrders orders) : IOperationHandler<AddLineHeld, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddLineHeld operation, OperationContext context, CancellationToken ct)
    {
        var order = await orders.FindAsync(operation.Order, ct);
        await Overlap.MeetAsync(operation.Order, ct);
        order.Add(context.NewId(), operation.Amount);
        return Unit.Value;
    }
}

/// <summary>An order split across a table per type: one version can't cover two rows.</summary>
internal sealed class TptOrdersDb(DbContextOptions<TptOrdersDb> options) : OrdersDb(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Order2>().ToTable("orders2");
        modelBuilder.Entity<RushOrder>().ToTable("rush_orders");
    }
}

public sealed class RushOrder : Order2;

public class Order2 : IAggregateRoot
{
    public Guid Id { get; private set; }
}

internal sealed class DerivedRootDb(DbContextOptions<DerivedRootDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Plain>();
        modelBuilder.Entity<RootOfPlain>();
    }
}

public class Plain
{
    public Guid Id { get; private set; }
}

/// <summary>Only the derived type is a root, its base a plain entity sharing its table.</summary>
public sealed class RootOfPlain : Plain, IAggregateRoot;

internal sealed class IdentityAlwaysDb(DbContextOptions<IdentityAlwaysDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Counter>().Property(c => c.Id).UseIdentityAlwaysColumn();
}

internal sealed class IdentityByDefaultDb(DbContextOptions<IdentityByDefaultDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Counter>().Property(c => c.Id).UseIdentityByDefaultColumn();
}

public sealed class Counter : IAggregateRoot
{
    public long Id { get; private set; }
}

/// <summary>One child type reached from two roots: whose version covers it?</summary>
internal sealed class SharedChildDb(DbContextOptions<SharedChildDb> options) : OrdersDb(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Basket>(e => e.HasMany(b => b.Lines).WithOne().HasForeignKey("basket_id"));
    }
}

public sealed class Basket : IAggregateRoot
{
    public Guid Id { get; private set; }

    public List<OrderLine> Lines { get; } = [];
}

/// <summary>A lookup with a collection of an aggregate's children: a reference, not an owner.</summary>
internal sealed class LookupDb(DbContextOptions<LookupDb> options) : OrdersDb(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Shelf>(e => e.HasMany(s => s.Lines).WithOne().HasForeignKey("shelf_id"));
    }
}

public sealed class Shelf
{
    public Guid Id { get; private set; }

    public List<OrderLine> Lines { get; } = [];
}

/// <summary>An order whose tenant lives in a table of its own: an edit there is no write of the order's row.</summary>
internal sealed class SplitOrdersDb(DbContextOptions<SplitOrdersDb> options) : OrdersDb(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Order>().SplitToTable("order_tenants", t => t.Property(o => o.Tenant));
    }
}

/// <summary>An order's lines linked to tags many-to-many: the link is a row of a join table.</summary>
internal sealed class LinkedOrdersDb(DbContextOptions<LinkedOrdersDb> options) : OrdersDb(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<OrderLine>().HasMany<LineTag>().WithMany();
    }
}

public sealed class LineTag
{
    public Guid Id { get; private set; }
}

/// <summary>The orders model on tables with no aggregate triggers.</summary>
internal sealed class BareOrdersDb(DbContextOptions<BareOrdersDb> options) : OrdersDb(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Order>().ToTable("bare_orders");
        modelBuilder.Entity<OrderLine>().ToTable("bare_order_lines");
    }
}

/// <summary>A desk holding the key of a lamp it navigates to: the lamp is part of no aggregate's version.</summary>
internal sealed class HeldDetailDb(DbContextOptions<HeldDetailDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Desk>().HasOne(d => d.Lamp).WithMany();
}

public sealed class Desk : IAggregateRoot
{
    public Guid Id { get; private set; }

    public Lamp? Lamp { get; private set; }
}

public sealed class Lamp
{
    public Guid Id { get; private set; }
}

internal sealed class CompositeDb(DbContextOptions<CompositeDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Ledger>().HasKey(l => new { l.Tenant, l.Number });
}

public sealed class Ledger : IAggregateRoot
{
    public string Tenant { get; private set; } = "";

    public int Number { get; private set; }
}

/// <summary>A parcel's own labels on a table of their own: they hold the parcel's key, not the shipment's.</summary>
internal sealed class GrandchildDb(DbContextOptions<GrandchildDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Shipment>();
}

public sealed class Shipment : IAggregateRoot
{
    public Guid Id { get; private set; }

    public List<Parcel> Parcels { get; } = [];
}

public sealed class Parcel
{
    public Guid Id { get; private set; }

    public List<ParcelLabel> Labels { get; } = [];
}

public sealed class ParcelLabel
{
    public Guid Id { get; private set; }
}
