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
    internal static JsonNode Schema(Type type, JsonSerializerOptions? options = null)
    {
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                // A converter of the app's writes the value its own way (a typed id as a string): the contract's shape isn't the
                // wire's, so the schema promises no shape at all rather than the wrong one.
                if ((context.PropertyInfo?.CustomConverter is { } own && !BuiltIn(own)) || !BuiltIn(context.TypeInfo.Converter))
                {
                    node = new JsonObject();
                }
                if (Marked(context.PropertyInfo?.AttributeProvider, context.PropertyInfo?.DeclaringType, typeof(SecretAttribute)) && node is JsonObject obj)
                {
                    obj["writeOnly"] = true;
                }
                return node;
            },
        };
        return (options ?? Options).GetJsonSchemaAsNode(type, exporter);
    }

    private static bool BuiltIn(JsonConverter converter) => converter.GetType().Assembly == typeof(JsonSerializer).Assembly;

    /// <summary>
    /// <paramref name="value"/> as the audit keeps it: every <see cref="SecretAttribute"/> and <see cref="PersonalAttribute"/>
    /// value replaced as the serializer writes it, so wherever the serializer goes (nested records and collections, nullable
    /// values, interfaces, derived and fallback types) and under the name it gives each member.
    /// </summary>
    internal static string Redact(object value, Type type, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = options ?? Options;
        return JsonSerializer.Serialize(value, type, RedactingOf(json));
    }

    /// <summary>
    /// The first <see cref="SecretAttribute"/> member <paramref name="value"/> carries, by its type and by the types its
    /// members actually have (an implementation behind an interface), or null: what decides whether a job's input is sealed.
    /// </summary>
    internal static string? FindSecretIn(object value, Type type, JsonSerializerOptions? options = null)
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

    /// <summary>
    /// The same options, each marked member written as its marker by its own contract, and each value a converter writes
    /// whole (a value object's own converter) written as its marker when its type carries one.
    /// </summary>
    private static JsonSerializerOptions RedactingOf(JsonSerializerOptions json) => Redacting.GetValue(json, o =>
    {
        var redacting = new JsonSerializerOptions(o)
        {
            TypeInfoResolver = (o.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()).WithAddedModifier(info => MarkMembers(info, o)),
        };
        // Ahead of the app's converters and the types' own: an opaque value that carries a mark is written as its marker.
        redacting.Converters.Insert(0, new OpaqueMarkedFactory(o));
        redacting.MakeReadOnly();
        return redacting;
    });

    // The member about to be written, when its mark depends on the value's own type: set by its getter, taken by its converter,
    // which the serializer calls right after, on the same thread.
    [ThreadStatic]
    private static string? _pending;

    [ThreadStatic]
    private static string? FoundSecret;

    /// <summary>A value whose contract shows no members (a converter writes it whole) and whose type carries a mark.</summary>
    private static bool Opaque(Type type, JsonSerializerOptions json) =>
        CarriedMark(type) is not null && json.GetTypeInfo(type).Kind == JsonTypeInfoKind.None;

    private static void MarkMembers(JsonTypeInfo info, JsonSerializerOptions json)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }
        foreach (var property in info.Properties.Where(p => !p.IsExtensionData))
        {
            if (Mark(property.AttributeProvider, info.Type) is { } mark)
            {
                property.CustomConverter = Marker(property.PropertyType, mark, property.Name);
            }
            // The property's own converter writes the value whole: what its type carries can't be told apart, so all of it is.
            else if (property.CustomConverter is not null && CarriedMark(property.PropertyType) is { } carried)
            {
                property.CustomConverter = Marker(property.PropertyType, carried, property.Name);
            }
            // Written by a contract another type may stand behind (an interface, a base the serializer falls back to): the value's
            // own type may mark the member.
            // Only where the value's own type could mark it (behind an interface, or a member it overrides), and only where writing it
            // as the serializer would is in this converter's reach: a configured reference handler, or number handling over a
            // collection, is the serializer's alone, so there the member is left to it.
            else if (MayMarkAtRunTime(info, property) && property.Get is { } get && json.ReferenceHandler is null
                && !((property.NumberHandling ?? info.NumberHandling ?? json.NumberHandling) is { } handling && handling.HasFlag(JsonNumberHandling.WriteAsString)
                    && HoldsNumbers(property.PropertyType)))
            {
                var provider = property.AttributeProvider;
                property.Get = owner =>
                {
                    _pending = owner is null ? null : Mark(provider, owner.GetType());
                    return get(owner!);
                };
                property.CustomConverter = (JsonConverter)Activator.CreateInstance(typeof(OwnTypeConverter<>).MakeGenericType(property.PropertyType),
                    property.CustomConverter, property.NumberHandling ?? info.NumberHandling ?? json.NumberHandling, property.Name)!;
            }
        }
    }

    private static bool MayMarkAtRunTime(JsonTypeInfo info, JsonPropertyInfo property) =>
        info.Type.IsInterface
        || (property.AttributeProvider is System.Reflection.PropertyInfo { GetMethod: { IsVirtual: true, IsFinal: false } } && !info.Type.IsSealed);

    /// <summary>A collection of numbers: what number handling formats that this converter can't reproduce (a scalar it can).</summary>
    private static bool HoldsNumbers(Type type) =>
        type != typeof(string) && (type.IsArray ? type.GetElementType() : type.GetInterfaces().Prepend(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0]) is { } element
        && (OwnTypeConverterNumbers.Contains(Nullable.GetUnderlyingType(element) ?? element)
            || (element.IsGenericType && element.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
                && OwnTypeConverterNumbers.Contains(Nullable.GetUnderlyingType(element.GetGenericArguments()[1]) ?? element.GetGenericArguments()[1])));

    private static readonly HashSet<Type> OwnTypeConverterNumbers =
    [
        typeof(int), typeof(long), typeof(short), typeof(byte), typeof(sbyte), typeof(uint), typeof(ulong), typeof(ushort), typeof(float),
        typeof(double), typeof(decimal),
    ];

    private static JsonConverter Marker(Type type, string mark, string member) =>
        (JsonConverter)Activator.CreateInstance(typeof(MarkerConverter<>).MakeGenericType(type), mark, member)!;

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

    /// <summary>
    /// Writes the marker when the value's own type marks the member; otherwise what the serializer would have: the property's
    /// own converter (resolved from a factory), its number handling, the type's converter.
    /// </summary>
    private sealed class OwnTypeConverter<T>(JsonConverter? inner, JsonNumberHandling? numbers, string member) : JsonConverter<T>
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
            else if (value is null && Resolve(options) is not { HandleNull: true })
            {
                writer.WriteNullValue();
            }
            else if (numbers is { } handling && handling.HasFlag(JsonNumberHandling.WriteAsString) && value is IFormattable number && IsNumber(typeof(T)))
            {
                writer.WriteStringValue(number.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                Resolve(options).Write(writer, value!, options);
            }
        }

        private JsonConverter<T> Resolve(JsonSerializerOptions options) =>
            (JsonConverter<T>)(inner is JsonConverterFactory factory ? factory.CreateConverter(typeof(T), options)! : inner ?? options.GetConverter(typeof(T)));

        private static bool IsNumber(Type type) => (Nullable.GetUnderlyingType(type) ?? type) is var t
            && (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte) || t == typeof(uint)
                || t == typeof(ulong) || t == typeof(ushort) || t == typeof(float) || t == typeof(double) || t == typeof(decimal));
    }

    private sealed class OpaqueMarkedFactory(JsonSerializerOptions json) : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => Opaque(typeToConvert, json);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            Marker(typeToConvert, CarriedMark(typeToConvert)!, typeToConvert.Name);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string?> Carried = new();

    /// <summary>The strongest mark anywhere in <paramref name="type"/>'s own members, by its CLR shape, or null.</summary>
    private static string? CarriedMark(Type type) => Carried.GetOrAdd(type, t => CarriedMark(t, []));

    private static string? CarriedMark(Type type, HashSet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        // An array carries what its elements do; it has no members of its own to mark.
        if (type.IsArray)
        {
            return type.GetElementType() is { } element ? CarriedMark(element, seen) : null;
        }
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || !seen.Add(type))
        {
            return null;
        }
        // Every member, its mark and what its own type carries both counted: a [Personal] member holding a [Secret] is a secret.
        var members = type.GetProperties(Everything)
            .Where(p => p.GetIndexParameters().Length == 0 && (p.GetMethod?.IsPublic == true || p.IsDefined(typeof(JsonIncludeAttribute), inherit: true)))
            .SelectMany(p => new string?[] { Mark(p, type), CarriedMark(p.PropertyType, seen) })
            .Concat(type.GetFields(Everything).Where(f => f.IsDefined(typeof(JsonIncludeAttribute), inherit: false) || (f.IsPublic && !f.IsStatic)).Select(f => CarriedMark(f.FieldType, seen)))
            .Concat((type.IsGenericType ? type.GetGenericArguments() : []).Select(a => CarriedMark(a, seen)));
        string? strongest = null;
        foreach (var mark in members)
        {
            if (mark == "[secret]")
            {
                return mark;
            }
            strongest ??= mark;
        }
        return strongest;
    }

    /// <summary>The first <see cref="SecretAttribute"/> property anywhere in <paramref name="type"/>'s wire shape, or null.</summary>
    internal static string? FindSecret(Type type, JsonSerializerOptions? options = null)
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
        if (owner is null || owner.IsInterface || owner.IsArray || property.GetMethod is not { } getter)
        {
            return false;
        }
        // The getter the value's own type runs for this member: an interface's, mapped to its implementation; a base's, to its
        // override (by its virtual slot: a member of the same name that hides it is another member).
        var runs = getter;
        if (property.DeclaringType is { IsInterface: true } through && through.IsAssignableFrom(owner))
        {
            var map = owner.GetInterfaceMap(through);
            var at = Array.FindIndex(map.InterfaceMethods, m => Same(m, getter));
            if (at >= 0)
            {
                runs = map.TargetMethods[at];
            }
        }
        else if (owner != property.DeclaringType && getter.IsVirtual
            && owner.GetProperties(Everything).Where(p => p.Name == property.Name && p.GetIndexParameters().Length == 0
                    && p.GetMethod is { } g && Same(g.GetBaseDefinition(), getter.GetBaseDefinition()))
                .OrderByDescending(p => Depth(p.DeclaringType)).FirstOrDefault() is { GetMethod: { } own })
        {
            runs = own;
        }
        // Marked where it's implemented, or on any interface the same implementation serves (a sibling's mark holds too).
        if (owner.GetProperties(Everything).FirstOrDefault(p => p.GetMethod is { } g && Same(g, runs)) is { } implementation
            && Attribute.IsDefined(implementation, mark, inherit: true))
        {
            return true;
        }
        foreach (var contract in owner.GetInterfaces())
        {
            var map = owner.GetInterfaceMap(contract);
            for (var i = 0; i < map.TargetMethods.Length; i++)
            {
                if (Same(map.TargetMethods[i], runs)
                    && contract.GetProperties().FirstOrDefault(p => p.GetMethod is { } g && Same(g, map.InterfaceMethods[i])) is { } declared
                    && declared.IsDefined(mark, inherit: false))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private const System.Reflection.BindingFlags Everything =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

    private static bool Same(System.Reflection.MethodInfo a, System.Reflection.MethodInfo b) => a.MetadataToken == b.MetadataToken && a.Module == b.Module;

    private static int Depth(Type? type)
    {
        var depth = 0;
        for (var t = type; t is not null; t = t.BaseType)
        {
            depth++;
        }
        return depth;
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
        // Written whole by a converter: what its type carries is all there is to go by.
        if (info.Kind == JsonTypeInfoKind.None && CarriedMark(info.Type) == "[secret]")
        {
            return info.Type.Name;
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
