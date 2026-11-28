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
    public async Task A_child_moved_between_aggregates_changes_both()
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

        Assert.True(moved.IsSuccess);
        Assert.NotEqual(fromBefore, await Version(from));
        Assert.NotEqual(toBefore, await Version(to));
        Assert.Equal(1, await postgres.CountAsync("select count(*) from order_lines where order_id = $1", to));
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
}

[Adapter]
internal sealed class EfOrders(OrdersDb db) : IOrders
{
    public void Add(Order order) => db.Add(order);

    public Task<Order> FindAsync(Guid id, CancellationToken ct) => db.Set<Order>().Include(o => o.Lines).SingleAsync(o => o.Id == id, ct);
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
