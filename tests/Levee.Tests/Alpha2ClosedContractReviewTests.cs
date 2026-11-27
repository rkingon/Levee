using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace Levee.Tests;

public sealed class Alpha2ClosedContractReviewTests
{
    [JsonDerivedType(typeof(BundlePrice), "bundle")]
    public record Price(int Amount);
    public sealed record BundlePrice(int Amount, ReadOnlyCollection<int> Quantities) : Price(Amount);
    public sealed record Quote(Price Price);

    [Fact]
    public void A_concrete_base_must_check_the_contract_of_its_tagged_subtype()
    {
        var control = JsonSerializer.Deserialize<Quote>("""{"price":{"amount":12}}""", LeveeJson.Options);
        Assert.Equal(12, control!.Price.Amount);
        var input = new Quote(new BundlePrice(12, new ReadOnlyCollection<int>([7])));
        var wire = JsonSerializer.Serialize(input, LeveeJson.Options);
        Assert.Contains("bundle", wire, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<Quote>(wire, LeveeJson.Options));
        var refusal = Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([("review.quote", typeof(Quote))]));
        Assert.Contains("review.quote", refusal.Message, StringComparison.Ordinal);
    }

    public sealed class Unbound
    {
        public int Amount { get; set; }
        public Unbound(int value) => Amount = value;
    }

    [Fact]
    public void A_setter_cannot_bind_an_unmatched_constructor_parameter()
    {
        var wire = JsonSerializer.Serialize(new Unbound(12), LeveeJson.Options);
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Deserialize<Unbound>(wire, LeveeJson.Options));
        var refused = Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([("review.unbound", typeof(Unbound))]));
        Assert.Contains("review.unbound", refused.Message, StringComparison.Ordinal);
    }

    public sealed record IncludedPrice
    {
        [JsonInclude] public int Amount { get; private set; }
    }

    [Theory]
    [InlineData(typeof(int[,]), "[[1,2],[3,4]]", false)]
    [InlineData(typeof(int[][]), "[[1,2],[3,4]]", true)]
    [InlineData(typeof(IReadOnlySet<int>), "[3,7]", false)]
    [InlineData(typeof(ISet<int>), "[3,7]", true)]
    [InlineData(typeof(Dictionary<TimeSpan,int>), """{"00:15:00":2}""", true)]
    [InlineData(typeof(Dictionary<DateOnly,int>), """{"2026-11-27":2}""", true)]
    [InlineData(typeof(Dictionary<TimeOnly,int>), """{"08:00:00":2}""", true)]
    [InlineData(typeof(Dictionary<Version,int>), """{"1.2.3":2}""", true)]
    [InlineData(typeof(Dictionary<Unit,int>), """{"unit":2}""", false)]
    [InlineData(typeof(IncludedPrice), """{"amount":7}""", true)]
    public void A_published_type_can_read_a_nonempty_example(Type type, string json, bool mustSupport)
    {
        try
        {
            OperationCatalog.Prove([("review.container", type)]);
        }
        catch (InvalidOperationException error) when (!mustSupport)
        {
            Assert.Contains("review.container", error.Message, StringComparison.Ordinal);
            return;
        }
        var read = JsonSerializer.Deserialize(json, type, LeveeJson.Options);
        Assert.NotNull(read);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonSerializer.SerializeToNode(read, type, LeveeJson.Options)));
    }
}
