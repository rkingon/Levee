namespace Levee.Analyzers.Tests;

public sealed class Alpha2ThirdReviewTests
{
    [Fact]
    public async Task A_derived_converter_attribute_requires_a_wire_declaration_in_contracts()
    {
        var output = await Probe.CompileAsync(new Probe.Settings("Contracts"), """
            using Levee;
            using System;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            public sealed class VendorCodeFormatAttribute() : JsonConverterAttribute(typeof(CodeJson));
            public readonly record struct Code(int Value);
            public sealed class CodeJson : JsonConverter<Code>
            {
                public override Code Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetInt32());
                public override void Write(Utf8JsonWriter w, Code c, JsonSerializerOptions o) => w.WriteNumberValue(c.Value);
            }
            [Operation("audit.read_code", "member", Platform = true)]
            public sealed record ReadCode([property: VendorCodeFormat] Code Value) : IOperation<int>;
            """, "Third.Contracts", GeneratorTests.Levee);
        Assert.DoesNotContain(output.Errors, e => e.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Contains("LEVEE104", output.Ids);
    }

    [Fact]
    public async Task Returning_a_cached_maintenance_error_is_a_deny_all_policy()
    {
        var output = await Probe.CompileAsync("Composition", """
            using Levee;
            using System.Threading;
            using System.Threading.Tasks;
            [Refuses("access.maintenance")]
            internal sealed class Maintenance : IAccessPolicy
            {
                private static readonly Error Closed = Error.Forbidden("access.maintenance", "Temporarily closed.");
                public ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct) =>
                    ValueTask.FromResult<Error?>(Closed);
            }
            """, "Third.Composition", GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration);
        Assert.Empty(output.Errors);
    }

    [Fact]
    public async Task An_adapter_can_hold_a_callback_that_consumes_the_current_principal()
    {
        var output = await Probe.CompileAsync("Adapter", """
            using Levee;
            using System;
            internal sealed class CallerObserver(Action<Principal> observe)
            {
                public void Record(OperationContext context) => observe(context.Principal);
            }
            """, "Third.Adapter", GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration);
        Assert.Empty(output.Errors);
    }
}
