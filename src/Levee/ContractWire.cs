using System.Text.Json;

namespace Levee;

/// <summary>
/// The contract on the wire, read and written the one way every side does it (the dispatcher, the projections, the client):
/// an operation's input, its result or a stream's item, and a failure. What one side refuses, the other refuses the same way.
/// Each read and write is synchronous, so no caller's cancellation reaches one: whatever a converter throws is refused here.
/// </summary>
internal static class ContractWire
{
    /// <summary>
    /// The input as its operation's type reads it, refused as <see cref="LeveeErrors.InvalidInput"/> when it won't: a field it
    /// broke on, or, for an exception of the type's own that isn't a validation's, only that it was refused (its message may
    /// hold anything, so <paramref name="refused"/> is told it, for the log).
    /// </summary>
    public static Result<IOperation> ReadInput(OperationDescriptor descriptor, JsonElement input, Action<Exception>? refused = null)
    {
        try
        {
            var operation = input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? JsonSerializer.Deserialize("{}", descriptor.InputType, LeveeJson.Options)
                : input.Deserialize(descriptor.InputType, LeveeJson.Options);
            return operation is IOperation op
                ? Result<IOperation>.Success(op)
                : Error.Validation(LeveeErrors.InvalidInput, $"{descriptor.Name}: no input.");
        }
        catch (Exception e)
        {
            return Refused(descriptor, e, refused);
        }
    }

    /// <summary>The input as the wire carries it, refused here exactly as the platform would refuse it on reading it back.</summary>
    public static Result<JsonElement> WriteInput(OperationDescriptor descriptor, IOperation operation)
    {
        var snapshot = Snapshot(descriptor, operation);
        return snapshot.IsSuccess ? snapshot.Value.Wire : snapshot.Error;
    }

    /// <summary>
    /// An input sent typed, taken once: written to the wire, and read back as the operation everything after reads (its
    /// audit, its idempotency key, its handler), so what a value yields once (a sequence) or writes anew each time (a
    /// converter) is the same to all of them, and the same as it would be sent as JSON.
    /// </summary>
    public static Result<(JsonElement Wire, IOperation Read)> Snapshot(OperationDescriptor descriptor, IOperation operation)
    {
        JsonElement written;
        try
        {
            written = JsonSerializer.SerializeToElement(operation, descriptor.InputType, LeveeJson.Options);
        }
        catch (Exception e)
        {
            return Refused(descriptor, e, refused: null);
        }
        var read = ReadInput(descriptor, written);
        return read.IsSuccess ? (written, read.Value) : read.Error;
    }

    /// <summary>A result, or a stream's item, as the wire carries it.</summary>
    public static JsonElement WriteResult(OperationDescriptor descriptor, object? value)
    {
        Check(descriptor, value);
        return JsonSerializer.SerializeToElement(value, descriptor.ResultType, LeveeJson.Options);
    }

    /// <summary>One its contract says isn't null and is, is a bug of the handler that answered it.</summary>
    public static void Check(OperationDescriptor descriptor, object? value)
    {
        if (value is null && !descriptor.ResultNullable)
        {
            throw new InvalidOperationException($"{descriptor.Name} answered null, which its contract's {descriptor.ResultType.Name} isn't.");
        }
    }

    /// <summary>
    /// A result, or a stream's item, read as its contract's type: anything it won't read (what isn't its JSON, a value its
    /// own type refuses, null where it promises a value) is <paramref name="unread"/>'s answer, never an exception.
    /// </summary>
    public static Result<object?> ReadResult(OperationDescriptor descriptor, JsonElement wire, Func<string, Error> unread)
    {
        ArgumentNullException.ThrowIfNull(unread);
        object? value;
        try
        {
            value = wire.Deserialize(descriptor.ResultType, LeveeJson.Options);
        }
        catch (Exception e)
        {
            return unread($"{descriptor.Name}'s answer isn't its contract's {descriptor.ResultType.Name}: {e.Message}");
        }
        return value is null && !descriptor.ResultNullable
            ? unread($"{descriptor.Name} was answered with null, which its contract's {descriptor.ResultType.Name} isn't.")
            : Result<object?>.Success(value);
    }

    /// <summary>
    /// The failure a problem says, as long as it agrees with itself and with the status it came with: a status, kind and
    /// transience that disagree aren't the platform's own problem, and are <paramref name="unread"/>'s answer.
    /// </summary>
    public static Error ReadProblem(ProblemView problem, int status, TimeSpan? retryAfter, Func<string, Error> unread)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(unread);
        // A kind the platform names (enum names read as flags would combine two into one it doesn't).
        var agrees = Enum.IsDefined(problem.Kind) && problem.Status == status && problem.Status == ProblemView.StatusOf(problem.Kind)
            && problem.Transient == (problem.Kind is ErrorKind.Unavailable or ErrorKind.Internal);
        return agrees
            ? Error.FromProblem(problem, retryAfter)
            : unread($"A problem said {problem.Kind} ({problem.Status}, {(problem.Transient ? "transient" : "not transient")}) but came as {status}.");
    }

    private static Error Refused(OperationDescriptor descriptor, Exception e, Action<Exception>? refused) => e switch
    {
        JsonException json => Error.InvalidInput(json, descriptor.Name),
        // A record that validates in its constructor, or a value object's converter, refusing the value.
        ArgumentException or FormatException or OverflowException => Error.InvalidInput(new JsonException(e.Message, "$", null, null, e), descriptor.Name),
        _ => RefusedWithoutDetail(descriptor, e, refused),
    };

    private static Error RefusedWithoutDetail(OperationDescriptor descriptor, Exception e, Action<Exception>? refused)
    {
        refused?.Invoke(e);
        return Error.Validation(LeveeErrors.InvalidInput, $"{descriptor.Name}: its input was refused.");
    }
}
