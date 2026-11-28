namespace Levee.Analyzers.Tests;

/// <summary>What the alpha.3 release audit found in the generator, held to.</summary>
public sealed class Alpha3AuditTests
{
    [Theory]
    [InlineData("IOperation<int>, IOperation<string>", true)]
    [InlineData("IOperation<int>, ISubscription<int>", true)]
    [InlineData("IOperation<int>", false)]
    public async Task An_operation_declares_one_contract(string contracts, bool refused)
    {
        var output = await Probe.CompileAsync("Contracts", $$"""
            using Levee;
            [Operation("audit.count", "member")] public sealed record Count(int From) : {{contracts}};
            """, "App.Contracts", [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration]);

        Assert.Equal(refused, output.Ids.Contains("LEVEE104"));
    }
}
