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
    /// value replaced, at any depth (in nested records and collections, by each value's own type, so a derived type's
    /// members are seen too), under the name the wire gives it.
    /// </summary>
    public static string Redact(object value, Type type, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = options ?? Options;
        var node = JsonSerializer.SerializeToNode(value, type, json);
        Redact(node, value, json);
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

    /// <summary>Walks the value and its JSON together, so each member is judged by the type it actually has.</summary>
    private static void Redact(JsonNode? node, object? value, JsonSerializerOptions json)
    {
        if (node is null || value is null)
        {
            return;
        }
        var info = json.GetTypeInfo(value.GetType());
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
                    else if (child is not null && property.Get is { } get)
                    {
                        Redact(child, get(value), json);
                    }
                }
                break;
            case JsonArray items when value is System.Collections.IEnumerable values && value is not string:
                var index = 0;
                foreach (var item in values)
                {
                    if (index < items.Count)
                    {
                        Redact(items[index], item, json);
                    }
                    index++;
                }
                break;
            // Entries are written in the order they enumerate, so each is matched to its JSON by position: a key's own spelling
            // (a date, an enum, a converter's) never has to be reproduced.
            case JsonObject map when info.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Dictionary && value is System.Collections.IEnumerable entries:
                var at = 0;
                foreach (var entry in entries)
                {
                    if (at < map.Count)
                    {
                        Redact(map[at], ValueOf(entry), json);
                    }
                    at++;
                }
                break;
        }
    }

    private static object? ValueOf(object? entry) => entry switch
    {
        System.Collections.DictionaryEntry e => e.Value,
        null => null,
        _ => entry.GetType().GetProperty("Value")?.GetValue(entry),
    };

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
        // A declared polymorphic base may arrive as any of its derived types: each is searched as well.
        foreach (var derived in info.PolymorphismOptions?.DerivedTypes ?? [])
        {
            if (Find(json.GetTypeInfo(derived.DerivedType), json, seen) is { } inDerived)
            {
                return inDerived;
            }
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
