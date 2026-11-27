namespace Levee.Analyzers.Tests;

public sealed class Alpha2NumberHandlingAuditTests
{
    [Theory]
    [InlineData("IAmount", "Strict", false)]
    [InlineData("IAmount", "WriteAsString", true)]
    [InlineData("IQuote", "Strict", false)]
    [InlineData("IQuote", "WriteAsString", true)]
    public async Task Effective_result_interface_members_follow_the_number_rule(string result, string mode, bool refused)
    {
        var compiled = await Probe.CompileAsync(new Probe.Settings("Contracts"), $$"""
            using Levee;
            using System.Text.Json.Serialization;
            public interface IAmount { [JsonNumberHandling(JsonNumberHandling.{{mode}})] int Amount { get; } }
            public interface IQuote : IAmount { }
            [Operation("billing.quote", "member", Platform = true)]
            public sealed record Quote : IOperation<{{result}}>;
            """, "Billing.Contracts", GeneratorTests.Levee);
        Assert.Equal(refused ? ["LEVEE104"] : [], compiled.Ids.Distinct());
    }
}
