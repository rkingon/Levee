using System.Collections.Concurrent;
using Levee.EntityFrameworkCore;
using Levee.Generated;
using Levee.Oxbow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Oxbow;
using Sample.Adapters;
using Sample.Contracts;

namespace Levee.IntegrationTests;

public sealed class ProcessStepTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost Build()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.App));
        builder.Services.AddDbContext<SampleDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<SampleDbContext>();
        builder.Services.AddLeveeOxbow(o => o.Queues["default"] = new QueueOptions { MaxWorkers = 2 }).AddLeveeJobs();
        builder.Services.AddSingleton<IAccessPolicy, TenantAccess>();
        builder.Services.AddScoped<IJobPrincipals, RestoreMembers>();
        return builder.Build();
    }

    private static (string Tenant, Member Member) NewTenant()
    {
        var tenant = $"t{Guid.CreateVersion7():N}";
        return (tenant, new Member($"ada@{tenant}", tenant));
    }

    [Fact]
    public async Task A_process_resumes_through_its_steps_and_does_none_twice()
    {
        using var host = Build();
        await host.StartAsync(Ct);
        var (tenant, ada) = NewTenant();

        Assert.True((await host.Services.GetRequiredService<Dispatcher>().SendAsync(new StartSteps(tenant), ada, Ct)).IsSuccess);

        for (var waited = 0; await postgres.CountAsync(
                 "select count(*) from oxbow_job where kind = 'levee_process' and args::text like '%' || $1 || '%' and state = 'completed'", tenant) == 0; waited++)
        {
            Assert.True(waited < 200, "the process didn't complete");
            await Task.Delay(50, Ct);
        }
        // Three runs: snoozed after the first step, and after the flaky step failed once; the note written once.
        Assert.Equal((3, 2), (StepProcess.Runs[tenant], StepProcess.Flaky[tenant]));
        Assert.Equal(1, await postgres.CountAsync("select count(*) from notes where tenant = $1", tenant));
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task A_superseded_attempts_write_rolls_back_and_ends_its_run_while_a_read_never_locks()
    {
        using var host = Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher>();
        var (tenant, ada) = NewTenant();
        static Task Superseded(IServiceProvider services, CancellationToken ct) => throw new AttemptSuperseded(new InvalidOperationException("rescued"));
        IAmbientTransaction? held = null;
        Task Locks(IServiceProvider services, CancellationToken ct)
        {
            held = services.GetRequiredService<IAmbientTransaction>();
            Assert.NotNull(held.Current);
            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<AttemptSuperseded>(() => dispatcher.SendAsStepAsync(new CreateNote(Guid.CreateVersion7(), tenant, "stale"), ada, Superseded, Ct));
        var read = await dispatcher.SendAsStepAsync(new ListNotes(tenant), ada, Superseded, Ct);
        var written = await dispatcher.SendAsStepAsync(new CreateNote(Guid.CreateVersion7(), tenant, "current"), ada, Locks, Ct);

        Assert.True(read.IsSuccess);
        Assert.True(written.IsSuccess);
        Assert.NotNull(held);
        Assert.Equal(1, await postgres.CountAsync("select count(*) from notes where tenant = $1", tenant));
    }
}

[Operation("probe.start_steps", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record StartSteps(string Tenant) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

public sealed record StepInput(string Tenant);

internal sealed class StartStepsHandler(IJobs jobs) : IOperationHandler<StartSteps, Unit>
{
    public async Task<Result<Unit>> HandleAsync(StartSteps operation, OperationContext context, CancellationToken ct)
    {
        await jobs.StartAsync(new StepInput(operation.Tenant), null, ct);
        return Unit.Value;
    }
}

/// <summary>Written from the top: a note, then a step that fails once. Each later run replays what's recorded.</summary>
internal sealed class StepProcess : IProcess<StepInput>
{
    public static ConcurrentDictionary<string, int> Runs { get; } = new();

    public static ConcurrentDictionary<string, int> Flaky { get; } = new();

    public async Task RunAsync(StepInput input, ProcessContext context, CancellationToken ct)
    {
        var run = Runs.AddOrUpdate(input.Tenant, 1, (_, n) => n + 1);
        var note = await context.StepAsync("write the note", token => context.SendAsync(new CreateNote(Guid.CreateVersion7(), input.Tenant, "once"), token), ct);
        if (!note.IsSuccess)
        {
            throw new InvalidOperationException(note.Error.Message);
        }
        if (run == 1)
        {
            context.Snooze(TimeSpan.FromMilliseconds(50));
        }
        var flaky = await context.StepAsync("ask the vendor", _ => Task.FromResult(Flaky.AddOrUpdate(input.Tenant, 1, (_, n) => n + 1) == 1
            ? Result<int>.Failure(Error.Unavailable("probe.vendor_busy", "Busy."))
            : Result<int>.Success(1)), ct);
        if (!flaky.IsSuccess)
        {
            context.Snooze(TimeSpan.FromMilliseconds(50));
        }
    }
}
