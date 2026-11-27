namespace Levee.Analyzers.Tests;

public sealed class Alpha2ReleaseReviewTests
{
    [Theory]
    [InlineData("sealed class")]
    [InlineData("readonly struct")]
    public async Task A_deadline_helper_holding_a_clock_is_reported(string kind)
    {
        var result = await Probe.CompileAsync("Application", $$"""
            using System;
            namespace Billing.Application;
            internal {{kind}} RetryDeadline(TimeProvider clock)
            {
                public DateTimeOffset After(TimeSpan delay) => clock.GetUtcNow() + delay;
            }
            """, "Billing.Application", GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration);
        Assert.DoesNotContain(result.Errors, d => d.Id != "LEVEE003");
        Assert.Contains(result.Errors, d => d.Id == "LEVEE003");
    }
}
