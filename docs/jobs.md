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

## Processes

Every process registers every worker through the shared composition; each chooses only which queues it works. A
process that works no queue only enqueues, and may enqueue kinds other processes work.

The app's own Oxbow job kinds, through the scoped `IOxbowClient`, commit or roll back with the operation too.
