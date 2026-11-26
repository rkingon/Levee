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

    /// <summary>
    /// <paramref name="value"/> as the audit keeps it: every <see cref="SecretAttribute"/> and <see cref="PersonalAttribute"/>
    /// value replaced, at any depth (in nested records and collections), under the name the wire gives it.
    /// </summary>
    public static string Redact(object value, Type type, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = options ?? Options;
        var node = JsonSerializer.SerializeToNode(value, type, json);
        Redact(node, json.GetTypeInfo(type), json);
        return node?.ToJsonString(json) ?? "null";
    }

    /// <summary>The first <see cref="SecretAttribute"/> property anywhere in <paramref name="type"/>'s wire shape, or null.</summary>
    public static string? FindSecret(Type type, JsonSerializerOptions? options = null)
    {
        var json = options ?? Options;
        return Find(json.GetTypeInfo(type), json, []);
    }

    private static string? Mark(System.Reflection.ICustomAttributeProvider? property) =>
        property?.IsDefined(typeof(SecretAttribute), inherit: false) == true ? "[secret]"
        : property?.IsDefined(typeof(PersonalAttribute), inherit: false) == true ? "[personal]"
        : null;

    private static void Redact(JsonNode? node, System.Text.Json.Serialization.Metadata.JsonTypeInfo info, JsonSerializerOptions json)
    {
        switch (node)
        {
            case JsonObject obj when info.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object:
                foreach (var property in info.Properties)
                {
                    if (!obj.TryGetPropertyValue(property.Name, out var child))
                    {
                        continue;
                    }
                    if (Mark(property.AttributeProvider) is { } mark)
                    {
                        obj[property.Name] = mark;
                    }
                    else if (child is not null)
                    {
                        Redact(child, json.GetTypeInfo(property.PropertyType), json);
                    }
                }
                break;
            case JsonArray items when info.ElementType is { } element:
                foreach (var item in items)
                {
                    Redact(item, json.GetTypeInfo(element), json);
                }
                break;
            case JsonObject map when info.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Dictionary && info.ElementType is { } value:
                foreach (var (_, item) in map)
                {
                    Redact(item, json.GetTypeInfo(value), json);
                }
                break;
        }
    }

    private static string? Find(System.Text.Json.Serialization.Metadata.JsonTypeInfo info, JsonSerializerOptions json, HashSet<Type> seen)
    {
        if (!seen.Add(info.Type))
        {
            return null;
        }
        if (info.ElementType is { } element)
        {
            return Find(json.GetTypeInfo(element), json, seen);
        }
        foreach (var property in info.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object ? info.Properties : [])
        {
            if (property.AttributeProvider?.IsDefined(typeof(SecretAttribute), inherit: false) == true)
            {
                return property.Name;
            }
            if (Find(json.GetTypeInfo(property.PropertyType), json, seen) is { } nested)
            {
                return $"{property.Name}.{nested}";
            }
        }
        return null;
    }
}
