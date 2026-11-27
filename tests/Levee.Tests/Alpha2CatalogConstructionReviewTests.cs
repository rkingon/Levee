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

public sealed class CatalogReadProofTests
{
    public sealed record Counts(System.Collections.ObjectModel.ReadOnlyCollection<int> Values);
    public sealed record CountsList(List<int> Values);

    [JsonDerivedType(typeof(Square))]
    public abstract record Shape;
    public sealed record Square(int Side) : Shape;
    public sealed record Draw(Shape Shape);

    [JsonDerivedType(typeof(Circle), "circle")]
    public abstract record NamedShape;
    public sealed record Circle(int Radius) : NamedShape;
    public sealed record DrawNamed(NamedShape Shape);

    public sealed record Money
    {
        public int Amount { get; }
        public Money(int value) => Amount = value;
    }
    public sealed record Pay(Money Money);
    public sealed record Amount(int Value);
    public sealed record PayAmount(Amount Money);

    public sealed record Order(int Quantity, int Price)
    {
        public int Total => Quantity * Price;
    }

    [Theory]
    [InlineData(typeof(Counts))]
    [InlineData(typeof(Draw))]
    [InlineData(typeof(Pay))]
    [InlineData(typeof(Order))]
    public void A_contract_whose_schema_doesnt_read_back_is_refused_by_operation(Type contract)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([("review.read", contract)]));

        Assert.Contains($"review.read's {contract.Name}", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(CountsList))]
    [InlineData(typeof(DrawNamed))]
    [InlineData(typeof(PayAmount))]
    public void Its_readable_counterpart_is_accepted(Type contract) => OperationCatalog.Prove([("review.read", contract)]);
}

public sealed class CatalogProofValueObjectTests
{
    [ValueObject(typeof(int)), JsonConverter(typeof(QtyJson))]
    public readonly record struct Qty(int Value);

    public sealed class QtyJson : JsonConverter<Qty>
    {
        public override Qty Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt32());
        public override void Write(Utf8JsonWriter writer, Qty value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
    }

    public sealed record Line(int Sku, Qty Quantity, Qty Backordered, string Note);

    [Fact]
    public void Value_objects_anywhere_in_an_input_prove_as_the_wire_carries_them() => OperationCatalog.Prove([("review.line", typeof(Line))]);
}

public sealed class ContractLanguageTests
{
    public sealed record Priced(Dictionary<string, System.Collections.ObjectModel.ReadOnlyCollection<int>> Price);

    [JsonDerivedType(typeof(Retail), "retail")]
    [JsonDerivedType(typeof(Bundle), "bundle")]
    public abstract record Tagged;
    public sealed record Retail(int Amount) : Tagged;
    public sealed record Bundle(System.Collections.ObjectModel.ReadOnlyCollection<int> Amounts) : Tagged;
    public sealed record TaggedPrice(Tagged Price);

    public sealed record Timing(TimeSpan Every, TimeSpan? Grace, Version Api);
    public sealed record Folder(string Name, Folder? Parent = null);
    public sealed record Keyed(Dictionary<DateOnly, int> ByDay, Dictionary<Guid, string> Names);

    [Theory]
    [InlineData(typeof(Priced))]
    [InlineData(typeof(TaggedPrice))]
    public void Every_branch_and_value_a_contract_can_carry_is_in_the_language(Type contract) =>
        Assert.Contains("isn't in the contract language", Assert.Throws<InvalidOperationException>(() => OperationCatalog.Prove([("review.read", contract)])).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData(typeof(Timing))]
    [InlineData(typeof(Folder))]
    [InlineData(typeof(Keyed))]
    public void What_the_wire_reads_back_starts(Type contract) => OperationCatalog.Prove([("review.read", contract)]);
}
