using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Levee.Tests;

public sealed class StepTests
{
    /// <summary>What Oxbow keeps of a job's steps: each one's result, recorded under its name once it returns.</summary>
    private sealed class Recorded
    {
        public Dictionary<string, string> Steps { get; } = [];

        public async Task<string> StepAsync(string name, Func<CancellationToken, Task<string>> run, CancellationToken ct)
        {
            if (Steps.TryGetValue(name, out var recorded))
            {
                return recorded;
            }
            return Steps[name] = await run(ct);
        }
    }

    private static ProcessContext Run(Recorded recorded, bool sealedResults = false) =>
        new(new Member("ada", "acme"), "acme", 1, DispatcherTests.Services(new Store(), new Outcomes()).GetRequiredService<Dispatcher>(), recorded.StepAsync, sealedResults);

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

    /// <summary>A sequence that can be read once: read again, it's empty.</summary>
    private sealed class Once(params int[] items) : IEnumerable<int>
    {
        private int _read;

        public IEnumerator<int> GetEnumerator() =>
            Interlocked.Exchange(ref _read, 1) == 0 ? ((IEnumerable<int>)items).GetEnumerator() : Enumerable.Empty<int>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task A_result_that_enumerates_once_is_recorded_as_it_was_written()
    {
        var counted = await Run(new Recorded()).StepAsync<IEnumerable<int>>("count",
            _ => Task.FromResult(Result<IEnumerable<int>>.Success(new Once(42))), CancellationToken.None);

        Assert.Equal([42], counted.Value);
    }

    [Fact]
    public async Task A_step_whose_result_couldnt_be_read_back_is_refused_before_it_acts()
    {
        var recorded = new Recorded();
        var ran = 0;

        var step = await Record.ExceptionAsync(() => Run(recorded).StepAsync<ISaid>("say",
            _ => Task.FromResult<Result<ISaid>>(new Said($"said {++ran}")), CancellationToken.None));

        Assert.IsType<InvalidOperationException>(step);
        Assert.Contains("isn't one a step can record and read back", step.Message, StringComparison.Ordinal);
        Assert.Equal(0, ran);
        Assert.Empty(recorded.Steps);
    }

    [Fact]
    public async Task A_step_whose_result_the_wire_cant_construct_is_refused_before_it_acts()
    {
        var ran = 0;

        var step = await Record.ExceptionAsync(() => Run(new Recorded()).StepAsync<Sealed>("make",
            _ => Task.FromResult<Result<Sealed>>(Sealed.Make(++ran)), CancellationToken.None));

        Assert.IsType<InvalidOperationException>(step);
        Assert.Equal(0, ran);
    }

    /// <summary>Made only by its own factory: the wire has no constructor to call.</summary>
    public sealed record Sealed
    {
        private Sealed()
        {
        }

        public static Sealed Make(int _) => new();
    }

    public interface ISaid
    {
        string Text { get; }
    }

    public sealed record Said(string Text) : ISaid;

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_secret_is_recorded_only_where_step_results_are_sealed(bool sealedResults, bool refused)
    {
        var recorded = new Recorded();
        var issued = 0;

        var step = Record.ExceptionAsync(() => Run(recorded, sealedResults).StepAsync("issue",
            _ => Task.FromResult<Result<IssuedToken>>(new IssuedToken("ci", $"token-{++issued}")), CancellationToken.None));

        Assert.Equal(refused, await step is InvalidOperationException);
        Assert.Equal(!refused, recorded.Steps.ContainsKey("issue"));
        // Refused before it acts, so no token is issued that can't be recorded.
        Assert.Equal(refused ? 0 : 1, issued);
    }

    [Fact]
    public async Task A_step_writes_its_result_once()
    {
        var before = WrittenJson.Count;

        await Run(new Recorded()).StepAsync("write", _ => Task.FromResult<Result<Written>>(new Written(1)), CancellationToken.None);

        Assert.Equal(1, WrittenJson.Count - before);
    }

    [Fact]
    public async Task A_step_that_answers_null_is_refused_and_recorded_nothing()
    {
        var recorded = new Recorded();

        var step = await Record.ExceptionAsync(() => Run(recorded).StepAsync<string>("say", _ => Task.FromResult(Result<string>.Success(null!)), CancellationToken.None));

        Assert.IsType<InvalidOperationException>(step);
        Assert.Empty(recorded.Steps);
    }
}

/// <summary>Counts every time it's written: what's written once is written by the wire alone.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(WrittenJson))]
[ValueObject(typeof(int))]
public sealed record Written(int Value);

public sealed class WrittenJson : System.Text.Json.Serialization.JsonConverter<Written>
{
    private static int _count;

    public static int Count => Volatile.Read(ref _count);

    public override Written Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, Written value, JsonSerializerOptions options)
    {
        Interlocked.Increment(ref _count);
        writer.WriteNumberValue(value.Value);
    }
}
