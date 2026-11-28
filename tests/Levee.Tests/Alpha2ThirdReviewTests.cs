using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2ThirdReviewTests
{
    [ValueObject(typeof(int)), JsonConverter(typeof(QuantityJson))]
    public readonly record struct Quantity(int Value);

    public sealed class QuantityJson : JsonConverter<Quantity>
    {
        public override Quantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt32());
        public override void Write(Utf8JsonWriter writer, Quantity value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
    }

    public sealed record Basket(Quantity? Count);

    [Fact]
    public void Nullable_numeric_value_objects_publish_the_null_the_wire_emits()
    {
        var wire = JsonSerializer.Serialize(new Basket(null), LeveeJson.Options);
        Assert.Equal("""{"count":null}""", wire);
        Assert.Null(JsonSerializer.Deserialize<Basket>(wire, LeveeJson.Options)!.Count);
        var schema = LeveeJson.Schema(typeof(Basket))["properties"]!["count"]!;
        Assert.True(schema["type"] is JsonArray kinds && kinds.Any(k => k?.GetValue<string>() == "null"), schema.ToJsonString());
    }

    public enum ParcelSize { Small, Large }

    [ValueObject(typeof(ParcelSize)), JsonConverter(typeof(SizeJson))]
    public readonly record struct Size(ParcelSize Value);

    public sealed class SizeJson : JsonConverter<Size>
    {
        public override Size Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<ParcelSize>(ref reader, options));
        public override void Write(Utf8JsonWriter writer, Size value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Value, options);
    }

    [ValueObject(typeof(ParcelSize)), JsonConverter(typeof(GradeJson))]
    public sealed record Grade(ParcelSize Value);

    public sealed class GradeJson : JsonConverter<Grade>
    {
        public override Grade Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<ParcelSize>(ref reader, options));
        public override void Write(Utf8JsonWriter writer, Grade value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Value, options);
    }

    public sealed record Graded(Grade? Grade);

    [Fact]
    public void A_value_object_that_may_be_null_admits_null_by_its_enum_too()
    {
        static bool Admits(JsonNode schema) => schema["enum"] is JsonArray names && names.Any(n => n is null);

        Assert.True(Admits(LeveeJson.Schema(typeof(Graded))["properties"]!["grade"]!), LeveeJson.Schema(typeof(Graded)).ToJsonString());
        Assert.True(Admits(LeveeJson.Schema(typeof(Grade), LeveeJson.Options, nullable: true)), LeveeJson.Schema(typeof(Grade), LeveeJson.Options, nullable: true).ToJsonString());
    }

    [Fact]
    public void A_value_object_round_trips_its_documented_enum_wire_value()
    {
        var size = new Size(ParcelSize.Small);
        var wire = JsonSerializer.Serialize(size, LeveeJson.Options);
        Assert.Equal("\"small\"", wire);
        Assert.Contains("\"small\"", LeveeJson.Schema(typeof(Size)).ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(size, JsonSerializer.Deserialize<Size>(wire, LeveeJson.Options));
    }
}

public sealed class ClosedWireTests
{
    public readonly record struct Label(string Text);

    public sealed class LabelJson : JsonConverter<Label>
    {
        public override Label Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(JsonDocument.ParseValue(ref reader).RootElement.ToString());
        public override void Write(Utf8JsonWriter writer, Label value, JsonSerializerOptions options) => writer.WriteStringValue(value.Text);
    }

    public sealed class LabelFormatAttribute() : JsonConverterAttribute(typeof(LabelJson));

    public sealed record OnMember([property: JsonConverter(typeof(LabelJson))] Label Label);

    public sealed record OnDerived([property: LabelFormat] Label Label);

    [JsonConverter(typeof(UndeclaredJson))]
    public readonly record struct Undeclared(string Text);

    public sealed class UndeclaredJson : JsonConverter<Undeclared>
    {
        public override Undeclared Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, Undeclared value, JsonSerializerOptions options) => writer.WriteStringValue(value.Text);
    }

    public sealed record OfUndeclared(Undeclared Value);

    [Fact]
    public void A_converter_is_a_value_objects_on_its_type_and_nowhere_else()
    {
        // Refused where the catalog is made: a converter on a member, or one on a type that doesn't say what it writes.
        foreach (var contract in new[] { typeof(OnMember), typeof(OnDerived), typeof(OfUndeclared) })
        {
            Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([("review.label", contract)]));
        }
    }
}
