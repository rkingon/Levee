using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2DiscriminatorReviewTests
{
    // A contract's base takes nothing; its members set on their own.
    [JsonDerivedType(typeof(Bundle), "bundle")]
    public record Price { public int Amount { get; init; } }
    public sealed record Bundle(int Count) : Price;

    [JsonPolymorphic(IgnoreUnrecognizedTypeDiscriminators = true)]
    [JsonDerivedType(typeof(QuotedBundle), "bundle")]
    public record Quote { public int Amount { get; init; } }
    public sealed record QuotedBundle(int Count) : Quote;

    [Theory]
    [InlineData("""{"$type":"bundle","amount":12,"count":2}""")]
    [InlineData("""{"amount":12,"count":2,"$type":"bundle"}""")]
    public void Every_member_order_deserializes_to_the_declared_subtype(string wire)
    {
        OperationCatalog.Prove([("review.price", typeof(Price))]);
        var read = JsonSerializer.Deserialize<Price>(wire, LeveeJson.Options);
        Assert.Equal(new Bundle(2) { Amount = 12 }, Assert.IsType<Bundle>(read));
    }

    [Fact]
    public void An_unknown_discriminator_cannot_be_accepted_and_discarded()
    {
        const string wire = """{"$type":"express","amount":12}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Price>(wire, LeveeJson.Options));
        try
        {
            OperationCatalog.Prove([("review.quote", typeof(Quote))]);
        }
        catch (InvalidOperationException error)
        {
            Assert.Contains("review.quote", error.Message, StringComparison.Ordinal);
            return;
        }
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Quote>(wire, LeveeJson.Options));
    }
}
