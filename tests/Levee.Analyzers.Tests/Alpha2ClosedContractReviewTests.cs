namespace Levee.Analyzers.Tests;

public sealed class Alpha2ClosedContractReviewTests
{
    [Theory]
    [InlineData("System.Collections.Generic.ISet<int>", "", false)]
    [InlineData("System.Collections.Generic.IReadOnlySet<int>", "", true)]
    [InlineData("int[][]", "", false)]
    [InlineData("int[,]", "", true)]
    [InlineData("System.Collections.Generic.Dictionary<System.Version,int>", "", false)]
    [InlineData("System.Collections.Generic.Dictionary<Levee.Unit,int>", "", true)]
    [InlineData("Price", "public sealed record Price { [System.Text.Json.Serialization.JsonInclude] public int Amount { get; private set; } }", false)]
    [InlineData("Price", "public sealed record Price { public int Amount { get; private set; } }", true)]
    [InlineData("Shipment", "public record Totals(int Ordered, int Delivered); public sealed record Shipment(int Ordered, int Delivered) : Totals(Delivered, Ordered);", true)]
    [InlineData("Shipment", "public record Totals(int Ordered, int Delivered); public sealed record Shipment(int Ordered, int Delivered) : Totals(Ordered, Delivered);", true)]
    [InlineData("Price", """[System.Text.Json.Serialization.JsonDerivedType(typeof(Bundle), "bundle")] public record Price(int Amount); public sealed record Bundle(int Amount, System.Collections.ObjectModel.ReadOnlyCollection<int> Items) : Price(Amount);""", true)]
    [InlineData("Price", "public sealed class Price { public int Amount { get; set; } public Price(int value) => Amount = value; }", true)]
    public async Task Diagnostic_agrees_with_the_supported_wire_shape(string type, string declaration, bool refused)
    {
        var compiled = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            {{declaration}}
            [Operation("review.check", "member", Platform = true)]
            public sealed record Check({{type}} Price) : IOperation<int>;
            """, "Billing.Contracts", GeneratorTests.Levee);
        Assert.Equal(refused ? ["LEVEE104"] : [], compiled.Ids.Distinct());
    }
}
