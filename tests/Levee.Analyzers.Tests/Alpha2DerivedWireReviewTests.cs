namespace Levee.Analyzers.Tests;

public sealed class Alpha2DerivedWireReviewTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task The_compiler_checks_the_members_of_declared_subtypes(bool input, bool ignored)
    {
        var parameters = input ? "Price Price" : "";
        var result = input ? "int" : "Price";
        var member = ignored ? "[JsonIgnore] public int LocalAmount => Amount;" : "public int LocalAmount() => Amount;";
        var compiled = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            using System.Text.Json.Serialization;
            [JsonDerivedType(typeof(RetailPrice), "retail")]
            public abstract record Price { public abstract int Amount { get; init; } }
            public sealed record RetailPrice(int Amount) : Price { {{member}} }
            [Operation("billing.price", "member", Platform = true)]
            public sealed record Quote({{parameters}}) : IOperation<{{result}}>;
            """, "Billing.Contracts", GeneratorTests.Levee);
        Assert.Equal(ignored ? ["LEVEE104"] : [], compiled.Ids.Distinct());
    }
}
