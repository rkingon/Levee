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

    public Task<Result<TResult>> SendAsync<TResult>(IOperation<TResult> operation, Principal principal, CancellationToken ct) =>
        SendAsync(operation, principal, idempotencyKey: null, ct);

    /// <summary>
    /// As <see cref="SendAsync{TResult}(IOperation{TResult}, Principal, CancellationToken)"/>, retryable: a write sent again
    /// with the same <paramref name="idempotencyKey"/>, by the same caller in the same scope, gets the first result back
    /// instead of writing twice, and an equal request still in flight is waited for. What an app's own projection passes on
    /// from its caller (the <c>Idempotency-Key</c> header). A read ignores it.
    /// </summary>
    public async Task<Result<TResult>> SendAsync<TResult>(IOperation<TResult> operation, Principal principal, string? idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(principal);
        if (catalog.Find(operation.GetType()) is not { } descriptor)
        {
            return Error.NotFound(LeveeErrors.UnknownOperation, $"{operation.GetType().Name} isn't in the catalog.");
        }
        var result = await RunAsync(descriptor, operation, principal, answer: null, pinned: null, idempotencyKey, ct);
        return result.IsSuccess ? Result<TResult>.Success((TResult)result.Value!) : result.Error;
    }

    /// <summary>An operation by name, its input and result as JSON, in-process: internal operations included (jobs run them).</summary>
    internal Task<Result<JsonElement>> SendJsonAsync(string name, JsonElement input, Principal principal, CancellationToken ct) =>
        SendFoundAsync(Find(name, fromClient: false), input, principal, pinned: null, key: null, ct);

    /// <summary>As <see cref="SendJsonAsync(string, JsonElement, Principal, CancellationToken)"/>, for work asked for earlier, in the caller's scope as it was then.</summary>
    internal Task<Result<JsonElement>> SendJsonAsync(string name, JsonElement input, Principal principal, string? pinned, CancellationToken ct) =>
        SendFoundAsync(Find(name, fromClient: false), input, principal, pinned, key: null, ct);

    /// <summary>As <see cref="SendJsonAsync(string, JsonElement, Principal, CancellationToken)"/>, for a client: what a projection sends. Internal operations aren't there.</summary>
    internal Task<Result<JsonElement>> SendFromClientAsync(string name, JsonElement input, Principal principal, CancellationToken ct) =>
        SendFoundAsync(Find(name, fromClient: true), input, principal, pinned: null, key: null, ct);

    /// <summary>As <see cref="SendFromClientAsync(string, JsonElement, Principal, CancellationToken)"/>, with the client's idempotency key.</summary>
    internal Task<Result<JsonElement>> SendFromClientAsync(string name, JsonElement input, Principal principal, string? idempotencyKey, CancellationToken ct) =>
        SendFoundAsync(Find(name, fromClient: true), input, principal, pinned: null, idempotencyKey, ct);

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
    internal Task<Result<IAsyncEnumerable<JsonElement>>> SubscribeJsonAsync(string name, JsonElement input, Principal principal, CancellationToken ct) =>
        SubscribeFoundAsync(Find(name, fromClient: false), input, principal, ct);

    /// <summary>As <see cref="SubscribeJsonAsync"/>, for a client. Internal subscriptions aren't there.</summary>
    internal Task<Result<IAsyncEnumerable<JsonElement>>> SubscribeFromClientAsync(string name, JsonElement input, Principal principal, CancellationToken ct) =>
        SubscribeFoundAsync(Find(name, fromClient: true), input, principal, ct);

    /// <summary>
    /// As <see cref="SubscribeFromClientAsync"/>, remembering who it opened for and in which scope, so a projection's recheck
    /// (<see cref="RecheckAsync"/>) asks about that same stream, not about where its caller is now.
    /// </summary>
    internal async Task<Result<OpenedSubscription>> OpenFromClientAsync(string name, JsonElement input, Principal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var found = Find(name, fromClient: true);
        if (!found.IsSuccess)
        {
            return found.Error;
        }
        var descriptor = found.Value;
        if (!descriptor.Streams)
        {
            return Error.Validation(LeveeErrors.AnswersOnce, $"{descriptor.Name} answers once: send it.");
        }
        var subscription = OperationCatalog.Deserialize(descriptor, input, LeveeJson.Options, e => LogInputRefused(log, e, descriptor.Name));
        if (!subscription.IsSuccess)
        {
            return subscription.Error;
        }
        var subject = principal.Subject;
        var scoped = Confine(ScopeOf(descriptor, subscription.Value, principal), descriptor, principal);
        var opened = await OpenAsync(descriptor, subscription.Value, principal, scoped, ct);
        return opened.IsSuccess
            ? new OpenedSubscription(Json(opened.Value, descriptor, CancellationToken.None), descriptor, subscription.Value, subject, scoped.Value)
            : opened.Error;
    }

    /// <summary>
    /// Whether the stream may go on: <paramref name="now"/> is the same subject, in the scope it opened in, and the access
    /// policy and admissions still allow that scope.
    /// </summary>
    internal async Task<Error?> RecheckAsync(OpenedSubscription opened, Principal? now, CancellationToken ct)
    {
        if (now is null)
        {
            return Error.Unauthenticated("The caller's credentials no longer hold.");
        }
        if (now.Subject != opened.Subject)
        {
            return Error.Unauthenticated("The stream's caller changed: open it again.");
        }
        var scope = ScopeOf(opened.Descriptor, opened.Subscription, now, opened.Scope.Key);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }
        try
        {
            return await AuthorizeAsync(opened.Descriptor, opened.Subscription, now, opened.Scope, ct)
                ?? await AdmitAsync(opened.Descriptor, opened.Subscription, now, opened.Scope, ct);
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            return Unexpected(e, opened.Descriptor);
        }
    }

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
        var subscription = OperationCatalog.Deserialize(descriptor, input, LeveeJson.Options, e => LogInputRefused(log, e, descriptor.Name));
        if (!subscription.IsSuccess)
        {
            return subscription.Error;
        }
        var opened = await OpenAsync(descriptor, subscription.Value, principal, ct);
        return opened.IsSuccess ? Result<IAsyncEnumerable<JsonElement>>.Success(Json(opened.Value, descriptor, CancellationToken.None)) : opened.Error;
    }

    // Each wrapper passes its reader's cancellation through, or a stream stopped by its reader would leave its handler running.
    private async IAsyncEnumerable<JsonElement> Json(IAsyncEnumerable<object?> items, OperationDescriptor descriptor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in items.WithCancellation(ct))
        {
            JsonElement json;
            try
            {
                json = JsonSerializer.SerializeToElement(item, descriptor.ResultType, LeveeJson.Options);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // An item that can't be written ends the stream as a handler's bug would: logged, and told as one last error.
                throw new OperationFailedException(Unexpected(e, descriptor), e);
            }
            yield return json;
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
    private Task<Result<IAsyncEnumerable<object?>>> OpenAsync(OperationDescriptor descriptor, IOperation subscription, Principal principal,
        CancellationToken ct) =>
        OpenAsync(descriptor, subscription, principal, Confine(ScopeOf(descriptor, subscription, principal), descriptor, principal), ct);

    private async Task<Result<IAsyncEnumerable<object?>>> OpenAsync(OperationDescriptor descriptor, IOperation subscription, Principal principal,
        Result<WorkScope> scoped, CancellationToken ct)
    {
        Error? denied;
        try
        {
            denied = scoped.IsSuccess ? await AuthorizeAsync(descriptor, subscription, principal, scoped.Value, ct) : scoped.Error;
            if (denied is null && scoped.IsSuccess)
            {
                denied = await AdmitAsync(descriptor, subscription, principal, scoped.Value, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordOutcomeAsync(new OperationOutcome(descriptor, SafeRedact(subscription), scoped.IsSuccess ? scoped.Value : null, principal,
                Error.Unavailable(LeveeErrors.Cancelled, $"{descriptor.Name} was cancelled by its caller as it opened.")), CancellationToken.None);
            throw;
        }
        catch (Exception e)
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
        services.ServiceProvider.GetRequiredService<CurrentSubscription>().Context = context;
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

    private async Task<Result<JsonElement>> SendFoundAsync(Result<OperationDescriptor> found, JsonElement input, Principal principal, string? pinned,
        string? key, CancellationToken ct)
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
        var operation = OperationCatalog.Deserialize(descriptor, input, LeveeJson.Options, e => LogInputRefused(log, e, descriptor.Name));
        if (!operation.IsSuccess)
        {
            return operation.Error;
        }
        // Serialized before the commit: a result that can't be is a bug the write rolls back with, not one the caller meets after.
        var result = await RunAsync(descriptor, operation.Value, principal,
            value => JsonSerializer.SerializeToElement(value, descriptor.ResultType, LeveeJson.Options), pinned, key, ct);
        return result.IsSuccess ? (JsonElement)result.Value! : result.Error;
    }

    // The principal a process runs as, held to the scope it was started in, by whatever path it reaches the dispatcher: the
    // process's own context, or a helper it hands the principal to. Keyed by the instance, which only the process holds.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Principal, WorkScope> Confined = [];

    /// <summary>
    /// Holds <paramref name="principal"/> to <paramref name="scope"/>: every operation it sends from now on runs there,
    /// whatever it declares (its input's scope, its caller's, none, the platform's), though the access policy might let the
    /// caller elsewhere. A process started by the platform isn't confined: it may act in any scope, as the platform could.
    /// </summary>
    internal static void Confine(Principal principal, WorkScope scope) => Confined.AddOrUpdate(principal, scope);

    // What holds a process's writes to its attempt, by the run's own principal, as its confinement is: a helper it hands the
    // principal to sends through the same lock as its context does.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Principal, Func<IServiceProvider, CancellationToken, Task>> Attempts = [];

    /// <summary>
    /// Holds every write <paramref name="principal"/> sends to its process's attempt: <paramref name="enlist"/> runs first in
    /// each write's transaction (Oxbow's attempt lock), so a superseded run's write can't commit, and throws
    /// <see cref="AttemptSuperseded"/> when the attempt is no longer the one running.
    /// </summary>
    internal static void HoldToAttempt(Principal principal, Func<IServiceProvider, CancellationToken, Task> enlist) => Attempts.AddOrUpdate(principal, enlist);

    /// <summary>A process's step: an operation it holds, as its principal.</summary>
    internal async Task<Result<object?>> SendAsStepAsync(IOperation operation, Principal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (catalog.Find(operation.GetType()) is not { } descriptor)
        {
            return Error.NotFound(LeveeErrors.UnknownOperation, $"{operation.GetType().Name} isn't in the catalog.");
        }
        return await RunAsync(descriptor, operation, principal, answer: null, pinned: null, key: null, ct);
    }

    private static Result<WorkScope> Confine(Result<WorkScope> scoped, OperationDescriptor descriptor, Principal principal) =>
        scoped.IsSuccess && Confined.TryGetValue(principal, out var to) && (scoped.Value.Key != to.Key || scoped.Value.Unscoped != to.Unscoped)
            ? Error.Forbidden(LeveeErrors.ScopeViolation,
                $"{descriptor.Name} would run {(scoped.Value.Key is { } key ? $"in {key}" : scoped.Value.Unscoped ? "in no one's scope" : "across every scope")}, "
                + "outside the scope its process was started in.")
            : scoped;

    private async Task<Result<object?>> RunAsync(OperationDescriptor descriptor, IOperation operation, Principal principal,
        Func<object?, object?>? answer, string? pinned, string? key, CancellationToken ct)
    {
        var enlist = Attempts.TryGetValue(principal, out var attempt) ? attempt : null;
        using var activity = Source.StartActivity(descriptor.Name);
        activity?.SetTag("levee.operation", descriptor.Name);
        var scoped = Confine(ScopeOf(descriptor, operation, principal, pinned), descriptor, principal);
        // Redacted once, for the audit and the outcome both. An input built in-process can hold what its own type refuses
        // (null where it's non-nullable): refused here, as the wire would, before any transaction opens.
        string? input = null;
        Result<object?>? refused = null;
        try
        {
            input = Redact(operation);
        }
        catch (JsonException e)
        {
            LogInputRefused(log, e, descriptor.Name);
            refused = Error.InvalidInput(e, descriptor.Name);
        }
        catch (Exception)
        {
            // Unredactable for another reason (a getter that throws): a bug, which the audit's own redaction fails on.
        }
        if (refused is null && key is not null && !descriptor.ReadOnly)
        {
            if (!IsKey(key))
            {
                refused = Error.Validation(LeveeErrors.InvalidIdempotencyKey,
                    $"{descriptor.Name}'s idempotency key isn't one: 1 to 255 characters, none of them a control character.");
            }
            // A key is its sender's: every anonymous caller is one subject, so one's key would answer another's request.
            else if (principal is AnonymousPrincipal)
            {
                refused = Error.Validation(LeveeErrors.InvalidIdempotencyKey,
                    $"{descriptor.Name} was sent with an idempotency key by no one: a key is held by a caller who signs in.");
            }
        }
        Result<object?> result;
        try
        {
            result = refused ?? (scoped.IsSuccess ? await RunCoreAsync(descriptor, operation, input, principal, scoped.Value, answer, key, enlist, ct) : scoped.Error);
        }
        catch (AttemptSuperseded)
        {
            // Rolled back with the transaction the lock failed; the process's run ends, and its rescuer's goes on.
            await RecordOutcomeAsync(new OperationOutcome(descriptor, input ?? SafeRedact(operation), scoped.IsSuccess ? scoped.Value : null, principal,
                Error.Unavailable(LeveeErrors.Cancelled, $"{descriptor.Name} was sent by a process whose attempt was superseded.")), CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller stopped waiting: it still ended, rolled back, and the outcome is told so before the cancel goes on.
            await RecordOutcomeAsync(new OperationOutcome(descriptor, input ?? SafeRedact(operation), scoped.IsSuccess ? scoped.Value : null, principal,
                Error.Unavailable(LeveeErrors.Cancelled, $"{descriptor.Name} was cancelled by its caller.")), CancellationToken.None);
            throw;
        }
        catch (Exception e)
        {
            // Rolled back already: the unit of work was disposed on the way out.
            result = Unexpected(e, descriptor);
        }
        // After the commit, the caller's cancel can't undo the operation, so it doesn't stop it being told either.
        await RecordOutcomeAsync(new OperationOutcome(descriptor, input ?? SafeRedact(operation), scoped.IsSuccess ? scoped.Value : null, principal,
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
    internal async Task<Error?> AuthorizeAsync(IOperation operation, Principal principal, CancellationToken ct)
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

    /// <summary>As <see cref="AuthorizeAsync(IOperation, Principal, CancellationToken)"/>, with the scope it authorized: what a job stores to run in.</summary>
    internal async Task<Result<WorkScope>> AuthorizeScopeAsync(IOperation operation, Principal principal, CancellationToken ct)
    {
        if (catalog.Find(operation.GetType()) is not { } descriptor)
        {
            return Error.NotFound(LeveeErrors.UnknownOperation, $"{operation.GetType().Name} isn't in the catalog.");
        }
        // A confined process's enqueue is held to its scope as its own steps are.
        var scope = Confine(ScopeOf(descriptor, operation, principal), descriptor, principal);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }
        return await AuthorizeAsync(descriptor, operation, principal, scope.Value, ct) is { } denied ? denied : scope.Value;
    }

    /// <summary>As <see cref="AuthorizeAsync(IOperation, Principal, CancellationToken)"/>, by name and JSON, for a client.</summary>
    internal async Task<Error?> AuthorizeFromClientAsync(string name, JsonElement input, Principal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var found = Find(name, fromClient: true);
        if (!found.IsSuccess)
        {
            return found.Error;
        }
        var operation = OperationCatalog.Deserialize(found.Value, input, LeveeJson.Options, e => LogInputRefused(log, e, found.Value.Name));
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
    /// <summary>A scope key the app computes (a getter that parses an id) may throw: a bug, answered as one.</summary>
    // pinned: the caller's scope as it was when the work was asked for (a job's enqueue, a stream's opening). A caller-scoped
    // operation whose caller is now scoped elsewhere is refused, not moved there.
    private Result<WorkScope> ScopeOf(OperationDescriptor descriptor, IOperation operation, Principal principal, string? pinned = null)
    {
        try
        {
            // Read once: every decision in the call is about the same scope, however the principal computes it.
            var caller = (principal as IScopedPrincipal)?.ScopeKey;
            if (pinned is not null && descriptor.CallerScoped && caller != pinned)
            {
                return Error.Forbidden(LeveeErrors.ScopeViolation, $"{descriptor.Name}'s caller is now scoped elsewhere than when it was asked for.");
            }
            var scoped = Scoping(descriptor, operation, caller);
            return principal is AnonymousPrincipal && (scoped is { IsSuccess: false, Error.Code: LeveeErrors.CallerUnscoped })
                ? Error.Unauthenticated($"{descriptor.Name} runs in its caller's own scope: sign in first.")
                // Every tenant's rows are never what anyone may do, whatever the policy grants: that's Unscoped, which sees none.
                : principal is AnonymousPrincipal && scoped is { IsSuccess: true, Value.Platform: true }
                    ? Error.Unauthenticated($"{descriptor.Name} runs across every scope: sign in first.")
                    : scoped;
        }
        catch (Exception e)
        {
            return Unexpected(e, descriptor);
        }
    }

    private static Result<WorkScope> Scoping(OperationDescriptor descriptor, IOperation operation, string? caller) =>
        (descriptor.Platform || descriptor.Unscoped, descriptor.CallerScoped, operation) switch
        {
            (true, false, not IScopedOperation) when !(descriptor.Platform && descriptor.Unscoped) =>
                new WorkScope(null, descriptor.ReadOnly) { Unscoped = descriptor.Unscoped },
            (false, true, not IScopedOperation) => caller is { } key && !string.IsNullOrWhiteSpace(key)
                ? new WorkScope(key, descriptor.ReadOnly)
                : Error.Forbidden(LeveeErrors.CallerUnscoped, $"{descriptor.Name} runs in its caller's scope, and this caller has none."),
            (false, false, IScopedOperation { ScopeKey: { } key }) when !string.IsNullOrWhiteSpace(key) => new WorkScope(key, descriptor.ReadOnly),
            (false, false, IScopedOperation) => Error.Validation(LeveeErrors.ScopeMissing, $"{descriptor.Name} needs its scope."),
            _ => Error.Forbidden(LeveeErrors.ScopeUndeclared, $"{descriptor.Name} must declare one scope: its input's, its caller's, none, or the platform's."),
        };

    private static bool IsKey(string key) => key.Length is > 0 and <= 255 && !key.Any(char.IsControl);

    private async Task<Result<object?>> RunCoreAsync(OperationDescriptor descriptor, IOperation operation, string? input, Principal principal, WorkScope scope,
        Func<object?, object?>? answer, string? key, Func<IServiceProvider, CancellationToken, Task>? enlist, CancellationToken ct)
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
            // First, before anything else reads or writes: a read commits nothing a stale run could leave, and Postgres takes
            // no row lock in a read-only transaction.
            if (enlist is not null && !scope.ReadOnly)
            {
                await enlist(services.ServiceProvider, ct);
            }
            if (operation is IVersionedOperation versioned)
            {
                unit.Expect(versioned.ExpectedVersion);
            }
            var context = new OperationContext(principal, descriptor, scope, Now());
            services.ServiceProvider.GetRequiredService<CurrentOperation>().Context = context;
            foreach (var admission in services.ServiceProvider.GetServices<IAdmission>())
            {
                if (await admission.AdmitAsync(operation, context, ct) is { } refused)
                {
                    return refused;
                }
            }
            // After the admissions, so a refused caller learns nothing of what a key did; before the handler, so a repeat never runs it.
            var claimed = key is not null && !scope.ReadOnly
                ? new IdempotencyKey(principal.Subject, descriptor.Name, key, Hash(operation, descriptor), options.IdempotencyKeyLifetime)
                : null;
            if (claimed is not null)
            {
                var claim = await unit.ClaimAsync(claimed, ct);
                switch (claim.State)
                {
                    case IdempotencyState.Mismatch:
                        return Error.Conflict(LeveeErrors.IdempotencyMismatch, $"{descriptor.Name} was sent with this idempotency key and other input: a key names one request.");
                    case IdempotencyState.Replayed:
                        return Error.Conflict(LeveeErrors.IdempotencyReplayed,
                            $"{descriptor.Name} was done already with this idempotency key, and its result isn't kept: it carries a secret, shown once.");
                    case IdempotencyState.Kept:
                        // Done already: its first result, and nothing written again, nothing audited again.
                        var first = JsonSerializer.Deserialize(claim.Result!, descriptor.ResultType, LeveeJson.Options);
                        return Result<object?>.Success(answer is null ? first : answer(first));
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
            if (claimed is not null)
            {
                // Written once and read back: the first answer and every replay are the same wire, however the handler's
                // result enumerates. A secret is shown once: a result that carries one is never kept to show again.
                var wire = JsonSerializer.Serialize(result.Value, descriptor.ResultType, LeveeJson.Options);
                result = Result<object?>.Success(JsonSerializer.Deserialize(wire, descriptor.ResultType, LeveeJson.Options));
                unit.Keep(claimed, result.Value is null || LeveeJson.FindSecretIn(result.Value, descriptor.ResultType, LeveeJson.Options) is null ? wire : null);
            }
            if (answer is not null)
            {
                result = Result<object?>.Success(answer(result.Value));
            }
            if (descriptor.Audited)
            {
                await unit.AuditAsync(new AuditEntry(context.Now, scope.Key, principal.Subject, descriptor.Name, input ?? Redact(operation)), ct);
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
    /// The operation's own result stands, committed or not: a recorder failing (to be built, to record, to be disposed) is its
    /// bug, not the caller's, and the caller waits on them all for no longer than <see cref="LeveeOptions.OutcomeTimeout"/>.
    /// </summary>
    private async Task RecordOutcomeAsync(OperationOutcome outcome, CancellationToken ct)
    {
        // Not disposed when the recorders outlast it: they may still hold its token.
        var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(options.OutcomeTimeout);
        // On the pool, the scope and every recorder in it: one that blocks before its first await is bounded too.
        var recording = Task.Run(() => RecordAllAsync(outcome, bounded.Token), CancellationToken.None);
        try
        {
            await recording.WaitAsync(options.OutcomeTimeout, CancellationToken.None);
            bounded.Dispose();
        }
        catch (TimeoutException e)
        {
            LogOutcomeFailed(log, e, outcome.Operation.Name, $"the recorders, past {options.OutcomeTimeout}");
        }
    }

    private async Task RecordAllAsync(OperationOutcome outcome, CancellationToken ct)
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
                    await each.RecordAsync(outcome, ct);
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

    /// <summary>
    /// The input as the wire carries it, its objects' members in one order (a dictionary's entries in any order are the same
    /// input), its arrays' as they are: what a key's repeat is compared by.
    /// </summary>
    private static byte[] Hash(IOperation operation, OperationDescriptor descriptor)
    {
        // From the element, not a JsonObject: members that differ only in case ("ABC", "abc") are two members on the wire.
        static void Write(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var member in element.EnumerateObject().OrderBy(m => m.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(member.Name);
                        Write(writer, member.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        Write(writer, item);
                    }
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, JsonSerializer.SerializeToElement(operation, descriptor.InputType, LeveeJson.Options));
        }
        return System.Security.Cryptography.SHA256.HashData(buffer.WrittenSpan);
    }

    /// <summary>The input as the audit keeps it: every [Secret] and [Personal] value replaced, at any depth.</summary>
    private static string Redact(IOperation operation) => LeveeJson.Redact(operation, operation.GetType(), LeveeJson.Options);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Operation}'s input was refused by its own type")]
    private static partial void LogInputRefused(ILogger logger, Exception exception, string operation);

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
public sealed class OperationFailedException : Exception
{
    public OperationFailedException(Error error, Exception inner)
        : base($"{error.Code}: {error.Message}", inner) =>
        Error = error;

    /// <summary>A failure told from elsewhere (a stream's last event, over the wire), with no exception of this process behind it.</summary>
    internal OperationFailedException(Error error)
        : base($"{error.Code}: {error.Message}") =>
        Error = error;

    public Error Error { get; }
}

public sealed class LeveeOptions
{
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
    /// How long the <see cref="IOperationOutcome"/> recorders, together, have to record how an operation ended: built,
    /// recording and disposed. The caller waits for them, after the commit too, so past it they're cancelled, logged, and left
    /// running, unawaited.
    /// </summary>
    public TimeSpan OutcomeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a write's idempotency key holds after it commits: a retry within it gets the first result back; one after
    /// it runs as a new request.
    /// </summary>
    public TimeSpan IdempotencyKeyLifetime { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>A stream a projection opened: its items, and who it was opened for, where, for its rechecks.</summary>
internal sealed record OpenedSubscription(IAsyncEnumerable<JsonElement> Items, OperationDescriptor Descriptor, IOperation Subscription, string Subject,
    WorkScope Scope);
