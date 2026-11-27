using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2SchemaSampleReviewTests
{
    public sealed record Order(Dictionary<string, ReadOnlyCollection<int>> Lines);

    [JsonDerivedType(typeof(RetailPrice), "single")]
    [JsonDerivedType(typeof(Bundle), "bundle")]
    public abstract record Price;
    public sealed record RetailPrice(int Amount) : Price;
    public sealed record Bundle(ReadOnlyCollection<int> Amounts) : Price;
    public sealed record Quote(Price Price);

    [Theory]
    [InlineData(typeof(Order), """{"lines":{}}""", """{"lines":{"sku":[7]}}""")]
    [InlineData(typeof(Quote), """{"price":{"$type":"single","amount":7}}""", """{"price":{"$type":"bundle","amounts":[7]}}""")]
    public void Published_alternatives_must_all_be_readable(Type type, string control, string alternate)
    {
        Assert.NotNull(JsonSerializer.Deserialize(control, type, LeveeJson.Options));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(alternate, type, LeveeJson.Options));
        var refusal = Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([("review.order", type)]));
        Assert.Contains("review.order", refusal.Message, StringComparison.Ordinal);
    }

    public sealed record Folder(string Name, Folder? Parent = null);
    public sealed record Limits(TimeSpan Timeout);
    public sealed record Revision(Version Version);

    [Theory]
    [InlineData(typeof(Folder), """{"name":"leaf","parent":{"name":"root","parent":null}}""")]
    [InlineData(typeof(Limits), """{"timeout":"00:15:00"}""")]
    [InlineData(typeof(Revision), """{"version":"1.2.3"}""")]
    public void An_ordinary_round_trippable_input_can_start(Type type, string wire)
    {
        var read = JsonSerializer.Deserialize(wire, type, LeveeJson.Options);
        Assert.Equal(wire, JsonSerializer.Serialize(read, type, LeveeJson.Options));
        OperationCatalog.Prove([("review.configure", type)]);
    }

    public sealed record MinimumPrice
    {
        public int Amount { get; }
        public MinimumPrice(int amount)
        {
            if (amount < 10)
            {
                throw new JsonException("Minimum price is 10.");
            }
            Amount = amount;
        }
    }

    [Fact]
    public void A_constructor_validating_its_own_price_does_not_block_the_catalog()
    {
        const string valid = """{"amount":12}""";
        var read = JsonSerializer.Deserialize<MinimumPrice>(valid, LeveeJson.Options);
        Assert.Equal(valid, JsonSerializer.Serialize(read, LeveeJson.Options));
        OperationCatalog.Prove([("review.minimum", typeof(MinimumPrice))]);
    }
}
