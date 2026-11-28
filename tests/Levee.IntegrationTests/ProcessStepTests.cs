using System.Collections.Concurrent;
using Levee.EntityFrameworkCore;
using Levee.Generated;
using Levee.Oxbow;
using Microsoft.AspNetCore.DataProtection;
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

    private IHost Build(bool seal = false)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Sample:BannedWords"] = "none";
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.App));
        builder.Services.AddDbContext<SampleDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddLeveeOperations(builder.Configuration);
        builder.Services.AddLeveePostgres<SampleDbContext>();
        builder.Services.AddDataProtection();
        builder.Services.AddLeveeOxbow(o => o.Queues["default"] = new QueueOptions { MaxWorkers = 2 }).AddLeveeJobs(sealSecrets: seal).AddArgsEncryption();
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

    [Theory]
    [InlineData(false, "levee_process")]
    [InlineData(true, "levee_process_sealed")]
    public async Task Where_jobs_seal_every_process_is_sealed_so_any_step_may_answer_a_secret(bool seal, string kind)
    {
        using var host = Build(seal);
        var (tenant, ada) = NewTenant();
        // A sealed job's args are ciphertext: it's told by its kind alone.
        var before = await postgres.CountAsync("select count(*) from oxbow_job where kind = $1", kind);

        Assert.True((await host.Services.GetRequiredService<Dispatcher>().SendAsync(new StartSteps(tenant), ada, Ct)).IsSuccess);

        Assert.Equal(before + 1, await postgres.CountAsync("select count(*) from oxbow_job where kind = $1", kind));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_superseded_attempt_reads_but_writes_nothing_however_it_sends(bool helper)
    {
        using var host = Build();
        await host.StartAsync(Ct);
        var (tenant, ada) = NewTenant();
        var run = Superseded.For(tenant);

        Assert.True((await host.Services.GetRequiredService<Dispatcher>().SendAsync(new StartSuperseded(tenant, helper), ada, Ct)).IsSuccess);
        await run.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // What the rescuer does: the job's attempt is another one now, and this run's is no longer current.
        await postgres.ExecuteAsync($"update oxbow_job set attempted_at = attempted_at + interval '1 second' where kind = 'levee_process' and args::text like '%{tenant}%'");
        run.Release.SetResult();

        Assert.Equal("read; write refused", await run.Outcome.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(0, await postgres.CountAsync("select count(*) from notes where tenant = $1", tenant));
        await host.StopAsync(Ct);
    }
}

[Operation("probe.start_superseded", "member", Effects = Effects.Write, Idempotency = Idempotency.Idempotent)]
public sealed record StartSuperseded(string Tenant, bool Helper) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

public sealed record SupersededInput(string Tenant, bool Helper);

internal sealed class StartSupersededHandler(IJobs jobs) : IOperationHandler<StartSuperseded, Unit>
{
    public async Task<Result<Unit>> HandleAsync(StartSuperseded operation, OperationContext context, CancellationToken ct)
    {
        await jobs.StartAsync(new SupersededInput(operation.Tenant, operation.Helper), new JobOptions(MaxAttempts: 1), ct);
        return Unit.Value;
    }
}

/// <summary>Waits to be superseded, then reads and writes: through its context, or through the dispatcher as a helper would.</summary>
internal sealed class SupersededProcess(Dispatcher dispatcher) : IProcess<SupersededInput>
{
    public async Task RunAsync(SupersededInput input, ProcessContext context, CancellationToken ct)
    {
        var run = Superseded.For(input.Tenant);
        run.Started.TrySetResult();
        await run.Release.Task.WaitAsync(ct);
        var read = await context.SendAsync(new ListNotes(input.Tenant), ct);
        var write = new CreateNote(Guid.CreateVersion7(), input.Tenant, "stale");
        try
        {
            var written = input.Helper ? await dispatcher.SendAsync(write, context.Principal, ct) : await context.SendAsync(write, ct);
            run.Outcome.TrySetResult($"{(read.IsSuccess ? "read" : "no read")}; write {(written.IsSuccess ? "committed" : written.Error.Code)}");
        }
        catch (Exception e)
        {
            run.Outcome.TrySetResult(e is AttemptSuperseded ? $"{(read.IsSuccess ? "read" : "no read")}; write refused" : e.ToString());
            throw;
        }
    }
}

internal sealed class Superseded
{
    private static readonly ConcurrentDictionary<string, Superseded> Runs = new();

    public static Superseded For(string tenant) => Runs.GetOrAdd(tenant, _ => new Superseded());

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<string> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
