using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

namespace Levee;

/// <summary>The wire format. Strict: unknown members and missing required parameters are refused.</summary>
public static class LeveeJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>The strict defaults, for an app that adds its own converters (typed ids) before making them read-only.</summary>
    public static JsonSerializerOptions Create(Action<JsonSerializerOptions>? configure = null)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            // An API's JSON, never embedded in HTML: '+' in a timestamp reads as '+'.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        configure?.Invoke(o);
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    /// <summary>The JSON Schema of a contract type as the wire carries it, <see cref="SecretAttribute"/> inputs write-only: what every projection shows.</summary>
    public static JsonNode Schema(Type type, JsonSerializerOptions? options = null)
    {
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (context.PropertyInfo?.AttributeProvider?.IsDefined(typeof(SecretAttribute), inherit: false) == true && node is JsonObject obj)
                {
                    obj["writeOnly"] = true;
                }
                return node;
            },
        };
        return (options ?? Options).GetJsonSchemaAsNode(type, exporter);
    }
}
