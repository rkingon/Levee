using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levee.Tests;

public sealed class Alpha2StrictWireAuditTests
{
    public sealed record Invoice(int Amount);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
    public sealed record ImportedInvoice(int Amount);

    public sealed record Loose(int Amount, [property: JsonExtensionData] Dictionary<string, JsonElement>? Rest);

    public interface IAmount { [JsonNumberHandling(JsonNumberHandling.WriteAsString)] int Amount { get; } }
    public interface IQuote : IAmount;

    [Fact]
    public void The_runtime_detects_the_number_override_inherited_by_the_result_interface()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => LeveeJson.Schema(typeof(IQuote)));
        Assert.Contains("[JsonNumberHandling]", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_members_are_refused_however_a_contract_asks_to_keep_them()
    {
        const string json = """[{"amount":4,"currency":"EUR"}]""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Invoice[]>(json, LeveeJson.Options));
        Assert.Contains("[JsonUnmappedMemberHandling(Skip)]", Assert.Throws<InvalidOperationException>(() => JsonSerializer.Deserialize<ImportedInvoice[]>(json, LeveeJson.Options)).Message, StringComparison.Ordinal);
        Assert.Contains("[JsonExtensionData]", Assert.Throws<InvalidOperationException>(() => JsonSerializer.Deserialize<Loose>("""{"amount":4}""", LeveeJson.Options)).Message, StringComparison.Ordinal);
    }
}
