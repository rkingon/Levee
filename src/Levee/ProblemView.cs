namespace Levee;

/// <summary>
/// A failure on the wire, as <c>application/problem+json</c> (RFC 9457), for every projection and the clients that read them: <paramref name="Code"/> is what a client branches on,
/// <paramref name="Trace"/> what it quotes to find the failure, <paramref name="Transient"/> whether the same call may be retried.
/// </summary>
/// <param name="Title">The status's reason phrase.</param>
/// <param name="Status">The HTTP status.</param>
/// <param name="Detail">For a person, never parsed.</param>
/// <param name="Code">The error's code.</param>
/// <param name="Kind">The error's kind.</param>
/// <param name="Transient">Whether the same call may succeed later.</param>
/// <param name="Trace">The trace it was logged under.</param>
/// <param name="Errors">A validation failure's fields, by path in the input.</param>
public sealed record ProblemView(string Title, int Status, string Detail, string Code,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(OneKind))] ErrorKind Kind, bool Transient, string Trace,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors)
{
    /// <summary>The HTTP status every projection reports a kind of failure as.</summary>
    public static int StatusOf(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => 400,
        ErrorKind.Unauthenticated => 401,
        ErrorKind.Forbidden => 403,
        ErrorKind.NotFound => 404,
        ErrorKind.Conflict => 409,
        ErrorKind.Unavailable => 503,
        _ => 500,
    };

    /// <summary>The problem for <paramref name="error"/>, its trace the error's own or else <paramref name="trace"/>.</summary>
    public static ProblemView From(Error error, string trace)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = StatusOf(error.Kind);
        var title = status switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            503 => "Service Unavailable",
            _ => "Internal Server Error",
        };
        return new ProblemView(title, status, error.Message, error.Code, error.Kind, error.Transient, error.Trace ?? trace, error.Fields);
    }
}

/// <summary>
/// A problem's kind as the wire names it: one of the kinds, by its name. The serializer's own enum reader would also take
/// several names joined by commas (as their combination, which may be another kind) or a number.
/// </summary>
internal sealed class OneKind : System.Text.Json.Serialization.JsonConverter<ErrorKind>
{
    public override ErrorKind Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        var name = reader.TokenType == System.Text.Json.JsonTokenType.String ? reader.GetString() : null;
        foreach (var kind in Enum.GetValues<ErrorKind>())
        {
            if (string.Equals(Name(kind), name, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }
        throw new System.Text.Json.JsonException("A problem's kind is one of the kinds, by its name.");
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, ErrorKind value, System.Text.Json.JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Name(value));
    }

    private static string Name(ErrorKind kind) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());
}
