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
    /// <summary>
    /// The contract wire, the only one: strict (unknown members refused, nullability and required parameters respected, every
    /// member written, numbers as numbers), enums by name, and a value's own JSON its value object's. Nothing configures it:
    /// every schema Levee shows is this wire's, so a setting changed would make each of them a promise the wire breaks.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            // A number is a number on the wire, as its schema says: Web's defaults would also read one from a string.
            NumberHandling = JsonNumberHandling.Strict,
            // A subtype's discriminator wherever the caller puts it among its members, as its schema allows.
            AllowOutOfOrderMetadataProperties = true,
            // An API's JSON, never embedded in HTML: '+' in a timestamp reads as '+'.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // A resolver of Levee's own, sealed, with nothing to add to: no modifier can be slipped in before first use.
            TypeInfoResolver = new ClosedResolver(),
        };
        // A value object's converter is handed only the JSON its [ValueObject] says it writes.
        o.Converters.Add(new ValueObjectWire());
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        o.MakeReadOnly();
        return o;
    }

    /// <summary>
    /// Every converter of the app's is a value object's, on its type: one on a member outranks the type's (and its check), and one
    /// on a type that doesn't say what it writes makes a schema of nothing. LEVEE104 refuses both in the build; this, whatever
    /// the build didn't see.
    /// </summary>
    private static void Closed(JsonTypeInfo info)
    {
        if (info.UnmappedMemberHandling == JsonUnmappedMemberHandling.Skip)
        {
            throw new InvalidOperationException(
                $"{info.Type.Name} skips members it doesn't know ([JsonUnmappedMemberHandling(Skip)]), so a misspelt field is dropped, not refused. "
                + "Fix: drop it; the wire refuses what a contract doesn't declare (LEVEE104).");
        }
        // Every member is written, as the strict reader requires it: one written only sometimes reads back as missing.
        // A contract is its wire: a member left off it is skipped when sent, not refused, and one on it only sometimes may not
        // read back. Every member is on the wire, always.
        if (info.Properties.FirstOrDefault(p => p.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
                is [JsonIgnoreAttribute { Condition: not JsonIgnoreCondition.Never }, ..]) is { } ignored)
        {
            throw new InvalidOperationException(
                $"{info.Type.Name}.{ignored.Name} is left off the wire ([JsonIgnore]), always or sometimes: a caller that sends it is silently ignored, "
                + "not refused, and one written only sometimes may not read back. Fix: a contract carries what's on its wire and nothing else: move "
                + "the member off the contract (to the domain type, or a method) (LEVEE104).");
        }
        if (info.Properties.FirstOrDefault(p => p.IsExtensionData) is { } extension)
        {
            throw new InvalidOperationException(
                $"{info.Type.Name}.{extension.Name} takes every member it doesn't declare ([JsonExtensionData]), which no schema shows. "
                + "Fix: declare each member it takes (LEVEE104).");
        }
        if (info.NumberHandling is { } typeNumbers && typeNumbers != JsonNumberHandling.Strict)
        {
            throw new InvalidOperationException(
                $"{info.Type.Name} says how its numbers are written ([JsonNumberHandling]), so a number its schema shows isn't one on the wire. "
                + "Fix: drop it; a value that travels as a string is a value object, [ValueObject(typeof(string))] with its converter (LEVEE104).");
        }
        foreach (var member in info.Properties)
        {
            if (member.NumberHandling is { } numbers && numbers != JsonNumberHandling.Strict)
            {
                throw new InvalidOperationException(
                    $"{info.Type.Name}.{member.Name} says how its numbers are written ([JsonNumberHandling]), so a number its schema shows isn't one on the "
                    + "wire. Fix: drop it; a value that travels as a string is a value object, [ValueObject(typeof(string))] with its converter (LEVEE104).");
            }
            if (member.CustomConverter is { } own && !BuiltIn(own))
            {
                throw new InvalidOperationException(
                    $"{info.Type.Name}.{member.Name} has a converter of its own, which outranks its type's and says nothing a schema can show. "
                    + $"Fix: put the converter on {member.PropertyType.Name} itself, with [ValueObject(typeof(T))] saying the JSON scalar it writes (LEVEE104).");
            }
        }
        var declared = (Nullable.GetUnderlyingType(info.Type) ?? info.Type).GetCustomAttributes(typeof(ValueObjectAttribute), inherit: false) is not [];
        if (declared && (Nullable.GetUnderlyingType(info.Type) ?? info.Type).GetCustomAttributes(typeof(JsonConverterAttribute), inherit: false) is [])
        {
            throw new InvalidOperationException(
                $"{info.Type.Name} is a [ValueObject] with no converter of its own, so the serializer would write its members, not the scalar its "
                + "schema shows. Fix: put the converter that writes it as that scalar on the type, [JsonConverter(typeof(...))] beside [ValueObject] (LEVEE104).");
        }
        if (!BuiltIn(info.Converter) && info.Converter is not IValueObjectWire && !declared)
        {
            throw new InvalidOperationException(
                $"{info.Type.Name} is written by a converter of its own, which no schema can read. Fix: say what it writes on the type, "
                + "[ValueObject(typeof(string))] (with Format or Pattern if it has one) (LEVEE104).");
        }
    }

    private interface IValueObjectWire;

    /// <summary>A value object: written by its own converter as the one scalar its <see cref="ValueObjectAttribute"/> names.</summary>
    internal static bool IsValueObject(Type type) => Options.GetTypeInfo(type).Converter is IValueObjectWire;

    private sealed class ClosedResolver : IJsonTypeInfoResolver
    {
        private readonly DefaultJsonTypeInfoResolver _inner = new();

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            _inner.GetTypeInfo(type, options) is { } info ? Checked(info) : null;

        private static JsonTypeInfo Checked(JsonTypeInfo info)
        {
            Closed(info);
            return info;
        }
    }


    /// <summary>
    /// What a value object's schema promises (a string, a number), held on the way in: JSON of another kind is refused before
    /// its converter sees it, so a converter that would take anything can't make the schema a lie.
    /// </summary>
    private sealed class ValueObjectWire : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.GetCustomAttributes(typeof(ValueObjectAttribute), inherit: false) is [ValueObjectAttribute]
            && typeToConvert.GetCustomAttributes(typeof(JsonConverterAttribute), inherit: false) is [JsonConverterAttribute];

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var declared = (ValueObjectAttribute)typeToConvert.GetCustomAttributes(typeof(ValueObjectAttribute), inherit: false)[0];
            var attribute = (JsonConverterAttribute)typeToConvert.GetCustomAttributes(typeof(JsonConverterAttribute), inherit: false)[0];
            // A library's attribute deriving JsonConverterAttribute makes its converter itself, naming no type.
            var inner = attribute.ConverterType is { } converterType
                ? (JsonConverter)Activator.CreateInstance(converterType)!
                : attribute.CreateConverter(typeToConvert)
                    ?? throw new InvalidOperationException($"{attribute.GetType().Name} on {typeToConvert.Name} makes no converter.");
            if (inner is JsonConverterFactory factory)
            {
                inner = factory.CreateConverter(typeToConvert, options)!;
            }
            var accepts = Describe(typeToConvert, declared.Wire, options).Tokens;
            return (JsonConverter)Activator.CreateInstance(typeof(Checked<>).MakeGenericType(typeToConvert), inner, declared, accepts)!;
        }

        private sealed class Checked<T>(JsonConverter<T> inner, ValueObjectAttribute declared, JsonTokenType[] accepts) : JsonConverter<T>, IValueObjectWire
        {
            public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                accepts.Contains(reader.TokenType)
                    ? inner.Read(ref reader, typeToConvert, options)
                    : throw new JsonException($"{typeof(T).Name} is written as {declared.Wire.Name}, not {reader.TokenType}.");

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => inner.Write(writer, value, options);

            // A dictionary keyed by the value object: its key is the converter's own, as System.Text.Json would ask it.
            public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                inner.ReadAsPropertyName(ref reader, typeToConvert, options);

            public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
                inner.WriteAsPropertyName(writer, value!, options);
        }
    }

    private static readonly Dictionary<Type, string> Formats = new()
    {
        [typeof(Guid)] = "uuid", [typeof(DateOnly)] = "date", [typeof(TimeOnly)] = "time", [typeof(TimeSpan)] = "duration",
        [typeof(DateTimeOffset)] = "date-time", [typeof(DateTime)] = "date-time", [typeof(Uri)] = "uri",
    };

    /// <summary>
    /// What a value object's wire is, once: the JSON tokens its converter is handed and the schema every client is shown, so
    /// the two can't disagree. One JSON scalar; an enum is written by name, as every contract enum is.
    /// </summary>
    private static (JsonTokenType[] Tokens, JsonObject Schema) Describe(Type value, Type wire, JsonSerializerOptions json)
    {
        var w = Nullable.GetUnderlyingType(wire) ?? wire;
        if (w.IsEnum)
        {
            var names = json.GetJsonSchemaAsNode(w, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true }) as JsonObject ?? [];
            names["type"] = "string";
            return ([JsonTokenType.String], names);
        }
        if (Formats.TryGetValue(w, out var format))
        {
            return ([JsonTokenType.String], new JsonObject { ["type"] = "string", ["format"] = format });
        }
        return Type.GetTypeCode(w) switch
        {
            TypeCode.String or TypeCode.Char => ([JsonTokenType.String], new JsonObject { ["type"] = "string" }),
            TypeCode.Boolean => ([JsonTokenType.True, JsonTokenType.False], new JsonObject { ["type"] = "boolean" }),
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64
                or TypeCode.UInt64 => ([JsonTokenType.Number], new JsonObject { ["type"] = "integer" }),
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => ([JsonTokenType.Number], new JsonObject { ["type"] = "number" }),
            _ => throw new InvalidOperationException(
                $"{value.Name} is a value object written as {wire.Name}, which isn't one JSON value. A value object is one JSON scalar: a string, "
                + "number, bool, enum, Guid or date (LEVEE104)."),
        };
    }

    /// <summary>The JSON Schema of a contract type as the wire carries it, <see cref="SecretAttribute"/> inputs write-only: what every projection shows.</summary>
    internal static JsonNode Schema(Type type, JsonSerializerOptions? options = null)

    {
        var json = options ?? Options;
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                // A converter of the app's writes the value its own way (a typed id as a string): the contract's shape isn't the
                // wire's. A value object says what it writes; anything else promises no shape rather than the wrong one.
                var underlying = Nullable.GetUnderlyingType(context.TypeInfo.Type);
                var declared = (underlying ?? context.TypeInfo.Type).GetCustomAttributes(typeof(ValueObjectAttribute), inherit: false) is [ValueObjectAttribute v] ? v : null;
                // A nullable value object's converter is the serializer's own wrapper, so the exporter knows no shape for it either.
                if (declared is not null && (underlying is not null || !BuiltIn(context.TypeInfo.Converter)
                    || context.PropertyInfo?.CustomConverter is { } wrapped && !BuiltIn(wrapped)))
                {
                    // Whether the member may be null is the member's: a converter-backed reference has no shape of its own to say so.
                    node = WireSchema(underlying ?? context.TypeInfo.Type, declared, json, underlying is not null || context.PropertyInfo is { IsGetNullable: true } || AllowsNull(node));
                }
                else if ((context.PropertyInfo?.CustomConverter is { } own && !BuiltIn(own)) || !BuiltIn(context.TypeInfo.Converter))
                {
                    node = new JsonObject();
                }
                // A member with a default whose type has no shape of the exporter's own is never transformed on its own (it's
                // written as just its default), so its value object is filled in here, from the object that holds it.
                if (node is JsonObject { } holder && holder["properties"] is JsonObject members)
                {
                    foreach (var member in context.TypeInfo.Properties)
                    {
                        if (members[member.Name] is JsonObject written && written["type"] is null
                            && (Nullable.GetUnderlyingType(member.PropertyType) ?? member.PropertyType).GetCustomAttributes(typeof(ValueObjectAttribute), inherit: false)
                                is [ValueObjectAttribute skipped]
                            && WireSchema(Nullable.GetUnderlyingType(member.PropertyType) ?? member.PropertyType, skipped, json, Nullable.GetUnderlyingType(member.PropertyType) is not null || member.IsGetNullable) is JsonObject filled)
                        {
                            if (member.AttributeProvider?.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: true)
                                is [System.ComponentModel.DescriptionAttribute { Description: { Length: > 0 } memberText }, ..])
                            {
                                filled["description"] = memberText;
                            }
                            foreach (var (key, value) in written.ToList())
                            {
                                filled[key] = value?.DeepClone();
                            }
                            members[member.Name] = filled;
                        }
                    }
                }
                if (node is JsonObject described
                    && (context.PropertyInfo?.AttributeProvider ?? (context.PropertyInfo is null ? context.TypeInfo.Type : null))?
                        .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: true) is [System.ComponentModel.DescriptionAttribute { Description: { Length: > 0 } text }, ..])
                {
                    described["description"] = text;
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

    private static bool AllowsNull(JsonNode node) =>
        node is JsonObject o && o["type"] is JsonArray types && types.Any(t => t?.GetValue<string>() == "null");

    /// <summary>The schema of what a value object's converter writes, with its format and pattern, and null if the member allows it.</summary>
    private static JsonObject WireSchema(Type value, ValueObjectAttribute declared, JsonSerializerOptions json, bool nullable)
    {
        var wire = Describe(value, declared.Wire, json).Schema;
        if (declared.Format is { } format)
        {
            wire["format"] = format;
        }
        if (declared.Pattern is { } pattern)
        {
            wire["pattern"] = pattern;
        }
        if (nullable)
        {
            wire["type"] = new JsonArray(wire["type"]!.GetValue<string>(), "null");
            if (wire["enum"] is JsonArray names)
            {
                names.Add(null);
            }
        }
        return wire;
    }

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
