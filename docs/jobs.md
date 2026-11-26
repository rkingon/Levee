# Jobs

Jobs are Oxbow's, enlisted in Levee's unit of work: a job commits or rolls back with the operation that enqueued it, so a
post-commit side effect (an email, a vendor call, a handover) is a job, with no window to lose it.

```csharp
services.AddLeveeOxbow(o => o.Queues["default"] = new QueueOptions { MaxWorkers = 10 })
    .AddLeveeJobs(sealSecrets: true);   // with Oxbow's AddArgsEncryption, for inputs that carry secrets
```

## An operation as a job

```csharp
await jobs.EnqueueAsync(new SendReceipt(order.Id), new JobOptions { Queue = "mail" }, ct);
```

- **Authorized as the caller**, now: the access policy decides at enqueue, and again when the job runs.
- **Enqueued in the caller's transaction:** if the operation rolls back, the job was never enqueued.
- **Run as a `JobPrincipal`** for whoever enqueued it, in the scope their credentials carried (a caller-scoped
  operation's). Register an `IJobPrincipals` to turn that subject back into the app's own principal.
- **At least once.** An operation declaring `Idempotency.None` can't be enqueued. One carrying a `[Secret]` anywhere in its
  input is refused unless jobs seal their inputs.
- **Failures:** a transient one (`Unavailable`, `internal`) is retried with Oxbow's backoff, or after the failure's own
  `RetryAfter` when it has one (a vendor's rate limit); anything else cancels the job.

## Processes

Every process registers every worker through the shared composition; each chooses only which queues it works. A
process that works no queue only enqueues, and may enqueue kinds other processes work.

The app's own Oxbow job kinds, through the scoped `IOxbowClient`, commit or roll back with the operation too.
