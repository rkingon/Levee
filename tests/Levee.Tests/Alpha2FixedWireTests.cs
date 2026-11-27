using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Levee.Tests;

public sealed class Alpha2FixedWireTests
{
    public sealed record Stock([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Quantity);

    public sealed record Plain(int Quantity);

    [Fact]
    public void A_member_written_only_sometimes_is_refused_before_it_breaks_a_round_trip()
    {
        Assert.Contains("only sometimes", Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(new Stock(0), LeveeJson.Options)).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => LeveeJson.Schema(typeof(Stock)));
        Assert.Equal(new Plain(0), JsonSerializer.Deserialize<Plain>(JsonSerializer.Serialize(new Plain(0), LeveeJson.Options), LeveeJson.Options));
    }

    [Fact]
    public void The_wire_publishes_nothing_that_can_be_changed()
    {
        Assert.True(LeveeJson.Options.IsReadOnly);
        Assert.IsNotType<DefaultJsonTypeInfoResolver>(LeveeJson.Options.TypeInfoResolver);
        Assert.Throws<InvalidOperationException>(() => LeveeJson.Options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver()));
    }
}

[System.Text.Json.Serialization.JsonDerivedType(typeof(Wholesale), "wholesale")]
public abstract record PriceList;

public sealed record Wholesale(int Tier) : PriceList
{
    [System.Text.Json.Serialization.JsonIgnore] public string? Note { get; init; }
}

public sealed class CatalogWireTests
{
    [Fact]
    public void A_contract_the_wire_refuses_fails_where_the_catalog_is_made_naming_its_operation()
    {
        // The build refuses such a contract first (LEVEE104); this is what holds where the build couldn't see.
        var refused = Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([("prices.list", typeof(PriceList))]));

        Assert.Contains("prices.list's PriceList isn't one the contract wire carries", refused.Message, StringComparison.Ordinal);
        Assert.Contains("[JsonIgnore]", refused.Message, StringComparison.Ordinal);
    }
}
