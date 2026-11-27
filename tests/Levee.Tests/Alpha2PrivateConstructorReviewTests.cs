using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2PrivateConstructorReviewTests
{
    public sealed record Price
    {
        public int Amount { get; }
        [JsonConstructor]
        private Price(int amount) => Amount = amount;
    }

    [Fact]
    public void The_production_wire_and_catalog_support_the_selected_private_constructor()
    {
        const string wire = """{"amount":12}""";
        OperationCatalog.Prove([("review.price", typeof(Price))]);
        var read = JsonSerializer.Deserialize<Price>(wire, LeveeJson.Options);
        Assert.Equal(12, read!.Amount);
        Assert.Equal(wire, JsonSerializer.Serialize(read, LeveeJson.Options));
    }
}
