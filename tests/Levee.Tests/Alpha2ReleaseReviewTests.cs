using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2ReleaseReviewTests
{
    public sealed record LegacyInvoice([property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] int Number);

    [Fact]
    public void Accepted_member_number_handling_round_trips_its_own_output()
    {
        // Refused where the catalog is made: a JSON attribute on a member is outside the contract language.
        Assert.Contains("JSON attribute", Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([("review.invoice", typeof(LegacyInvoice))])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wire_round_trips_an_enum_value_object_as_its_schema_says()
    {
        var value = new Alpha2ThirdReviewTests.Size(Alpha2ThirdReviewTests.ParcelSize.Small);
        var emitted = JsonSerializer.SerializeToElement(value, LeveeJson.Options);
        Assert.Equal("string", LeveeJson.Schema(typeof(Alpha2ThirdReviewTests.Size))["type"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.String, emitted.ValueKind);
        Assert.Equal(value, emitted.Deserialize<Alpha2ThirdReviewTests.Size>(LeveeJson.Options));
    }
}
