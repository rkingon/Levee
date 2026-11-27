using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2ReleaseReviewTests
{
    public sealed record LegacyInvoice([property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] int Number);

    [Fact]
    public void Accepted_member_number_handling_round_trips_its_own_output()
    {
        // Refused before use: a member can't loosen the wire's strict numbers either.
        Assert.Contains("[JsonNumberHandling]", Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(new LegacyInvoice(42), LeveeJson.Options)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_accepted_converter_list_preserves_the_declared_enum_wire()
    {
        var options = new LeveeOptions { Json = LeveeJson.Create(o => o.Converters.Clear()) };
        var json = options.Json;
        var value = new Alpha2ThirdReviewTests.Size(Alpha2ThirdReviewTests.ParcelSize.Small);
        var schema = LeveeJson.Schema(typeof(Alpha2ThirdReviewTests.Size), json);
        var emitted = JsonSerializer.SerializeToElement(value, json);
        var readFailure = Record.Exception(() => Assert.Equal(value, emitted.Deserialize<Alpha2ThirdReviewTests.Size>(json)));
        Assert.Equal("string", schema["type"]!.GetValue<string>());
        Assert.True(emitted.ValueKind == JsonValueKind.String && readFailure is null,
            $"Schema declares a string, emitted {emitted.GetRawText()}, read result: {readFailure?.Message ?? "success"}.");
    }
}
