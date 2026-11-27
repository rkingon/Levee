namespace Levee.Analyzers.Tests;

public sealed class Alpha2FourthReviewTests
{
    [Theory]
    [InlineData("DateOnly ContractEnd { get; }", "public DateOnly ContractEnd => End;", "ContractEnd")]
    [InlineData("DateOnly EndOfContract(int contract);", "public DateOnly EndOfContract(int contract) => End;", "EndOfContract(7)")]
    public async Task Configured_period_end_is_domain_data_in_a_handler(string member, string implementation, string use)
    {
        var result = await Probe.CompileAsync("Application", $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Levee;
            [Operation("billing.period", "member", Platform = true, Internal = true)]
            public sealed record BillingPeriod : IOperation<string>;
            public interface IBillingTerms { {{member}} }
            public sealed record BillingTerms(DateOnly End) : IBillingTerms { {{implementation}} }
            internal sealed class BillingPeriodHandler(IBillingTerms terms) : IOperationHandler<BillingPeriod, string>
            {
                public Task<Result<string>> HandleAsync(BillingPeriod op, OperationContext context, CancellationToken ct) =>
                    Task.FromResult<Result<string>>(terms.{{use}}.ToString("O"));
            }
            """, "Review.Application", GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration);
        Assert.DoesNotContain(result.Errors, d => d.Id != "LEVEE003");
        Assert.DoesNotContain(result.Errors, d => d.Id == "LEVEE003");
    }
}
