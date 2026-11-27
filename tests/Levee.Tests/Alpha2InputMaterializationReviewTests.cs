using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2InputMaterializationReviewTests
{
    public sealed record Cart(ReadOnlyCollection<int> Quantities);

    [JsonDerivedType(typeof(RetailPrice))]
    public abstract record Price(int Amount);
    public sealed record RetailPrice(int Amount) : Price(Amount);
    public sealed record Quote(Price Price);

    public sealed class Unbound
    {
        public int Amount { get; }
        public Unbound(int value) => Amount = value;
    }

    [Theory]
    [InlineData("collection", typeof(NotSupportedException))]
    [InlineData("polymorphism", typeof(NotSupportedException))]
    [InlineData("parameter", typeof(InvalidOperationException))]
    public void The_catalog_refuses_input_the_reader_cannot_restore(string kind, Type expected)
    {
        object value = kind switch
        {
            "collection" => new Cart(new ReadOnlyCollection<int>([7])),
            "polymorphism" => new Quote(new RetailPrice(12)),
            "parameter" => new Unbound(12),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var contract = value.GetType();
        var wire = JsonSerializer.Serialize(value, contract, LeveeJson.Options);
        Assert.IsType(expected, Record.Exception(() => JsonSerializer.Deserialize(wire, contract, LeveeJson.Options)));
        var error = Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([("review.price", contract)]));
        Assert.Contains("review.price", error.Message, StringComparison.Ordinal);
    }
}
