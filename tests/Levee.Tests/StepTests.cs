using System.Text.Json;

namespace Levee.Tests;

public sealed class StepTests
{
    /// <summary>What Oxbow keeps of a job's steps: each one's result, recorded under its name once it returns.</summary>
    private sealed class Recorded
    {
        public Dictionary<string, JsonElement> Steps { get; } = [];

        public async Task<JsonElement> StepAsync(string name, Func<CancellationToken, Task<JsonElement>> run, CancellationToken ct)
        {
            if (Steps.TryGetValue(name, out var recorded))
            {
                return recorded;
            }
            return Steps[name] = await run(ct);
        }
    }

    private static ProcessContext Run(Recorded recorded, bool sealedResults = false) =>
        new(new Member("ada", "acme"), "acme", 1, (_, _) => throw new InvalidOperationException("sends nothing"), recorded.StepAsync, sealedResults);

    [Fact]
    public async Task A_step_done_is_answered_from_its_record_on_every_later_run()
    {
        var recorded = new Recorded();
        var ran = 0;
        Task<Result<EchoView>> Say(CancellationToken ct) => Task.FromResult<Result<EchoView>>(new EchoView($"said {++ran}"));

        var first = await Run(recorded).StepAsync("say", Say, CancellationToken.None);
        var later = await Run(recorded).StepAsync("say", Say, CancellationToken.None);

        Assert.Equal(("said 1", "said 1", 1), (first.Value.Said, later.Value.Said, ran));
    }

    [Fact]
    public async Task A_failed_step_records_nothing_and_runs_again()
    {
        var recorded = new Recorded();
        var tries = 0;
        Task<Result<int>> Flaky(CancellationToken ct) =>
            Task.FromResult(++tries == 1 ? Result<int>.Failure(Error.Unavailable("probe.busy", "Busy.")) : Result<int>.Success(tries));

        var failed = await Run(recorded).StepAsync("flaky", Flaky, CancellationToken.None);
        var retried = await Run(recorded).StepAsync("flaky", Flaky, CancellationToken.None);

        Assert.Equal("probe.busy", failed.Error!.Code);
        Assert.Equal(2, retried.Value);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_secret_is_recorded_only_where_step_results_are_sealed(bool sealedResults, bool refused)
    {
        var recorded = new Recorded();

        var step = Record.ExceptionAsync(() => Run(recorded, sealedResults).StepAsync("issue",
            _ => Task.FromResult<Result<IssuedToken>>(new IssuedToken("ci", "token-ci")), CancellationToken.None));

        Assert.Equal(refused, await step is InvalidOperationException);
        Assert.Equal(!refused, recorded.Steps.ContainsKey("issue"));
    }
}
