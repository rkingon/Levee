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
