using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2DiscriminatorReviewTests
{
    [JsonDerivedType(typeof(Bundle), "bundle")]
    public record Price(int Amount);
    public sealed record Bundle(int Amount, int Count) : Price(Amount);

    [JsonPolymorphic(IgnoreUnrecognizedTypeDiscriminators = true)]
    [JsonDerivedType(typeof(QuotedBundle), "bundle")]
    public record Quote(int Amount);
    public sealed record QuotedBundle(int Amount, int Count) : Quote(Amount);

    [Theory]
    [InlineData("""{"$type":"bundle","amount":12,"count":2}""")]
    [InlineData("""{"amount":12,"count":2,"$type":"bundle"}""")]
    public void Every_member_order_deserializes_to_the_declared_subtype(string wire)
    {
        OperationCatalog.Prove([("review.price", typeof(Price))]);
        var read = JsonSerializer.Deserialize<Price>(wire, LeveeJson.Options);
        Assert.Equal(new Bundle(12, 2), Assert.IsType<Bundle>(read));
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
