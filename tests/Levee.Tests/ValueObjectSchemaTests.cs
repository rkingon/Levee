using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levee.Tests;

[JsonConverter(typeof(ZipJson))]
[ValueObject(typeof(string), Pattern = "^[0-9]{5}$")]
public readonly record struct Zip(string Value);

public sealed class ZipJson : JsonConverter<Zip>
{
    public override Zip Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, Zip value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

public sealed record FindHomes([property: Description("The area's five-digit zip.")] Zip Area, Zip? Near, string Text,
    [property: Description("Where they were born.")] Zip? Born = null, Plate? Plate = null);

[JsonConverter(typeof(PlateJson))]
[ValueObject(typeof(string))]
public sealed record Plate(string Value);

public sealed class PlateJson : JsonConverter<Plate>
{
    public override Plate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, Plate value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

public sealed class ValueObjectSchemaTests
{
    [Fact]
    public void A_value_object_is_shown_as_what_its_converter_writes_with_its_description()
    {
        var properties = LeveeJson.Schema(typeof(FindHomes))["properties"]!;

        Assert.Equal(("string", "^[0-9]{5}$", "The area's five-digit zip."), (properties["area"]!["type"]!.GetValue<string>(),
            properties["area"]!["pattern"]!.GetValue<string>(), properties["area"]!["description"]!.GetValue<string>()));
        Assert.Equal("""["string","null"]""", properties["near"]!["type"]!.ToJsonString());
        Assert.Equal("string", properties["text"]!["type"]!.GetValue<string>());
        Assert.Equal("""["string","null"]""", properties["born"]!["type"]!.ToJsonString());
        Assert.Equal("Where they were born.", properties["born"]!["description"]!.GetValue<string>());
        // A nullable reference the serializer writes as null: its schema says so.
        Assert.Equal("""["string","null"]""", properties["plate"]!["type"]!.ToJsonString());
    }
}

public sealed class OptionsConverterTests
{
    private sealed class Loud : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString()!.ToLowerInvariant();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToUpperInvariant());
    }

    [Fact]
    public void A_converter_in_the_serializers_options_that_nothing_describes_is_refused_as_the_app_composes()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new("Probe:Greeter", "en")]).Build();

        var refused = Assert.Throws<InvalidOperationException>(() =>
            Generated.LeveeComposition.AddLeveeOperations(services, configuration, o => o.Json = LeveeJson.Create(j => j.Converters.Add(new Loud()))));

        Assert.Contains("probe.echo's input", refused.Message, StringComparison.Ordinal);
        // A .NET type can't take an attribute: the fix is to leave its JSON alone, or wrap it.
        Assert.Contains("remove it so the type is written as System.Text.Json writes it", refused.Message, StringComparison.Ordinal);
    }
}

[JsonConverter(typeof(AnythingJson))]
[ValueObject(typeof(string))]
public sealed record Note(string Text);

/// <summary>Takes whatever JSON it's given, as a careless converter would.</summary>
public sealed class AnythingJson : JsonConverter<Note>
{
    public override Note Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return new(document.RootElement.GetRawText());
    }

    public override void Write(Utf8JsonWriter writer, Note value, JsonSerializerOptions options) => writer.WriteStringValue(value.Text);
}

public sealed record Noted(Note Body);

public sealed class ValueObjectWireTests
{
    [Fact]
    public void A_value_objects_converter_is_handed_only_what_its_schema_promises()
    {
        Assert.Equal("\"hi\"", JsonSerializer.Deserialize<Noted>("""{"body":"hi"}""", LeveeJson.Options)!.Body.Text);
        var refused = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Noted>("""{"body":{"anything":1}}""", LeveeJson.Options));
        Assert.Contains("Note is written as String, not StartObject", refused.Message, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Noted>("""{"body":42}""", LeveeJson.Options));
    }
}
