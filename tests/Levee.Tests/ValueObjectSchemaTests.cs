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

public sealed record FindHomes([property: Description("The area's five-digit zip.")] Zip Area, Zip? Near, string Text, Zip? Born = null);

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
    }
}
