namespace Levee.Analyzers.Tests;

public sealed class Alpha2BaseArgumentsReviewTests
{
    [Theory]
    // A base taking constructor parameters is outside the contract language, however its arguments are bound.
    [InlineData("", "Delivered: Delivered, Ordered: Ordered", true)]
    [InlineData("", "Delivered: Ordered, Ordered: Delivered", true)]
    [InlineData(", int Pending = 0", "Ordered, Delivered", true)]
    [InlineData(", int Pending = 0", "Delivered, Ordered", true)]
    public async Task Positional_inputs_follow_the_bound_base_parameters(string optional, string forwarding, bool refused)
    {
        var compiled = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            public record Totals(int Ordered, int Delivered{{optional}});
            public sealed record Shipment(int Ordered, int Delivered) : Totals({{forwarding}});
            [Operation("review.ship", "member", Platform = true)]
            public sealed record Ship(Shipment Parcel) : IOperation<int>;
            """, "Billing.Contracts", GeneratorTests.Levee);
        Assert.Equal(refused ? ["LEVEE104"] : [], compiled.Ids.Distinct());
    }
}
