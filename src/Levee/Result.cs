using System.Diagnostics.CodeAnalysis;

namespace Levee;

/// <summary>The failures every projection maps the same way. A closed set: domain outcomes that aren't failures belong in the result type.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Unauthenticated,
    Forbidden,
    Unavailable,

    /// <summary>Not a refusal but a bug: the operation threw. Only the dispatcher makes one; the detail stays in its log, under <see cref="Error.Trace"/>.</summary>
    Internal,
}

/// <summary>
/// A failure, made only by its factories: <see cref="ErrorKind.Internal"/> is the dispatcher's alone, for a bug, so no code
/// of an app's can answer one.
/// </summary>
public sealed record Error
{
    private Error(ErrorKind kind, string code, string message)
    {
        Kind = kind;
        Code = code;
        Message = message;
    }

    /// <summary>How every projection maps it: a status, whether to retry.</summary>
    public ErrorKind Kind { get; }

    /// <summary>
    /// Lowercase and dotted, like an operation's name: what a client branches on. An operation declares the codes it fails
    /// with (<see cref="OperationAttribute.Errors"/>); Levee's own are <see cref="LeveeErrors"/>.
    /// </summary>
    public string Code { get; }

    /// <summary>For a person, never parsed.</summary>
    public string Message { get; init; }

    /// <summary>A validation error's detail: what's wrong with each field, by its path in the input (<c>$.lines[2].amount</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Fields { get; init; }

    /// <summary>When an <see cref="ErrorKind.Unavailable"/> call may be tried again, if the cause says (a vendor's rate limit).</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>The trace the failure was logged under: what a caller quotes to find it.</summary>
    public string? Trace { get; init; }

    /// <summary>The same call, unchanged, may succeed later: a client may retry it, and a job is retried.</summary>
    public bool Transient => Kind is ErrorKind.Unavailable or ErrorKind.Internal;

    /// <summary>A failure of <paramref name="kind"/>, chosen at run time (mapping a vendor's status, say). Never <see cref="ErrorKind.Internal"/>.</summary>
    public static Error Of(ErrorKind kind, string code, string message) => kind == ErrorKind.Internal
        ? throw new ArgumentException("Internal is the dispatcher's, for a bug: return the failure the caller should see.", nameof(kind))
        : new(kind, code, message);

    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static Error Validation(string code, string message, IReadOnlyDictionary<string, IReadOnlyList<string>> fields) =>
        new(ErrorKind.Validation, code, message) { Fields = fields };

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static Error Unauthenticated(string message) => new(ErrorKind.Unauthenticated, LeveeErrors.Unauthenticated, message);
    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
    public static Error Unavailable(string code, string message) => new(ErrorKind.Unavailable, code, message);

    public static Error Unavailable(string code, string message, TimeSpan retryAfter) =>
        new(ErrorKind.Unavailable, code, message) { RetryAfter = retryAfter };

    internal static Error Internal(string trace) =>
        new(ErrorKind.Internal, LeveeErrors.Internal, $"Something went wrong on our side. Quote {trace} to find it.") { Trace = trace };

    internal static Error Uninitialized { get; } =
        new(ErrorKind.Internal, LeveeErrors.Internal, "A Result was used without being given a value or an error.");

    /// <summary>Input that isn't the operation's wire shape, with the field it broke on.</summary>
    public static Error InvalidInput(System.Text.Json.JsonException exception, string? operation = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Validation(LeveeErrors.InvalidInput, operation is null ? exception.Message : $"{operation}: {exception.Message}",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [exception.Path ?? "$"] = [exception.Message] });
    }
}

/// <summary>The codes Levee fails with itself. Any operation may fail with these, besides those it declares.</summary>
public static class LeveeErrors
{
    /// <summary>The operation threw: a bug, logged under the error's trace.</summary>
    public const string Internal = "internal";

    public const string Unauthenticated = "unauthenticated";
    public const string UnknownOperation = "operation.unknown";
    public const string InvalidInput = "input.invalid";
    public const string Streams = "operation.streams";
    public const string AnswersOnce = "operation.answers_once";
    public const string ScopeMissing = "scope.missing";
    public const string ScopeUndeclared = "scope.undeclared";

    /// <summary>The caller cancelled before the operation ended: it was rolled back. What <see cref="IOperationOutcome"/> is told.</summary>
    public const string Cancelled = "operation.cancelled";

    /// <summary>A caller-scoped operation's caller carries no scope.</summary>
    public const string CallerUnscoped = "scope.caller";

    /// <summary>A generated handler that hasn't been written yet.</summary>
    public const string NotImplemented = "not_implemented";

    /// <summary>A write through a read-only unit of work: the contract or the handler is wrong.</summary>
    public const string UndeclaredWrite = "effects.undeclared";

    /// <summary>A write outside the unit of work's scope.</summary>
    public const string ScopeViolation = "scope.violation";

    /// <summary>The store refused a duplicate: what a repeated <see cref="Idempotency.Conflicts"/> write gets.</summary>
    public const string Conflict = "conflict";

    /// <summary>The store refused the change by one of its rules (a check, a foreign key, a trigger): repeating it can't help.</summary>
    public const string ConstraintViolation = "store.constraint";

    /// <summary>Concurrent transactions collided (a serialization failure, a deadlock): try again.</summary>
    public const string Contention = "store.contention";

    /// <summary>The store couldn't be reached, or timed out: try again.</summary>
    public const string StoreUnavailable = "store.unavailable";

    /// <summary>A stream's caller couldn't be confirmed as it was rechecked (an identity lookup that failed or timed out): open it again.</summary>
    public const string CallerUnconfirmed = "caller.unconfirmed";

    /// <summary>
    /// An update or delete found no row to change: another write changed it first (a concurrency token), or it isn't the
    /// operation's scope's (row-level security hides it). Nothing was written; repeating it as it is can't help.
    /// </summary>
    public const string Stale = "store.stale";

    /// <summary>
    /// The store refused a value as its column's type: invalid text for the type (a scope key that isn't the policy's uuid),
    /// out of range, too long, a malformed date. The rest of SQL's data exceptions are the query's own bug.
    /// </summary>
    public const string InvalidValue = "store.invalid_value";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Internal, Unauthenticated, UnknownOperation, InvalidInput, Streams, AnswersOnce, ScopeMissing, ScopeUndeclared, CallerUnscoped, Cancelled, NotImplemented,
        UndeclaredWrite, ScopeViolation, Conflict, ConstraintViolation, Contention, StoreUnavailable, Stale, InvalidValue, CallerUnconfirmed,
    };
}

public readonly record struct Unit
{
    public static readonly Unit Value;
}

[SuppressMessage("Design", "CA1000", Justification = "Factory members on the generic result are its construction API.")]
public readonly struct Result<T>
{

    private readonly T? _value;
    private readonly Error? _error;
    private readonly bool _ok;

    private Result(T value)
    {
        _value = value;
        _error = null;
        _ok = true;
    }

    private Result(Error error)
    {
        _value = default;
        _error = error;
        _ok = false;
    }

    // default(Result<T>) is neither a value nor a failure anyone returned: it reads as a failure, never as a success, and the
    // dispatcher answers it as the bug it is.
    public Error? Error => _ok ? null : _error ?? Error.Uninitialized;

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => _ok;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result failed: {Error.Code}: {Error.Message}");

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(Error error) => new(error);

    [SuppressMessage("Usage", "CA2225", Justification = "Success and Failure are the named alternates.")]
    public static implicit operator Result<T>(T value) => new(value);

    [SuppressMessage("Usage", "CA2225", Justification = "Success and Failure are the named alternates.")]
    public static implicit operator Result<T>(Error error) => new(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? map(Value) : Error;
    }

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return IsSuccess ? next(Value) : Error;
    }
}
