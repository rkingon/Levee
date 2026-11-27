using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2FourthReviewTests
{
    [ValueObject(typeof(int))]
    public readonly record struct Weight(int Grams);
    public sealed record Package(Weight? Weight);

    [Fact]
    public void Annotating_a_nullable_struct_without_a_converter_keeps_schema_and_value_together()
    {
        // Refused where the catalog is made: a [ValueObject] the serializer would write member by member promises a scalar it never sends.
        Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([("review.package", typeof(Package))]));
    }

    [ValueObject(typeof(int)), JsonConverter(typeof(CountConverter))]
    public readonly record struct Count(int Value);
    public sealed class CountConverter : JsonConverter<Count>
    {
        public override Count Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<int>(ref reader, options));
        public override void Write(Utf8JsonWriter writer, Count value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Value, options);
    }

    [Fact]
    public void An_accepted_number_handling_configuration_round_trips_a_numeric_value_object()
    {
        // Rejected before use: number handling is the wire's, strict both ways.
        var json = JsonSerializer.Serialize(new Count(42), LeveeJson.Options);
        Assert.Equal("42", json);
        Assert.Equal(new Count(42), JsonSerializer.Deserialize<Count>(json, LeveeJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Count>("\"42\"", LeveeJson.Options));
    }

    [ValueObject(typeof(string)), JsonConverter(typeof(ProductCodeConverter))]
    public readonly record struct ProductCode(string Text);
    public sealed class ProductCodeConverter : JsonConverter<ProductCode>
    {
        public override ProductCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, ProductCode value, JsonSerializerOptions options) => writer.WriteStringValue(value.Text);
        public override ProductCode ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString()!);
        public override void WriteAsPropertyName(Utf8JsonWriter writer, ProductCode value, JsonSerializerOptions options) => writer.WritePropertyName(value.Text);
    }

    [Fact]
    public void A_string_value_objects_dictionary_key_converter_is_preserved()
    {
        var quantities = new Dictionary<ProductCode, int> { [new("BOX")] = 2 };
        var expected = JsonSerializer.Serialize(quantities);
        Assert.Equal("""{"BOX":2}""", expected);
        Assert.Equal(expected, JsonSerializer.Serialize(quantities, LeveeJson.Options));
        Assert.Equal(2, JsonSerializer.Deserialize<Dictionary<ProductCode, int>>(expected, LeveeJson.Options)![new("BOX")]);
    }
}
