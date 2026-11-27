namespace Levee.Analyzers.Tests;

public sealed class Alpha2ContractOwnershipReviewTests
{
    [Theory]
    // A base taking constructor parameters is outside the contract language, however its arguments are bound.
    [InlineData("int Ordered, int Delivered = 0", "", "Ordered, Delivered", true)]
    [InlineData("int Ordered, int Delivered = 0", "", "Ordered", true)]
    [InlineData("int Ordered, int Delivered", "protected Counts() : this(0, 0) { }", "", true)]
    public async Task A_positional_input_must_store_every_argument_bound_to_an_inherited_property(
        string parameters, string extra, string forwarding, bool refused)
    {
        var result = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            public record Counts({{parameters}}) { {{extra}} }
            public sealed record Shipment(int Ordered, int Delivered) : Counts({{forwarding}})
            {
                public int Total { get; init; } = Ordered + Delivered;
            }
            [Operation("review.ship", "member", Platform = true)]
            public sealed record Ship(Shipment Parcel) : IOperation<int>;
            """, "Shop.Contracts", GeneratorTests.Levee);
        Assert.DoesNotContain(result.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused ? ["LEVEE104"] : [], result.Ids.Distinct());
    }

    [Theory]
    [InlineData("Levee.ChangeSignal", false)]
    [InlineData("Levee.Error", true)]
    public async Task The_generator_accepts_framework_types_the_catalog_can_build(string type, bool refused)
    {
        var result = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            [Operation("review.read", "member", Platform = true)]
            public sealed record Read({{type}} Value) : IOperation<int>;
            """, "Shop.Contracts", GeneratorTests.Levee);
        Assert.DoesNotContain(result.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused ? ["LEVEE104"] : [], result.Ids.Distinct());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_owner_rule_applies_to_a_parameterless_grandparent(bool external)
    {
        const string counts = "public record Counts { public int Ordered { get; init; } }";
        var kernel = await Probe.CompileAsync(new Probe.Settings("Kernel"), external ? counts : "public enum Kind { Standard }",
            "Shop.Kernel", GeneratorTests.Levee);
        var contracts = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            {{(external ? "" : counts)}}
            public record Line : Counts;
            public sealed record Shipment : Line;
            [Operation("review.ship", "member", Platform = true)]
            public sealed record Ship(Shipment Parcel) : IOperation<int>;
            """, "Shop.Contracts", GeneratorTests.Levee, kernel.Reference());
        Assert.DoesNotContain(kernel.Errors.Concat(contracts.Errors), e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(external ? ["LEVEE104"] : [], contracts.Ids.Distinct());
    }
}
