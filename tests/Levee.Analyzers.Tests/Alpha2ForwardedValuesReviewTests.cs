namespace Levee.Analyzers.Tests;

public sealed class Alpha2ForwardedValuesReviewTests
{
    private const string Primary = "public record Counts(int Ordered, int Delivered);";

    [Theory]
    [InlineData("Ordered", false)]
    [InlineData("(byte)Ordered", true)]
    public async Task A_base_argument_must_preserve_the_supplied_integer(string argument, bool refused)
    {
        var result = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            {{Primary}}
            public sealed record Shipment(int Ordered, int Delivered) : Counts({{argument}}, Delivered);
            [Operation("review.ship", "member", Platform = true)] public sealed record Ship(Shipment Parcel) : IOperation<int>;
            """, "Shop.Contracts", GeneratorTests.Levee);
        Assert.DoesNotContain(result.Errors, d => d.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused ? ["LEVEE104"] : [], result.Ids.Distinct());
    }

    // Either way: a base call binds the base's positional constructor, not one of its own that chains on through this(...).
    [Theory]
    [InlineData("Ordered, Delivered", true)]
    [InlineData("Delivered, Ordered", true)]
    public async Task A_selected_overload_must_forward_its_own_arguments_faithfully(string next, bool refused)
    {
        var result = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            public record Counts(int Ordered, int Delivered, int Pending)
            {
                protected Counts(int Ordered, int Delivered) : this({{next}}, 0) { }
            }
            public sealed record Shipment(int Ordered, int Delivered) : Counts(Ordered, Delivered);
            [Operation("review.ship", "member", Platform = true)] public sealed record Ship(Shipment Parcel) : IOperation<int>;
            """, "Shop.Contracts", GeneratorTests.Levee);
        Assert.DoesNotContain(result.Errors, d => d.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused ? ["LEVEE104"] : [], result.Ids.Distinct());
    }

    // Either way: a contract's record has no base from another assembly, whose forwarding only its own source shows.
    [Theory]
    [InlineData("Ordered, Delivered", true)]
    [InlineData("Delivered, Ordered", true)]
    public async Task A_kernel_record_in_the_forwarding_chain_is_checked(string forwarding, bool refused)
    {
        var kernel = await Probe.CompileAsync(new Probe.Settings("Kernel"), $$"""
            {{Primary}}
            public record Line(int Ordered, int Delivered) : Counts({{forwarding}});
            """, "Shop.Kernel", GeneratorTests.Levee);
        var contracts = await Probe.CompileAsync(new Probe.Settings("Contracts"), """
            using Levee;
            public sealed record Shipment(int Ordered, int Delivered) : Line(Ordered, Delivered);
            [Operation("review.ship", "member", Platform = true)] public sealed record Ship(Shipment Parcel) : IOperation<int>;
            """, "Shop.Contracts", GeneratorTests.Levee, kernel.Reference());
        Assert.DoesNotContain(kernel.Errors.Concat(contracts.Errors), d => d.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(refused ? ["LEVEE104"] : [], kernel.Ids.Concat(contracts.Ids).Distinct());
    }

    // A Kernel record crosses into a contract only as a value object: metadata hides how its constructor routes values.
    [Theory]
    [InlineData("public")]
    [InlineData("private")]
    public async Task A_supported_kernel_value_can_be_reused_in_an_operation(string access)
    {
        var kernel = await Probe.CompileAsync(new Probe.Settings("Kernel"), $$"""
            public sealed record Price
            {
                public int Amount { get; }
                [System.Text.Json.Serialization.JsonConstructor]
                {{access}} Price(int amount) => Amount = amount;
            }
            """, "Shop.Kernel", GeneratorTests.Levee);
        using var image = new MemoryStream();
        Assert.True(kernel.Compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken).Success);
        var type = System.Reflection.Assembly.Load(image.ToArray()).GetType("Price", throwOnError: true)!;
        const string wire = """{"amount":12}""";
        var read = System.Text.Json.JsonSerializer.Deserialize(wire, type, LeveeJson.Options);
        Assert.Equal(wire, System.Text.Json.JsonSerializer.Serialize(read, type, LeveeJson.Options));
        var contracts = await Probe.CompileAsync(new Probe.Settings("Contracts"), """
            using Levee;
            [Operation("review.price", "member", Platform = true)] public sealed record Quote(Price Price) : IOperation<int>;
            """, "Shop.Contracts", GeneratorTests.Levee, kernel.Reference());
        Assert.DoesNotContain(kernel.Errors.Concat(contracts.Errors), d => d.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(["LEVEE104"], kernel.Ids.Concat(contracts.Ids).Distinct());
    }
}
