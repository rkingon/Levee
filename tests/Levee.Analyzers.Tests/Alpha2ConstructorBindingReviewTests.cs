namespace Levee.Analyzers.Tests;

public sealed class Alpha2ConstructorBindingReviewTests
{
    private const string Overloads = """
        public abstract record Counts(int Ordered, int Delivered, int Pending = 0)
        {
            protected Counts(int Delivered, int Ordered) : this(Ordered, Delivered, 0) { }
        }
        """;

    // Through Overloads' protected constructor, refused however it's bound: a base call binds the base's positional constructor.
    [Theory]
    [InlineData(Overloads, "Counts(Ordered, Delivered)", true)]
    [InlineData(Overloads, "Counts(Delivered, Ordered)", true)]
    [InlineData(Overloads, "Counts(Ordered: Ordered, Delivered: Delivered)", true)]
    // A base taking constructor parameters is outside the contract language, however its arguments are bound.
    [InlineData("public record Counts(int Ordered, int Delivered);", "Counts(Ordered: Ordered, Delivered)", true)]
    [InlineData("public record Counts(int Ordered, int Delivered); public record Line(int Ordered, int Delivered) : Counts(Delivered, Ordered);", "Line(Ordered, Delivered)", true)]
    [InlineData("public record Counts(int Ordered, int Delivered); public record Line(int Ordered, int Delivered) : Counts(Ordered, Delivered);", "Line(Ordered, Delivered)", true)]
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
    // A constructor of its own, annotated or not, decides where values land: the contract language builds an object only by its
    // positional constructor or a parameterless one, so this is refused where it's declared, and never dropped where it's composed.
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
        Assert.Contains("LEVEE104", contracts.Ids);
        var composition = await Probe.CompileAsync(new Probe.Settings("Composition"), "internal static class Wiring;",
            "Shop.Composition", GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration, contracts.Reference());
        Assert.DoesNotContain("LEVEE104", composition.Ids);
        Assert.Contains("review.price", composition.Generated("Levee.Composition.g.cs"), StringComparison.Ordinal);
    }
}
