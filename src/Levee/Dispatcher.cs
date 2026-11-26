using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Levee;

/// <summary>
/// Every operation, from every client and job, goes through here: find its contract, authorize the principal, open a unit
/// of work scoped and read-only as the contract says, admit, run its one handler, audit, commit. Each call gets its own DI
/// scope, shared by the unit of work and the handler. A subscription is authorized and scoped the same way as it opens.
/// In-process callers (the host, jobs) reach internal operations; clients reach them only through the <c>FromClient</c>
/// methods, which refuse them.
/// </summary>
public sealed partial class Dispatcher(IServiceScopeFactory scopes, OperationCatalog catalog, IAccessPolicy access, LeveeOptions options,
    TimeProvider time, ILogger<Dispatcher> log)
{
    public static readonly ActivitySource Source = new("Levee");

    public OperationCatalog Catalog => catalog;

    public async Task<Result<TResult>> SendAsync<TResult>(IOperation<TResult> operation, Principal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(principal);
        if (catalog.Find(operation.GetType()) is not { } descriptor)
        {
            return Error.NotFound(LeveeErrors.UnknownOperation, $"{operation.GetType().Name} isn't in the catalog.");
        }
        var result = await RunAsync(descriptor, operation, principal, ct);
        return result.IsSuccess ? Result<TResult>.Success((TResult)result.Value!) : result.Error;
    }

    /// <summary>An operation by name, its input and result as JSON, in-process: internal operations included (jobs run them).</summary>
    public Task<Result<JsonElement>> SendJsonAsync(string name, JsonElement input, Principal principal, CancellationToken ct) =>
        SendFoundAsync(Find(name, fromClient: false), input, principal, ct);

    /// <summary>As <see cref="SendJsonAsync"/>, for a client: what a projection sends. Internal operations aren't there.</summary>
    public Task<Result<JsonElement>> SendFromClientAsync(string name, JsonElement input, Principal principal, CancellationToken ct) =>
        SendFoundAsync(Find(name, fromClient: true), input, principal, ct);

    public async Task<Result<IAsyncEnumerable<TItem>>> SubscribeAsync<TItem>(ISubscription<TItem> subscription, Principal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (catalog.Find(subscription.GetType()) is not { } descriptor)
        {
            return Error.NotFound(LeveeErrors.UnknownOperation, $"{subscription.GetType().Name} isn't in the catalog.");
        }
        var opened = await OpenAsync(descriptor, subscription, principal, ct);
        return opened.IsSuccess ? Result<IAsyncEnumerable<TItem>>.Success(Items<TItem>(opened.Value, CancellationToken.None)) : opened.Error;
    }

    /// <summary>A subscription by name, its input and items as JSON, in-process.</summary>
    public Task<Result<IAsyncEnumerable<JsonElement>>> SubscribeJsonAsync(string name, JsonElement input, Principal principal, CancellationToken ct) =>
        SubscribeFoundAsync(Find(name, fromClient: false), input, principal, ct);

    /// <summary>As <see cref="SubscribeJsonAsync"/>, for a client. Internal subscriptions aren't there.</summary>
    public Task<Result<IAsyncEnumerable<JsonElement>>> SubscribeFromClientAsync(string name, JsonElement input, Principal principal, CancellationToken ct) =>
        SubscribeFoundAsync(Find(name, fromClient: true), input, principal, ct);

    private Result<OperationDescriptor> Find(string name, bool fromClient) =>
        catalog.Find(name) is { } found && !(fromClient && found.Internal)
            ? found
            : Error.NotFound(LeveeErrors.UnknownOperation, $"There's no operation {name}.");

    private async Task<Result<IAsyncEnumerable<JsonElement>>> SubscribeFoundAsync(Result<OperationDescriptor> found, JsonElement input, Principal principal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!found.IsSuccess)
        {
            return found.Error;
        }
        var descriptor = found.Value;
        if (!descriptor.Streams)
        {
            return Error.Validation(LeveeErrors.AnswersOnce, $"{descriptor.Name} answers once: send it.");
        }
        var subscription = OperationCatalog.Deserialize(descriptor, input, options.Json);
        if (!subscription.IsSuccess)
        {
            return subscription.Error;
        }
        var opened = await OpenAsync(descriptor, subscription.Value, principal, ct);
        return opened.IsSuccess ? Result<IAsyncEnumerable<JsonElement>>.Success(Json(opened.Value, descriptor.ResultType, CancellationToken.None)) : opened.Error;
    }

    // Each wrapper passes its reader's cancellation through, or a stream stopped by its reader would leave its handler running.
    private async IAsyncEnumerable<JsonElement> Json(IAsyncEnumerable<object?> items, Type type,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in items.WithCancellation(ct))
        {
            yield return JsonSerializer.SerializeToElement(item, type, options.Json);
        }
    }

    private static async IAsyncEnumerable<TItem> Items<TItem>(IAsyncEnumerable<object?> items,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in items.WithCancellation(ct))
        {
            yield return (TItem)item!;
        }
    }

    /// <summary>Scoped and authorized now; its items come from a DI scope of its own that lives as long as they're read.</summary>
    private async Task<Result<IAsyncEnumerable<object?>>> OpenAsync(OperationDescriptor descriptor, IOperation subscription, Principal principal,
        CancellationToken ct)
    {
        var scoped = ScopeOf(descriptor, subscription, principal);
        Error? denied;
        try
        {
            denied = scoped.IsSuccess ? await AuthorizeAsync(descriptor, subscription, principal, scoped.Value, ct) : scoped.Error;
            if (denied is null && scoped.IsSuccess)
            {
                denied = await AdmitAsync(descriptor, subscription, principal, scoped.Value, ct);
            }
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            // A policy or an admission that throws is a bug, answered as one, the same as in an operation.
            denied = Unexpected(e, descriptor);
        }
        await RecordOutcomeAsync(new OperationOutcome(descriptor, SafeRedact(subscription), scoped.IsSuccess ? scoped.Value : null, principal, denied), ct);
        return denied is null
            ? Result<IAsyncEnumerable<object?>>.Success(Stream(descriptor, subscription, new SubscriptionContext(principal, descriptor, scoped.Value), CancellationToken.None))
            : denied;
    }

    /// <summary>A handler that throws mid-stream ends it with <see cref="OperationFailedException"/>, logged like a failed operation.</summary>
    private async IAsyncEnumerable<object?> Stream(OperationDescriptor descriptor, IOperation subscription, SubscriptionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var services = scopes.CreateAsyncScope();
        await using var items = descriptor.SubscribeAsync(services.ServiceProvider, subscription, context, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            try
            {
                if (!await items.MoveNextAsync())
                {
                    yield break;
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                throw new OperationFailedException(Unexpected(e, descriptor), e);
            }
            yield return items.Current;
        }
    }

    /// <summary>A bug: logged once with everything, answered with nothing but the trace to find it by.</summary>
    private Error Unexpected(Exception exception, OperationDescriptor descriptor)
    {
        var trace = Activity.Current?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        LogUnexpected(log, exception, descriptor.Name, trace);
        return Error.Internal(trace);
    }

    private async Task<Result<JsonElement>> SendFoundAsync(Result<OperationDescriptor> found, JsonElement input, Principal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!found.IsSuccess)
        {
            return found.Error;
        }
        var descriptor = found.Value;
        if (descriptor.Streams)
        {
            return Error.Validation(LeveeErrors.Streams, $"{descriptor.Name} streams: subscribe to it.");
        }
        var operation = OperationCatalog.Deserialize(descriptor, input, options.Json);
        if (!operation.IsSuccess)
        {
            return operation.Error;
        }
        // Serialized before the commit: a result that can't be is a bug the write rolls back with, not one the caller meets after.
        var result = await RunAsync(descriptor, operation.Value, principal,
            value => JsonSerializer.SerializeToElement(value, descriptor.ResultType, options.Json), ct);
        return result.IsSuccess ? (JsonElement)result.Value! : result.Error;
    }

    private async Task<Result<object?>> RunAsync(OperationDescriptor descriptor, IOperation operation, Principal principal, CancellationToken ct) =>
        await RunAsync(descriptor, operation, principal, answer: null, ct);

    private async Task<Result<object?>> RunAsync(OperationDescriptor descriptor, IOperation operation, Principal principal,
        Func<object?, object?>? answer, CancellationToken ct)
    {
        using var activity = Source.StartActivity(descriptor.Name);
        activity?.SetTag("levee.operation", descriptor.Name);
        var scoped = ScopeOf(descriptor, operation, principal);
        Result<object?> result;
        try
        {
            result = scoped.IsSuccess ? await RunCoreAsync(descriptor, operation, principal, scoped.Value, answer, ct) : scoped.Error;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller stopped waiting: it still ended, rolled back, and the outcome is told so before the cancel goes on.
            await RecordOutcomeAsync(new OperationOutcome(descriptor, SafeRedact(operation), scoped.IsSuccess ? scoped.Value : null, principal,
                Error.Unavailable(LeveeErrors.Cancelled, $"{descriptor.Name} was cancelled by its caller.")), CancellationToken.None);
            throw;
        }
        catch (Exception e)
        {
            // Rolled back already: the unit of work was disposed on the way out.
            result = Unexpected(e, descriptor);
        }
        // After the commit, the caller's cancel can't undo the operation, so it doesn't stop it being told either.
        await RecordOutcomeAsync(new OperationOutcome(descriptor, SafeRedact(operation), scoped.IsSuccess ? scoped.Value : null, principal,
            result.IsSuccess ? null : result.Error), CancellationToken.None);
        activity?.SetTag("levee.outcome", result.IsSuccess ? "ok" : result.Error.Code);
        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }
        return result;
    }

    /// <summary>
    /// Whether <paramref name="principal"/> may run <paramref name="operation"/>, without running it: its scope, then the
    /// access policy. What every call is checked by first, and what a job's enqueue is checked by, as its caller.
    /// </summary>
    /// <returns>Null to allow; the refusal otherwise.</returns>
    public async Task<Error?> AuthorizeAsync(IOperation operation, Principal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(principal);
        if (catalog.Find(operation.GetType()) is not { } descriptor)
        {
            return Error.NotFound(LeveeErrors.UnknownOperation, $"{operation.GetType().Name} isn't in the catalog.");
        }
        var scope = ScopeOf(descriptor, operation, principal);
        return scope.IsSuccess ? await AuthorizeAsync(descriptor, operation, principal, scope.Value, ct) : scope.Error;
    }

    /// <summary>As <see cref="AuthorizeAsync(IOperation, Principal, CancellationToken)"/>, by name and JSON, for a client.</summary>
    public async Task<Error?> AuthorizeFromClientAsync(string name, JsonElement input, Principal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var found = Find(name, fromClient: true);
        if (!found.IsSuccess)
        {
            return found.Error;
        }
        var operation = OperationCatalog.Deserialize(found.Value, input, options.Json);
        if (!operation.IsSuccess)
        {
            return operation.Error;
        }
        var scope = ScopeOf(found.Value, operation.Value, principal);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }
        try
        {
            return await AuthorizeAsync(found.Value, operation.Value, principal, scope.Value, ct)
                ?? await AdmitAsync(found.Value, operation.Value, principal, scope.Value, ct);
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            return Unexpected(e, found.Value);
        }
    }

    /// <summary>
    /// The admissions alone, in a read-only unit of work of their own: what a subscription, which holds no transaction while it
    /// streams, is admitted by as it opens and each time a projection checks it again.
    /// </summary>
    private async Task<Error?> AdmitAsync(OperationDescriptor descriptor, IOperation operation, Principal principal, WorkScope scope, CancellationToken ct)
    {
        await using var services = scopes.CreateAsyncScope();
        var admissions = services.ServiceProvider.GetServices<IAdmission>().ToList();
        if (admissions.Count == 0)
        {
            return null;
        }
        var factory = services.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
        try
        {
            var reading = scope with { ReadOnly = true };
            await using var unit = await factory.BeginAsync(reading, ct);
            var context = new OperationContext(principal, descriptor, reading, Now());
            services.ServiceProvider.GetRequiredService<CurrentOperation>().Context = context;
            foreach (var admission in admissions)
            {
                if (await admission.AdmitAsync(operation, context, ct) is { } refused)
                {
                    return refused;
                }
            }
            return null;
        }
        catch (Exception e) when (factory.Classify(e) is { } error)
        {
            return error;
        }
    }

    /// <summary>The operation's one time. Postgres keeps microseconds; Linux's clock ticks in 100ns, so an unrounded time wouldn't read back equal.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private async Task<Error?> AuthorizeAsync(OperationDescriptor descriptor, IOperation operation, Principal principal, WorkScope scope,
        CancellationToken ct)
    {
        var denied = await access.AuthorizeAsync(new AccessRequest(principal, descriptor, operation, scope), ct);
        if (denied is not null)
        {
            LogDenied(log, descriptor.Name, denied.Code);
        }
        return denied;
    }

    /// <summary>
    /// Where the operation runs, as it declares: its input's scope, its caller's, or every scope (platform). A scope it needs
    /// and lacks is a refusal, never the platform's by default.
    /// </summary>
    private static Result<WorkScope> ScopeOf(OperationDescriptor descriptor, IOperation operation, Principal principal) =>
        (descriptor.Platform, descriptor.CallerScoped, operation) switch
        {
            (true, false, not IScopedOperation) => new WorkScope(null, descriptor.ReadOnly),
            (false, true, not IScopedOperation) => principal is IScopedPrincipal { ScopeKey: { } key } && !string.IsNullOrWhiteSpace(key)
                ? new WorkScope(key, descriptor.ReadOnly)
                : Error.Forbidden(LeveeErrors.CallerUnscoped, $"{descriptor.Name} runs in its caller's scope, and this caller has none."),
            (false, false, IScopedOperation { ScopeKey: { } key }) when !string.IsNullOrWhiteSpace(key) => new WorkScope(key, descriptor.ReadOnly),
            (false, false, IScopedOperation) => Error.Validation(LeveeErrors.ScopeMissing, $"{descriptor.Name} needs its scope."),
            _ => Error.Forbidden(LeveeErrors.ScopeUndeclared, $"{descriptor.Name} must declare one scope: its input's, its caller's, or the platform's."),
        };

    private async Task<Result<object?>> RunCoreAsync(OperationDescriptor descriptor, IOperation operation, Principal principal, WorkScope scope,
        Func<object?, object?>? answer, CancellationToken ct)
    {
        if (await AuthorizeAsync(descriptor, operation, principal, scope, ct) is { } denied)
        {
            return denied;
        }

        await using var services = scopes.CreateAsyncScope();
        var factory = services.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
        try
        {
            await using var unit = await factory.BeginAsync(scope, ct);
            var context = new OperationContext(principal, descriptor, scope, Now());
            services.ServiceProvider.GetRequiredService<CurrentOperation>().Context = context;
            foreach (var admission in services.ServiceProvider.GetServices<IAdmission>())
            {
                if (await admission.AdmitAsync(operation, context, ct) is { } refused)
                {
                    return refused;
                }
            }
            var result = await descriptor.InvokeAsync(services.ServiceProvider, operation, context, ct);
            if (!result.IsSuccess && ReferenceEquals(result.Error, Error.Uninitialized))
            {
                throw new InvalidOperationException($"{descriptor.Name}'s handler returned default(Result<T>): neither a value nor an error.");
            }
            if (!result.IsSuccess)
            {
                if (!descriptor.Declares(result.Error.Code))
                {
                    if (options.RefuseUndeclaredErrors)
                    {
                        // A test switch: the answer names what to declare, where production would only log it.
                        var undeclared = Unexpected(new InvalidOperationException($"{descriptor.Name} failed with {result.Error.Code}, which its contract doesn't declare."), descriptor);
                        return undeclared with
                        {
                            Message = $"{descriptor.Name} failed with '{result.Error.Code}', which it doesn't declare: add it to [Operation(..., Errors = [...])]. ({undeclared.Trace})",
                        };
                    }
                    LogUndeclaredError(log, descriptor.Name, result.Error.Code);
                }
                return result;
            }
            if (answer is not null)
            {
                result = Result<object?>.Success(answer(result.Value));
            }
            if (descriptor.Audited)
            {
                await unit.AuditAsync(new AuditEntry(context.Now, scope.Key, principal.Subject, descriptor.Name, Redact(operation)), ct);
            }
            // Once COMMIT is sent, a cancelled wait would leave the caller not knowing whether it happened.
            await unit.CommitAsync(CancellationToken.None);
            return result;
        }
        catch (Exception e) when (factory.Classify(e) is { } error)
        {
            LogRefusedByStore(log, descriptor.Name, error.Code);
            return error.Code == LeveeErrors.UndeclaredWrite
                ? Error.Forbidden(error.Code, $"{descriptor.Name} tried to write, but its contract declares only reads.")
                : error;
        }
    }

    /// <summary>
    /// The operation's own result stands, committed or not: a recorder failing (to be built, to record, within
    /// <see cref="LeveeOptions.OutcomeTimeout"/>, to be disposed) is its bug, not the caller's.
    /// </summary>
    private async Task RecordOutcomeAsync(OperationOutcome outcome, CancellationToken ct)
    {
        var recorder = "the recorders' scope";
        try
        {
            await using var services = scopes.CreateAsyncScope();
            foreach (var each in services.ServiceProvider.GetServices<IOperationOutcome>())
            {
                recorder = each.GetType().Name;
                try
                {
                    // Bounded: after the commit the caller is waiting, and a recorder that hangs is its bug.
                    using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    bounded.CancelAfter(options.OutcomeTimeout);
                    // On the pool: a recorder that blocks before its first await is bounded too.
                    await Task.Run(() => each.RecordAsync(outcome, bounded.Token), CancellationToken.None).WaitAsync(options.OutcomeTimeout, CancellationToken.None);
                }
                catch (Exception e)
                {
                    LogOutcomeFailed(log, e, outcome.Operation.Name, recorder);
                }
            }
            recorder = "the recorders' scope";
        }
        catch (Exception e)
        {
            LogOutcomeFailed(log, e, outcome.Operation.Name, recorder);
        }
    }

    /// <summary>The input as the audit keeps it: every [Secret] and [Personal] value replaced, at any depth.</summary>
    private string Redact(IOperation operation) => LeveeJson.Redact(operation, operation.GetType(), options.Json);

    /// <summary>
    /// The input for an outcome, which is told after the commit: an input that can't be serialized (a getter that throws) is a
    /// bug, logged, and the outcome is still told. The audit's own redaction fails the operation instead, before it commits.
    /// </summary>
    private string SafeRedact(IOperation operation)
    {
        try
        {
            return Redact(operation);
        }
        catch (Exception e)
        {
            LogUnredactable(log, e, operation.GetType().Name);
            return "\"[unredactable]\"";
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Denied {Operation}: {Reason}")]
    private static partial void LogDenied(ILogger logger, string operation, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording how {Operation} ended failed in {Recorder}")]
    private static partial void LogOutcomeFailed(ILogger logger, Exception exception, string operation, string recorder);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Input}, the input of an operation, couldn't be serialized for its outcome")]
    private static partial void LogUnredactable(ILogger logger, Exception exception, string input);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Operation} failed unexpectedly (trace {Trace})")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, string operation, string trace);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Operation} failed with {Code}, which its contract doesn't declare: add it to [Operation(Errors = ...)]")]
    private static partial void LogUndeclaredError(ILogger logger, string operation, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Operation} was refused by the store: {Reason}")]
    private static partial void LogRefusedByStore(ILogger logger, string operation, string reason);
}

/// <summary>An operation that failed where no result can carry it: a subscription whose handler threw mid-stream.</summary>
public sealed class OperationFailedException(Error error, Exception inner) : Exception($"{error.Code}: {error.Message}", inner)
{
    public Error Error { get; } = error;
}

public sealed class LeveeOptions
{
    public JsonSerializerOptions Json { get; set; } = LeveeJson.Options;

    /// <summary>
    /// A handler failing with a code its operation doesn't declare is a bug, answered <c>internal</c>, rather than only
    /// logged. For tests, where every path an assertion takes is checked against the contract; production keeps the code.
    /// </summary>
    public bool RefuseUndeclaredErrors { get; set; }

    /// <summary>How often a projection streaming a subscription writes something, so an idle proxy doesn't close it.</summary>
    public TimeSpan SubscriptionKeepAlive { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How often a projection checks again that a subscription's caller may still read it (a revoked key, a lost
    /// membership), ending the stream when not: a subscription is authorized as it opens, and may outlive that.
    /// </summary>
    public TimeSpan SubscriptionRecheck { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long each <see cref="IOperationOutcome"/> has to record how an operation ended. The caller waits for them, after
    /// the commit too, so one that takes longer is cancelled, logged, and left running, unawaited.
    /// </summary>
    public TimeSpan OutcomeTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
