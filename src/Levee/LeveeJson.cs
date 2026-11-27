using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
                if (Marked(context.PropertyInfo?.AttributeProvider, context.PropertyInfo?.DeclaringType, typeof(SecretAttribute)) && node is JsonObject obj)
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
    /// value replaced as the serializer writes it, so wherever the serializer goes (nested records and collections, nullable
    /// values, interfaces, derived and fallback types) and under the name it gives each member.
    /// </summary>
    public static string Redact(object value, Type type, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = options ?? Options;
        return JsonSerializer.Serialize(value, type, RedactingOf(json));
    }

    /// <summary>
    /// The first <see cref="SecretAttribute"/> member <paramref name="value"/> carries, by its type and by the types its
    /// members actually have (an implementation behind an interface), or null: what decides whether a job's input is sealed.
    /// </summary>
    public static string? FindSecretIn(object value, Type type, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = options ?? Options;
        if (FindSecret(type, json) is { } declared)
        {
            return declared;
        }
        FoundSecret = null;
        JsonSerializer.SerializeToUtf8Bytes(value, type, RedactingOf(json));
        var found = FoundSecret;
        FoundSecret = null;
        return found;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Redacting = [];

    /// <summary>The same options, each marked member written as its marker by its own contract.</summary>
    private static JsonSerializerOptions RedactingOf(JsonSerializerOptions json) => Redacting.GetValue(json, o =>
    {
        var redacting = new JsonSerializerOptions(o)
        {
            TypeInfoResolver = (o.TypeInfoResolver ?? new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()).WithAddedModifier(MarkMembers),
        };
        redacting.MakeReadOnly();
        return redacting;
    });

    // The member about to be written, when its mark depends on the value's own type: set by its getter, taken by its converter,
    // which the serializer calls right after, on the same thread.
    [ThreadStatic]
    private static string? _pending;

    [ThreadStatic]
    private static string? FoundSecret;

    private static void MarkMembers(System.Text.Json.Serialization.Metadata.JsonTypeInfo info)
    {
        if (info.Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object)
        {
            return;
        }
        foreach (var property in info.Properties)
        {
            if (Mark(property.AttributeProvider, info.Type) is { } mark)
            {
                property.CustomConverter = (JsonConverter)Activator.CreateInstance(typeof(MarkerConverter<>).MakeGenericType(property.PropertyType), mark, property.Name)!;
            }
            // Written by a contract another type may stand behind (an interface, a base the serializer falls back to): the value's
            // own type may mark the member.
            else if ((info.Type.IsInterface || (!info.Type.IsSealed && !info.Type.IsValueType)) && property.Get is { } get)
            {
                var provider = property.AttributeProvider;
                property.Get = owner =>
                {
                    _pending = owner is null ? null : Mark(provider, owner.GetType());
                    return get(owner!);
                };
                property.CustomConverter = (JsonConverter)Activator.CreateInstance(typeof(OwnTypeConverter<>).MakeGenericType(property.PropertyType),
                    property.CustomConverter, property.Name)!;
            }
        }
    }

    private static void Wrote(string mark, string member)
    {
        if (mark == "[secret]")
        {
            FoundSecret ??= member;
        }
    }

    private sealed class MarkerConverter<T>(string mark, string member) : JsonConverter<T>
    {
        public override bool HandleNull => true;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("A redacted value is only written.");

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            Wrote(mark, member);
            writer.WriteStringValue(mark);
        }
    }

    private sealed class OwnTypeConverter<T>(JsonConverter? inner, string member) : JsonConverter<T>
    {
        public override bool HandleNull => true;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("A redacted value is only written.");

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var mark = _pending;
            _pending = null;
            if (mark is not null)
            {
                Wrote(mark, member);
                writer.WriteStringValue(mark);
            }
            else if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                ((JsonConverter<T>)(inner ?? options.GetConverter(typeof(T)))).Write(writer, value, options);
            }
        }
    }

    /// <summary>The first <see cref="SecretAttribute"/> property anywhere in <paramref name="type"/>'s wire shape, or null.</summary>
    public static string? FindSecret(Type type, JsonSerializerOptions? options = null)
    {
        var json = options ?? Options;
        return Find(json.GetTypeInfo(type), json, []);
    }

    private static string? Mark(System.Reflection.ICustomAttributeProvider? property, Type owner) =>
        Marked(property, owner, typeof(SecretAttribute)) ? "[secret]"
        : Marked(property, owner, typeof(PersonalAttribute)) ? "[personal]"
        : null;

    /// <summary>
    /// Marked on the member itself, on any member it overrides, or on an interface member it implements for
    /// <paramref name="owner"/> (the type the value has, which may take the interface on after a base declared the property):
    /// a mark on an abstract base's or an interface's member holds for every implementation, whatever it says.
    /// </summary>
    private static bool Marked(System.Reflection.ICustomAttributeProvider? member, Type? owner, Type mark)
    {
        if (member is not System.Reflection.PropertyInfo property)
        {
            return member?.IsDefined(mark, inherit: false) == true;
        }
        // Attribute.IsDefined walks the overridden properties; PropertyInfo.IsDefined ignores inherit.
        if (Attribute.IsDefined(property, mark, inherit: true))
        {
            return true;
        }
        if (owner is null || owner.IsInterface || property.GetMethod is not { } getter)
        {
            return false;
        }
        // Written by a base's contract: the value's own type may override the member and mark it there.
        if (owner != property.DeclaringType && !property.DeclaringType!.IsInterface
            && owner.GetProperty(property.Name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public) is { } own
            && own != property && Attribute.IsDefined(own, mark, inherit: true))
        {
            return true;
        }
        // Written through an interface: the member the value's own type implements it with may carry the mark.
        if (property.DeclaringType is { IsInterface: true } through && through.IsAssignableFrom(owner))
        {
            var map = owner.GetInterfaceMap(through);
            var at = Array.FindIndex(map.InterfaceMethods, m => m.MetadataToken == getter.MetadataToken && m.Module == getter.Module);
            if (at >= 0 && map.TargetMethods[at] is var target
                && owner.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    .FirstOrDefault(p => p.GetMethod is { } g && g.MetadataToken == target.MetadataToken && g.Module == target.Module) is { } implementation
                && Attribute.IsDefined(implementation, mark, inherit: true))
            {
                return true;
            }
        }
        foreach (var contract in owner.GetInterfaces())
        {
            var map = owner.GetInterfaceMap(contract);
            for (var i = 0; i < map.TargetMethods.Length; i++)
            {
                if (map.TargetMethods[i].MetadataToken == getter.MetadataToken && map.TargetMethods[i].Module == getter.Module
                    && contract.GetProperties().FirstOrDefault(p => p.GetMethod == map.InterfaceMethods[i]) is { } declared
                    && declared.IsDefined(mark, inherit: false))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Never written: a bare <c>[JsonIgnore]</c>, or one whose condition is <c>Always</c>.</summary>
    private static bool Ignored(System.Text.Json.Serialization.Metadata.JsonPropertyInfo property) =>
        property.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: false).OfType<JsonIgnoreAttribute>()
            .Any(a => a.Condition == JsonIgnoreCondition.Always) == true;

    private static string? Find(System.Text.Json.Serialization.Metadata.JsonTypeInfo info, JsonSerializerOptions json, HashSet<Type> seen)
    {
        if (!seen.Add(info.Type))
        {
            return null;
        }
        if (Nullable.GetUnderlyingType(info.Type) is { } underlying)
        {
            return Find(json.GetTypeInfo(underlying), json, seen);
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
        foreach (var property in (info.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object ? info.Properties : []).Where(p => !Ignored(p)))
        {
            if (Marked(property.AttributeProvider, info.Type, typeof(SecretAttribute)))
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
