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

    /// <summary>The orders table, once per database: every test in a class shares its fixture's. An order's lines are on its row.</summary>
    private async Task OrdersAsync()
    {
        if (await postgres.CountAsync("select count(*) from pg_class where relname = 'orders'") == 0)
        {
            await postgres.ExecuteAsync($"""
                create table orders (id uuid primary key, tenant text not null, lines jsonb not null default '[]');
                {LeveeSchema.Isolation("orders", "tenant")}
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
        // The same text writes nothing, so it's held to nothing: it loses no one's edit.
        var unchanged = await dispatcher.SendAsync(new EditNote(seen.Id, tenant, "second", seen.Version), ada, Ct);
        var current = await dispatcher.SendAsync(new EditNote(seen.Id, tenant, "fourth", edited.Value.Version), ada, Ct);

        Assert.True(edited.IsSuccess);
        Assert.NotEqual(seen.Version, edited.Value.Version);
        Assert.Equal(LeveeErrors.Stale, stale.Error!.Code);
        Assert.True(unchanged.IsSuccess, unchanged.Error?.Message);
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
        Assert.Equal(1, await postgres.CountAsync("select jsonb_array_length(lines)::bigint from orders where id = $1", order));
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

        Assert.True(moved.IsSuccess, moved.Error?.Message);
        Assert.NotEqual(fromBefore, await Version(from));
        Assert.NotEqual(toBefore, await Version(to));
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
        Assert.Equal(0, await postgres.CountAsync("select jsonb_array_length(lines)::bigint from orders where id = $1", order));
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
        var (order, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, other), ada, Ct)).IsSuccess);
        var before = await postgres.TextAsync("select xmin::text from orders where id = $1", order);

        // Its save writes the order's line, then an order whose id is taken, which the database refuses.
        var caught = await dispatcher.SendAsync(new AddLineCaught(tenant, order, other), ada, Ct);

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
        Assert.Equal(0, await postgres.CountAsync("select jsonb_array_length(lines)::bigint from orders where id = $1", order));
    }

    [Fact]
    public async Task A_save_a_callback_makes_inside_the_unit_of_works_own_fails_it_though_caught()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var order = Guid.CreateVersion7();
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);

        var echoed = await dispatcher.SendAsync(new AddLineEchoed(tenant, order), ada, Ct);

        Assert.Equal(ErrorKind.Internal, echoed.Error!.Kind);
        Assert.Equal(0, await postgres.CountAsync("select jsonb_array_length(lines)::bigint from orders where id = $1", order));
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
        Assert.Equal(2, await postgres.CountAsync("select jsonb_array_length(lines)::bigint from orders where id = $1", order));
    }

    [Fact]
    public async Task A_versioned_edit_whose_callback_opens_another_aggregate_is_refused()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var (order, opened) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);
        var version = uint.Parse(await postgres.TextAsync("select xmin::text from orders where id = $1", order), System.Globalization.CultureInfo.InvariantCulture);

        var added = await dispatcher.SendAsync(new AddLineOpening(tenant, order, opened, version), ada, Ct);

        Assert.Equal(ErrorKind.Internal, added.Error!.Kind);
        Assert.Equal(0, await postgres.CountAsync("select count(*) from orders where id = $1", opened));
    }

    [Fact]
    public async Task An_aggregate_is_removed_with_the_parts_on_its_row()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var order = Guid.CreateVersion7();
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);
        Assert.True((await dispatcher.SendAsync(new AddLine(tenant, order, Guid.CreateVersion7()), ada, Ct)).IsSuccess);

        var removed = await dispatcher.SendAsync(new RemoveOrder(tenant, order), ada, Ct);

        Assert.True(removed.IsSuccess, removed.Error?.Message);
        Assert.Equal(0, await postgres.CountAsync("select count(*) from orders where id = $1", order));
    }

    [Fact]
    public async Task A_versioned_edit_that_replaces_its_root_creates_one_and_is_refused()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var order = Guid.CreateVersion7();
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);
        Assert.True((await dispatcher.SendAsync(new AddLine(tenant, order, Guid.CreateVersion7()), ada, Ct)).IsSuccess);
        var version = uint.Parse(await postgres.TextAsync("select xmin::text from orders where id = $1", order), System.Globalization.CultureInfo.InvariantCulture);

        var replaced = await dispatcher.SendAsync(new ReplaceOrder(tenant, order, version), ada, Ct);

        Assert.Equal(ErrorKind.Internal, replaced.Error!.Kind);
        Assert.Equal(1, await postgres.CountAsync("select jsonb_array_length(lines)::bigint from orders where id = $1", order));
    }

    [Fact]
    public async Task A_versioned_edit_whose_callback_changes_another_aggregate_is_refused()
    {
        await OrdersAsync();
        using var host = Build<OrdersDb>();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        var (order, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, order), ada, Ct)).IsSuccess);
        Assert.True((await dispatcher.SendAsync(new OpenOrder(tenant, other), ada, Ct)).IsSuccess);
        var version = uint.Parse(await postgres.TextAsync("select xmin::text from orders where id = $1", order), System.Globalization.CultureInfo.InvariantCulture);

        var added = await dispatcher.SendAsync(new AddLineAlongside(tenant, order, other, version), ada, Ct);

        Assert.Equal(ErrorKind.Internal, added.Error!.Kind);
        Assert.Equal(0, await postgres.CountAsync("select sum(jsonb_array_length(lines))::bigint from orders where tenant = $1", tenant));
    }

    public static TheoryData<string, string?> Mappings() => new()
    {
        { nameof(OrdersDb), null },
        { nameof(OwnedOnTheRowDb), null },
        { nameof(TptOrdersDb), "aggregate root in an inheritance hierarchy" },
        { nameof(DerivedRootDb), "aggregate root in an inheritance hierarchy" },
        { nameof(SplitOrdersDb), "split across several" },
        { nameof(ChildTableDb), "an entity of its own" },
        { nameof(RootReferenceDb), "an entity of its own" },
        { nameof(OwnedTableDb), "on a table of its own" },
        { nameof(OwnedSplitDb), "on a table of its own" },
        { nameof(LinkedDb), "many-to-many link" },
        { nameof(CascadingDb), "by a key whose delete deletes it" },
        { nameof(RestrictedDb), null },
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void An_aggregate_is_mapped_on_its_roots_row(string context, string? refused)
    {
        using var db = context switch
        {
            nameof(OrdersDb) => (DbContext)Model<OrdersDb>(),
            nameof(OwnedOnTheRowDb) => Model<OwnedOnTheRowDb>(),
            nameof(TptOrdersDb) => Model<TptOrdersDb>(),
            nameof(DerivedRootDb) => Model<DerivedRootDb>(),
            nameof(SplitOrdersDb) => Model<SplitOrdersDb>(),
            nameof(ChildTableDb) => Model<ChildTableDb>(),
            nameof(RootReferenceDb) => Model<RootReferenceDb>(),
            nameof(OwnedTableDb) => Model<OwnedTableDb>(),
            nameof(OwnedSplitDb) => Model<OwnedSplitDb>(),
            nameof(CascadingDb) => Model<CascadingDb>(),
            nameof(RestrictedDb) => Model<RestrictedDb>(),
            _ => Model<LinkedDb>(),
        };

        var problem = Levee.EntityFrameworkCore.AggregateLanguage.Problem(db.Model);

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
        note.Edit(Sample.Domain.NoteText.From(operation.Text).Value);
        await notes.SaveAsync(ct);
        return note.Text;
    }
}

public sealed class Order : IAggregateRoot
{
    private Order(Guid id, string tenant)
    {
        Id = id;
        Tenant = tenant;
    }

    public Guid Id { get; private set; }

    public string Tenant { get; private set; }

    /// <summary>On the order's own row, so a change to one is a write of the order.</summary>
    public List<OrderLine> Lines { get; private set; } = [];

    public static Order Open(Guid id, string tenant) => new(id, tenant);

    public void Add(Guid line, int amount) => Lines.Add(new OrderLine(line, amount));

    public void Take(Order from, Guid line)
    {
        var taken = from.Lines.Single(l => l.Id == line);
        from.Lines.Remove(taken);
        Lines.Add(taken);
    }
}

public sealed record OrderLine(Guid Id, int Amount);

internal class OrdersDb : DbContext
{
    public OrdersDb(DbContextOptions<OrdersDb> options)
        : base(options)
    {
        SavingChanges += (_, _) =>
        {
            if (Alongside is { } other)
            {
                Alongside = null;
                other.Add(Guid.CreateVersion7(), 1);
            }
            if (Opening is { } opened)
            {
                Opening = null;
                Add(opened);
            }
        };
        SavedChanges += (_, _) =>
        {
            if (Echo is { } echo)
            {
                Echo = null;
                echo.Order.Add(echo.Line, 1);
                try
                {
                    SaveChanges();
                }
                catch (InvalidOperationException)
                {
                    // Caught, as a callback that means well would: the operation must fail all the same.
                }
            }
        };
    }

    /// <summary>A line a callback adds and saves after the unit of work's own save: a save inside a save.</summary>
    public (Order Order, Guid Line)? Echo { get; set; }

    /// <summary>An order a callback adds a line to as the next save begins, beside whatever the save writes.</summary>
    public Order? Alongside { get; set; }

    /// <summary>An order a callback opens as the next save begins, beside whatever the save writes.</summary>
    public Order? Opening { get; set; }

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
            e.ComplexCollection(o => o.Lines, l => l.ToJson("lines"));
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

/// <summary>Adds a line and opens an order whose id is taken, catches the save's refusal, lets go of both, and goes on as if it had written.</summary>
[Operation("probe.order_add_line_caught", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts)]
public sealed record AddLineCaught(string Tenant, Guid Order, Guid Taken) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class AddLineCaughtHandler(IOrders orders) : IOperationHandler<AddLineCaught, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddLineCaught operation, OperationContext context, CancellationToken ct)
    {
        (await orders.FindAsync(operation.Order, ct)).Add(context.NewId(), 1);
        orders.Add(Order.Open(operation.Taken, operation.Tenant));
        try
        {
            await orders.SaveAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Swallowed, and both let go of: the commit has nothing left to write.
            orders.Forget();
        }
        return Unit.Value;
    }
}

[Operation("probe.order_remove", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record RemoveOrder(string Tenant, Guid Order) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class RemoveOrderHandler(IOrders orders) : IOperationHandler<RemoveOrder, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RemoveOrder operation, OperationContext context, CancellationToken ct)
    {
        orders.Remove(await orders.FindAsync(operation.Order, ct));
        return Unit.Value;
    }
}

/// <summary>Held to one order's version, adds a line to it while the context's callback opens another order.</summary>
[Operation("probe.order_add_line_opening", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record AddLineOpening(string Tenant, Guid Order, Guid Opened, uint Version) : IOperation<Unit>, IScopedOperation, IVersionedOperation
{
    string IScopedOperation.ScopeKey => Tenant;

    uint IVersionedOperation.ExpectedVersion => Version;
}

internal sealed class AddLineOpeningHandler(IOrders orders) : IOperationHandler<AddLineOpening, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddLineOpening operation, OperationContext context, CancellationToken ct)
    {
        (await orders.FindAsync(operation.Order, ct)).Add(context.NewId(), 1);
        orders.OpenAlongside(operation.Opened, operation.Tenant);
        await orders.SaveAsync(ct);
        return Unit.Value;
    }
}

/// <summary>Held to a version, removes its order, then opens one with the same id in its place.</summary>
[Operation("probe.order_replace", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record ReplaceOrder(string Tenant, Guid Order, uint Version) : IOperation<Unit>, IScopedOperation, IVersionedOperation
{
    string IScopedOperation.ScopeKey => Tenant;

    uint IVersionedOperation.ExpectedVersion => Version;
}

internal sealed class ReplaceOrderHandler(IOrders orders) : IOperationHandler<ReplaceOrder, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ReplaceOrder operation, OperationContext context, CancellationToken ct)
    {
        orders.Remove(await orders.FindAsync(operation.Order, ct));
        await orders.SaveAsync(ct);
        orders.Add(Order.Open(operation.Order, operation.Tenant));
        await orders.SaveAsync(ct);
        return Unit.Value;
    }
}

/// <summary>Held to one order's version, adds a line to it while the context's callback adds one to another.</summary>
[Operation("probe.order_add_line_alongside", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record AddLineAlongside(string Tenant, Guid Order, Guid Other, uint Version) : IOperation<Unit>, IScopedOperation, IVersionedOperation
{
    string IScopedOperation.ScopeKey => Tenant;

    uint IVersionedOperation.ExpectedVersion => Version;
}

internal sealed class AddLineAlongsideHandler(IOrders orders) : IOperationHandler<AddLineAlongside, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddLineAlongside operation, OperationContext context, CancellationToken ct)
    {
        orders.ChangeAlongside(await orders.FindAsync(operation.Other, ct));
        (await orders.FindAsync(operation.Order, ct)).Add(context.NewId(), 1);
        await orders.SaveAsync(ct);
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

/// <summary>Adds a line and saves, while the context's callback saves another inside that save.</summary>
[Operation("probe.order_add_line_echoed", "member", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record AddLineEchoed(string Tenant, Guid Order) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

internal sealed class AddLineEchoedHandler(IOrders orders) : IOperationHandler<AddLineEchoed, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddLineEchoed operation, OperationContext context, CancellationToken ct)
    {
        (await orders.FindAsync(operation.Order, ct)).Add(context.NewId(), 1);
        orders.EchoOnSave(operation.Order, context.NewId());
        await orders.SaveAsync(ct);
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
        (await notes.FindAsync(operation.First, ct))!.Edit(Sample.Domain.NoteText.From("first, edited").Value);
        await notes.SaveAsync(ct);
        (await notes.FindAsync(operation.Second, ct))!.Edit(Sample.Domain.NoteText.From("second, edited").Value);
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

    /// <summary>Has the context's own callback add a line to an order and save it, once the next save is written.</summary>
    void EchoOnSave(Guid order, Guid line);

    /// <summary>Has the context's own callback add a line to a loaded order as the next save begins.</summary>
    void ChangeAlongside(Order order);

    void Remove(Order order);

    /// <summary>Has the context's own callback open an order as the next save begins.</summary>
    void OpenAlongside(Guid id, string tenant);

}

[Adapter]
internal sealed class EfOrders(OrdersDb db, IFlush flush) : IOrders
{
    public void Add(Order order) => db.Add(order);

    public Task<Order> FindAsync(Guid id, CancellationToken ct) => db.Set<Order>().SingleAsync(o => o.Id == id, ct);

    public Task SaveAsync(CancellationToken ct) => flush.FlushAsync(ct);

    public void Forget() => db.ChangeTracker.Clear();

    public Task SaveItselfAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public void EchoOnSave(Guid order, Guid line) => db.Echo = (db.Set<Order>().Local.Single(o => o.Id == order), line);

    public void ChangeAlongside(Order order) => db.Alongside = order;

    public void Remove(Order order) => db.Remove(order);

    public void OpenAlongside(Guid id, string tenant) => db.Opening = Order.Open(id, tenant);

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

/// <summary>An order whose tenant lives in a table of its own: an edit there is no write of the order's row.</summary>
internal sealed class SplitOrdersDb(DbContextOptions<SplitOrdersDb> options) : OrdersDb(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Order>().SplitToTable("order_tenants", t => t.Property(o => o.Tenant));
    }
}

/// <summary>A crate's parts on its own row: in a JSON column, and on its table.</summary>
internal sealed class OwnedOnTheRowDb(DbContextOptions<OwnedOnTheRowDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Crate>(e =>
    {
        e.OwnsMany(c => c.Slats, s => s.ToJson());
        e.OwnsOne(c => c.Lid);
        e.Ignore(c => c.Next);
        e.Ignore(c => c.Tags);
    });
}

/// <summary>A crate's slats, each an entity on a table of its own.</summary>
internal sealed class ChildTableDb(DbContextOptions<ChildTableDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Crate>(e =>
    {
        e.HasMany(c => c.Slats).WithOne();
        e.Ignore(c => c.Lid);
        e.Ignore(c => c.Next);
        e.Ignore(c => c.Tags);
    });
}

/// <summary>A crate that navigates to another aggregate's root.</summary>
internal sealed class RootReferenceDb(DbContextOptions<RootReferenceDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Crate>(e =>
    {
        e.HasOne(c => c.Next).WithMany();
        e.Ignore(c => c.Slats);
        e.Ignore(c => c.Lid);
        e.Ignore(c => c.Tags);
    });
}

/// <summary>A crate's slats owned, but on a table of their own (EF's default for OwnsMany).</summary>
internal sealed class OwnedTableDb(DbContextOptions<OwnedTableDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Crate>(e =>
    {
        e.OwnsMany(c => c.Slats);
        e.Ignore(c => c.Lid);
        e.Ignore(c => c.Next);
        e.Ignore(c => c.Tags);
    });
}

/// <summary>A crate's lid on its row, but one of its properties split onto a table of its own.</summary>
internal sealed class OwnedSplitDb(DbContextOptions<OwnedSplitDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Crate>(e =>
    {
        e.OwnsOne(c => c.Lid, l => l.SplitToTable("lid_colors", t => t.Property(x => x.Color)));
        e.Ignore(c => c.Slats);
        e.Ignore(c => c.Next);
        e.Ignore(c => c.Tags);
    });
}

/// <summary>A crate that refers to the shelf it's on by its id, with EF's default for a required key: deleting the shelf deletes it.</summary>
internal class CascadingDb : DbContext
{
    public CascadingDb(DbContextOptions<CascadingDb> options)
        : base(options)
    {
    }

    protected CascadingDb(DbContextOptions options)
        : base(options)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Rack>();
        modelBuilder.Entity<Shelved>().HasOne<Rack>().WithMany().HasForeignKey(c => c.Rack);
    }
}

/// <summary>The same reference, restricted: a shelf with crates on it isn't deleted.</summary>
internal sealed class RestrictedDb(DbContextOptions<RestrictedDb> options) : CascadingDb(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Shelved>().HasOne<Rack>().WithMany().HasForeignKey(c => c.Rack).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class Rack : IAggregateRoot
{
    public Guid Id { get; private set; }
}

public sealed class Shelved : IAggregateRoot
{
    public Guid Id { get; private set; }

    public Guid Rack { get; private set; }
}

/// <summary>A crate linked to tags many-to-many: the link is a row of a join table.</summary>
internal sealed class LinkedDb(DbContextOptions<LinkedDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.AddLeveeConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Crate>(e =>
    {
        e.HasMany(c => c.Tags).WithMany();
        e.Ignore(c => c.Slats);
        e.Ignore(c => c.Lid);
        e.Ignore(c => c.Next);
    });
}

public sealed class Crate : IAggregateRoot
{
    public Guid Id { get; private set; }

    public List<Slat> Slats { get; private set; } = [];

    public Lid? Lid { get; private set; }

    public Crate? Next { get; private set; }

    public List<CrateTag> Tags { get; private set; } = [];
}

public sealed class Slat
{
    public Guid Id { get; private set; }
}

public sealed class Lid
{
    public string Color { get; private set; } = "";

    public string Finish { get; private set; } = "";
}

public sealed class CrateTag
{
    public Guid Id { get; private set; }
}
