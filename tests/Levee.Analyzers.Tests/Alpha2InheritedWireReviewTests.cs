using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Levee.Analyzers.Tests;

public sealed class Alpha2InheritedWireReviewTests
{
    private const string Optional = "public record Counts(int Delivered = 0);";
    private const string Receipt = " { public bool Entered { get; init; } = Delivered > 0; }";
    private const string Entered = """{"delivered":2,"entered":true}""";
    private const string Keyword = "public record Counts(int @event = 0);";
    private const string EventReceipt = " { public bool Entered { get; init; } = @event > 0; }";
    private const string Event = """{"event":2,"entered":true}""";
    private const string Field = "public record Counts { [System.Text.Json.Serialization.JsonInclude] internal int Delivered = 0; }";
    private const string Abstract = "public abstract record Counts { public abstract int Delivered { get; init; } }";
    private const string Override = "public record DefaultCounts { public virtual int Delivered { get; init; } } public abstract record Counts : DefaultCounts { public abstract override int Delivered { get; init; } }";
    private const string Overload = "public record Counts(int Ordered, int Delivered) { protected Counts(int pending = 0) : this(0, 0) { } }";
    private const string Amounts = """{"ordered":300,"delivered":2,"amount":7}""";

    [Theory]
    // A base taking constructor parameters is outside the contract language, however its arguments are bound.
    [InlineData(Keyword + "public sealed record Shipment(int @event) : Counts(@event)" + EventReceipt, Event, true, true)]
    [InlineData(Keyword + "public sealed record Shipment(int @event) : Counts()" + EventReceipt, Event, false, true)]
    [InlineData(Optional + "public sealed record Shipment(int Delivered) : Counts" + Receipt, Entered, false, true)]
    [InlineData(Optional + "public sealed partial record Shipment(int Delivered)" + Receipt + "public sealed partial record Shipment : Counts;", Entered, false, true)]
    [InlineData(Field + "public sealed record Shipment(int Delivered) : Counts" + Receipt, Entered, false, true)]
    // A field is outside the contract language.
    [InlineData(Field + "public sealed record Shipment : Counts;", """{"delivered":2}""", true, true)]
    [InlineData(Abstract + "public sealed record Shipment(int Delivered) : Counts;", """{"delivered":2}""", true, false)]
    [InlineData(Override + "public sealed record Shipment(int Delivered) : Counts;", """{"delivered":2}""", true, false)]
    [InlineData(Overload + "public sealed record Shipment(int Amount) : Counts();", Amounts, true, true)]
    [InlineData(Overload + "public sealed record Shipment(int Amount) : Counts;", Amounts, true, true)]
    public async Task The_build_judges_the_bound_member_and_constructor(string declarations, string json, bool preservesInput, bool shouldRefuse)
    {
        var output = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            {{declarations}}
            [Operation("review.ship", "member", Platform = true)]
            public sealed record Ship(Shipment Parcel) : IOperation<int>;
            """, $"Shop.Contracts{Guid.NewGuid():N}", GeneratorTests.Levee);
        Assert.DoesNotContain(output.Errors, d => d.Id.StartsWith("CS", StringComparison.Ordinal));

        // Run the wire independently so a build refusal can't mask what a supported DTO would actually do.
        using var image = new MemoryStream();
        Assert.True(output.Compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken).Success);
        var type = Assembly.Load(image.ToArray()).GetType("Shipment", throwOnError: true)!;
        var value = JsonSerializer.Deserialize(json, type, LeveeJson.Options);
        var written = JsonSerializer.SerializeToNode(value, type, LeveeJson.Options);
        Assert.Equal(preservesInput, JsonNode.DeepEquals(JsonNode.Parse(json), written));
        Assert.Equal(shouldRefuse, output.Ids.Contains("LEVEE104"));
    }

    [Theory]
    [InlineData("ChangeSignal")]
    [InlineData("Invoice")]
    public async Task A_nested_contract_is_checked_using_its_declaring_type(string name)
    {
        var output = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            namespace Levee
            {
                public static class BillingEvents
                {
                    public sealed record {{name}}(int Quantity) { public int Count => Quantity + 1; }
                }
            }
            [Operation("review.read", "member", Platform = true)]
            public sealed record Read(Levee.BillingEvents.{{name}} Detail) : IOperation<int>;
            """, "Shop.Contracts", GeneratorTests.Levee);
        Assert.DoesNotContain(output.Errors, d => d.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Contains("LEVEE104", output.Ids);
    }
}
