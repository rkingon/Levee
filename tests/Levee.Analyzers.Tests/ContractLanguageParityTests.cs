using System.Reflection;

namespace Levee.Analyzers.Tests;

/// <summary>
/// The contract language is checked twice, by LEVEE104 over the source and by the catalog over the wire's metadata at start:
/// each case here goes through both, and the two must agree (but for what only source shows, a constructor's body).
/// </summary>
public sealed class ContractLanguageParityTests
{
    private const string Vo = """
        [System.Text.Json.Serialization.JsonConverter(typeof(QtyJson))][ValueObject(typeof(int))] public readonly record struct Qty(int Value);
        public sealed class QtyJson : System.Text.Json.Serialization.JsonConverter<Qty>
        {
            public override Qty Read(ref System.Text.Json.Utf8JsonReader r, System.Type t, System.Text.Json.JsonSerializerOptions o) => new(r.GetInt32());
            public override void Write(System.Text.Json.Utf8JsonWriter w, Qty q, System.Text.Json.JsonSerializerOptions o) => w.WriteNumberValue(q.Value);
        }

        """;

    [Theory]
    [InlineData("", "int A, string? B, System.Guid C, System.DateOnly D, System.TimeSpan E, System.Version F, System.Uri G", false, false)]
    [InlineData("public enum Size { Small, Large }", "Size A, Size? B", false, false)]
    [InlineData(Vo, "Qty A, Qty? B, System.Collections.Generic.List<Qty> C", false, false)]
    [InlineData("", "int[][] A, System.Collections.Generic.IReadOnlyList<int> B, System.Collections.Generic.HashSet<string> C", false, false)]
    [InlineData("", "System.Collections.Generic.Dictionary<System.Version, int> A, System.Collections.Generic.IReadOnlyDictionary<System.Guid, string> B", false, false)]
    [InlineData("public sealed record Folder(string Name, Folder? Parent = null);", "Folder A", false, false)]
    [InlineData("public readonly record struct Point(int X, int Y);", "Point A", false, false)]
    [InlineData("public sealed record Amount { [System.Text.Json.Serialization.JsonInclude] public int Value { get; private set; } }", "Amount A", false, false)]
    [InlineData("[System.Text.Json.Serialization.JsonDerivedType(typeof(Card), \"card\")] public abstract record Pay; public sealed record Card(string Last4) : Pay;", "Pay A", false, false)]
    [InlineData("[System.Text.Json.Serialization.JsonPolymorphic(IgnoreUnrecognizedTypeDiscriminators = true)][System.Text.Json.Serialization.JsonDerivedType(typeof(Card), \"card\")] public abstract record Pay; public sealed record Card(string Last4) : Pay;", "Pay A", true, false)]
    [InlineData("[System.Text.Json.Serialization.JsonPolymorphic(UnknownDerivedTypeHandling = System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling.FallBackToBaseType)][System.Text.Json.Serialization.JsonDerivedType(typeof(Card), \"card\")] public record Pay; public sealed record Card(string Last4) : Pay;", "Pay A", true, false)]
    [InlineData("", "System.Collections.Generic.Dictionary<Levee.Unit, int> A", true, false)]
    [InlineData("", "System.Collections.Generic.IReadOnlySet<int> A", true, false)]
    [InlineData("", "int[,] A", true, false)]
    [InlineData("", "System.Collections.ObjectModel.ReadOnlyCollection<int> A", true, false)]
    [InlineData("", "System.Collections.Generic.Queue<int> A", true, false)]
    [InlineData("", "object A", true, false)]
    [InlineData("public interface IThing { int Size { get; } }", "IThing A", true, false)]
    [InlineData("[System.Text.Json.Serialization.JsonDerivedType(typeof(Card))] public abstract record Pay; public sealed record Card(string Last4) : Pay;", "Pay A", true, false)]
    [InlineData("[System.Text.Json.Serialization.JsonDerivedType(typeof(Bundle), \"bundle\")] public record Price(int Amount); public sealed record Bundle(int Amount, System.Collections.ObjectModel.ReadOnlyCollection<int> Parts) : Price(Amount);", "Price A", true, false)]
    [InlineData("public sealed record Order(int Quantity, int Price) { public int Total => Quantity * Price; }", "Order A", true, false)]
    [InlineData("public sealed record Money { public int Amount { get; set; } public Money(int value) => Amount = value; }", "Money A", true, false)]
    [InlineData("public sealed class Bag : System.Collections.Generic.List<int>;", "Bag A", true, false)]
    // What only the source shows: which member a constructor's body, or a base call, puts each value in.
    [InlineData("public sealed record Q { public int Ordered { get; } public int Delivered { get; } public Q(int ordered, int delivered) { Ordered = delivered; Delivered = ordered; } }", "Q A", true, true)]
    [InlineData("public abstract record Counts(int Ordered, int Delivered); public sealed record Q(int Ordered, int Delivered) : Counts(Delivered, Ordered);", "Q A", true, true)]
    [InlineData("public abstract record Counts(int Ordered, int Delivered); public sealed record Q(int Ordered, int Delivered) : Counts(Ordered: Delivered, Delivered: Ordered);", "Q A", true, true)]
    [InlineData("public abstract record Counts(int Ordered, int Delivered = 0); public sealed record Q(int Ordered, int Delivered) : Counts(Delivered);", "Q A", true, true)]
    [InlineData("public abstract record Counts(int Ordered, int Delivered); public sealed record Q(int Ordered, int Delivered) : Counts(Delivered: Delivered, Ordered: Ordered);", "Q A", false, false)]
    public async Task The_build_and_the_catalog_agree_on_the_contract_language(string types, string members, bool refused, bool sourceOnly)
    {
        var name = $"Parity{(types + members).GetHashCode(StringComparison.Ordinal):x8}";
        var output = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            {{types}}
            [Operation("parity.check", "member", Platform = true)] public sealed record Check({{members}}) : IOperation<int>;
            """, name, GeneratorTests.Levee);
        Assert.DoesNotContain(output.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        var built = output.Ids.Contains("LEVEE104");

        using var image = new MemoryStream();
        Assert.True(output.Compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken).Success);
        var check = Assembly.Load(image.ToArray()).GetType("Check", throwOnError: true)!;
        var prove = typeof(OperationCatalog).GetMethod("Prove", BindingFlags.NonPublic | BindingFlags.Static)!;
        var started = Record.Exception(() => prove.Invoke(null, [new[] { ("parity.check", check) }, true])) is null;

        Assert.Equal(refused, built);
        Assert.Equal(sourceOnly ? true : !refused, started);
    }
}

public sealed class ReferencedOperationTests
{
    [Theory]
    [InlineData("public sealed record Amount { [System.Text.Json.Serialization.JsonInclude] public int Value { get; private set; } }", false)]
    [InlineData("public sealed record Amount { public int Value { get; } }", true)]
    public async Task An_operation_composed_from_another_assembly_is_in_the_catalog_or_refused_never_left_out(string amount, bool refused)
    {
        var contracts = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            {{amount}}
            [Operation("prices.set", "member", Platform = true)] public sealed record SetPrice(Amount Price) : IOperation<int>;
            """, "Shop.Contracts", GeneratorTests.Levee);
        var composition = await Probe.CompileAsync(new Probe.Settings("Composition"), "internal static class Wiring;", "Shop.Composition",
            GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration, contracts.Reference());

        Assert.Equal(refused, contracts.Ids.Contains("LEVEE104"));
        Assert.Equal(refused, composition.Ids.Contains("LEVEE104"));
        if (!refused)
        {
            Assert.Contains("prices.set", composition.Generated("Levee.Composition.g.cs"), StringComparison.Ordinal);
        }
    }
}
