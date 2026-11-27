using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2DerivedWireReviewTests
{
    [JsonDerivedType(typeof(RetailPrice), "retail")]
    public abstract record Price(int Amount);

    public sealed record RetailPrice(int Amount) : Price(Amount)
    {
        [JsonIgnore] public int LocalAmount => Amount;
    }

    public sealed record Plain([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int Amount);

    [Fact]
    public void The_catalog_and_reader_refuse_an_ignored_property_in_a_referenced_contract()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => LeveeJson.Schema(typeof(Price)));
        Assert.Contains("LEVEE104", refused.Message, StringComparison.Ordinal);
        Assert.Contains("LocalAmount", refused.Message, StringComparison.OrdinalIgnoreCase);
        var ignored = Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Deserialize<RetailPrice>("""{"amount":7,"localAmount":99}""", LeveeJson.Options));
        Assert.Contains("LEVEE104", ignored.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_wire_property_round_trips_and_an_extra_property_is_refused()
    {
        var value = new Plain(7);
        Assert.Equal(value, JsonSerializer.Deserialize<Plain>(JsonSerializer.Serialize(value, LeveeJson.Options), LeveeJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Plain>("""{"amount":7,"localAmount":99}""", LeveeJson.Options));
    }
}
