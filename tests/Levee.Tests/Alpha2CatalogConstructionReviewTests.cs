using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2CatalogConstructionReviewTests
{
    public sealed record Price
    {
        public int Amount { get; }
        public string Currency { get; }
        public Price(int amount, string currency) => (Amount, Currency) = (amount, currency);
        public Price(int amount) : this(amount, "USD") { }
    }

    public sealed record Quote(Price Price);

    public sealed record FirstCollision(int Quantity, [property: JsonPropertyName("quantity")] int Other);
    public sealed record SecondCollision(int Amount, [property: JsonPropertyName("amount")] int Other);

    [Fact]
    public void Metadata_refusals_name_each_affected_operation()
    {
        var error = Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([
            ("review.first", typeof(FirstCollision)), ("review.second", typeof(SecondCollision))]));
        Assert.Contains("review.first's FirstCollision", error.Message, StringComparison.Ordinal);
        Assert.Contains("review.second's SecondCollision", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_validation_refuses_input_with_no_usable_json_constructor()
    {
        var encoded = JsonSerializer.Serialize(new Quote(new Price(12, "USD")), LeveeJson.Options);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<Quote>(encoded, LeveeJson.Options));
        var failure = Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([("review.quote", typeof(Quote))]));
        Assert.Contains("review.quote", failure.Message, StringComparison.Ordinal);
    }
}
