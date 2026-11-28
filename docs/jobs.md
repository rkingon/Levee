# Jobs

Jobs are Oxbow's, enlisted in Levee's unit of work: a job commits or rolls back with the operation that enqueued it, so a
post-commit side effect (an email, a vendor call, a handover) is a job, with no window to lose it.

```csharp
services.AddLeveeOxbow(o => o.Queues["default"] = new QueueOptions { MaxWorkers = 10 })
    .AddLeveeJobs(sealSecrets: true);   // with Oxbow's AddArgsEncryption, for inputs that carry secrets
```

## Committed together

```csharp
internal sealed class CreateAccountHandler(IAccounts accounts, IJobs jobs) : IOperationHandler<CreateAccount, AccountView>
{
    public async Task<Result<AccountView>> HandleAsync(CreateAccount operation, OperationContext context, CancellationToken ct)
    {
        var account = Account.Open(operation.Id, operation.Email, context.Now);
        accounts.Add(account);
        var queued = await jobs.EnqueueAsync(new SendWelcomeEmail(account.Id), new JobOptions { Queue = "mail" }, ct);
        if (!queued.IsSuccess)
        {
            return queued.Error;   // refused: the account isn't created either
        }
        return AccountView.Of(account);
    }
}
```

The account and its welcome-email job commit together. If the transaction rolls back, neither is saved. If the process
dies right after the commit, the job is already in Postgres for a worker to pick up. What the commit guarantees is that
the job is **durably queued**; running it, its retries, and the email's delivery happen afterward. Publishing an
integration event is the same shape: the event is the job.

## An operation as a job

- **Authorized as the caller**, now: the access policy decides at enqueue, and again when the job runs.
- **Enqueued in the caller's transaction:** if the operation rolls back, the job was never enqueued.
- **Run as a `JobPrincipal`** for whoever enqueued it, in the scope it was authorized in at enqueue (a caller-scoped
  operation's). Register an `IJobPrincipals` to turn that subject back into the app's own principal; a principal it
  restores as another subject, or a caller-scoped run whose caller is now scoped elsewhere, cancels the job rather than
  moving it.
- **At least once.** An operation declaring `Idempotency.None` can't be enqueued, nor a subscription (a job answers once). One carrying a `[Secret]` anywhere in its
  input is refused unless jobs seal their inputs.
- **Failures:** a transient one (`Unavailable`, `internal`) is retried with Oxbow's backoff, or after the failure's own
  `RetryAfter` when it has one (a vendor's rate limit); anything else cancels the job.

## Long-running work

An operation is one transaction, so work that runs for minutes (an agent's turn, a backfill between pauses) isn't one
operation. It's a process: an `IProcess<TInput>` in Application, started by an operation and run as a job, outside any
transaction. Every step is an operation it sends, as the caller that started it and in the scope it was started in, so
each commits on its own and nothing is held open while it waits. Its I/O goes through ports, as a handler's does.

```csharp
// Application
internal sealed class RunTurn(IAgentRunner runner) : IProcess<TurnStarted>
{
    public async Task RunAsync(TurnStarted turn, ProcessContext context, CancellationToken ct)
    {
        var state = (await context.SendAsync(new GetTurn(turn.TurnId), ct)).Value;
        if (state.Finished) return;                                   // a retry or a snooze starts from the top
        var reply = await runner.RunAsync(state.Prompt, ct);          // minutes, no transaction open
        await context.SendAsync(new FinishTurn(turn.TurnId, reply), ct);
    }
}

// The operation that starts it: inserted with its transaction, run only if it commits
await jobs.StartAsync(new TurnStarted(turn.Id), null, ct);
```

Its state is what its operations commit (a process manager's aggregate): a run that's retried, or one that snoozes
(`context.Snooze(delay)`) to come back later, starts again from the top and reads where it was. A snooze isn't an
attempt, so `context.Attempt` doesn't say where a process is: its state does.

### Steps

Reading where it was needs an operation that knows; a step needs none. `context.StepAsync(name, run, ct)` records a
step's result under its name once it succeeds, and every later run (after a failed attempt, a snooze, a rescue) gets the
recorded result back instead of running it again, so a process written from the top resumes where it was:

```csharp
var reply = await context.StepAsync("ask the model", ct => runner.RunAsync(state.Prompt, ct), ct);   // done once
if (!reply.IsSuccess) context.Snooze(TimeSpan.FromSeconds(30));                                       // nothing recorded: asked again
await context.StepAsync("finish", ct => context.SendAsync(new FinishTurn(turn.TurnId, reply.Value), ct), ct);
```

- **A step answers a `Result`.** Only a success is recorded; a failure is answered as it is and the next run tries the
  step again. A port's call and `SendAsync` both answer one.
- **At least once.** A result is recorded after its step runs, so a run that dies in between runs it again: a vendor call
  takes an idempotency key.
- **Names identify results.** Name a step for what it does, unique in the process; renaming it runs it again.
- **Results travel on the contract wire** (`LeveeJson.Options`), kept with the job (Oxbow's `job_step`). A result
  carrying a `[Secret]` is refused unless jobs seal their inputs, which seals step results too.
- **A superseded run can't write.** An attempt that outlives its timeout or heartbeat can be rescued while it still runs.
  Every write a process sends takes Oxbow's attempt lock first, in the operation's own transaction, so a run the rescuer
  has taken over rolls back and ends there; its rescuer's run is the process now. A read takes no lock.

Needs Oxbow's schema version 3 (0.2.0-alpha.5) and its grants: `dotnet levee db schema --jobs` and `db grants --jobs`.

A process started in a scope (or in none) stays there: every step runs in that scope, whatever the step declares (its
input's, its caller's, the platform's), even when the access policy would let its caller elsewhere. Its principal is
held to that scope, not its context: handed to anything else that sends, it's refused outside the scope all the same. One the platform started may act
in any scope, as the platform could. A stored process resolves only to an input type some registered process takes;
anything else is cancelled, never loaded. The generated
registration finds a process in Application; its input is stored, so renaming the input type strands runs still queued,
and it carries no `[Secret]` unless jobs seal their inputs.

## Which process works which queue

Every process registers every worker through the shared composition; each chooses only which queues it works. A
process that works no queue only enqueues, and may enqueue kinds other processes work.

The app's own Oxbow job kinds, through the scoped `IOxbowClient`, commit or roll back with the operation too.
