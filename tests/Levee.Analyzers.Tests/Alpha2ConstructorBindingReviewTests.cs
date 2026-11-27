namespace Levee.Analyzers.Tests;

public sealed class Alpha2ConstructorBindingReviewTests
{
    private const string Overloads = """
        public abstract record Counts(int Ordered, int Delivered, int Pending = 0)
        {
            protected Counts(int Delivered, int Ordered) : this(Ordered, Delivered, 0) { }
        }
        """;

    [Theory]
    [InlineData(Overloads, "Counts(Ordered, Delivered)", true)]
    [InlineData(Overloads, "Counts(Delivered, Ordered)", false)]
    [InlineData(Overloads, "Counts(Ordered: Ordered, Delivered: Delivered)", false)]
    [InlineData("public record Counts(int Ordered, int Delivered);", "Counts(Ordered: Ordered, Delivered)", false)]
    [InlineData("public record Counts(int Ordered, int Delivered); public record Line(int Ordered, int Delivered) : Counts(Delivered, Ordered);", "Line(Ordered, Delivered)", true)]
    [InlineData("public record Counts(int Ordered, int Delivered); public record Line(int Ordered, int Delivered) : Counts(Ordered, Delivered);", "Line(Ordered, Delivered)", false)]
    public async Task Every_forwarded_argument_reaches_the_member_that_was_sent(string bases, string call, bool refused)
    {
        var built = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            {{bases}}
            public sealed record Parcel(int Ordered, int Delivered) : {{call}};
            [Operation("review.forward", "member", Platform = true)]
            public sealed record Ship(Parcel Parcel) : IOperation<int>;
            """, "Shop.Contracts", GeneratorTests.Levee);
        Assert.DoesNotContain(built.Errors, error => error.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused ? ["LEVEE104"] : [], built.Ids.Distinct());
    }

    [Theory]
    [InlineData("public")]
    [InlineData("private")]
    public async Task An_annotated_constructor_supported_by_the_wire_composes(string access)
    {
        var contracts = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            public sealed record Price
            {
                public int Amount { get; }
                [System.Text.Json.Serialization.JsonConstructor]
                {{access}} Price(int amount) => Amount = amount;
            }
            [Operation("review.price", "member", Platform = true)]
            public sealed record Quote(Price Price) : IOperation<int>;
            """, "Shop.Contracts", GeneratorTests.Levee);
        Assert.DoesNotContain("LEVEE104", contracts.Ids);
        var composition = await Probe.CompileAsync(new Probe.Settings("Composition"), "internal static class Wiring;",
            "Shop.Composition", GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration, contracts.Reference());
        Assert.DoesNotContain("LEVEE104", composition.Ids);
        Assert.Contains("review.price", composition.Generated("Levee.Composition.g.cs"), StringComparison.Ordinal);
    }
}
