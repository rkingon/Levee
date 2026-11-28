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

    [Theory]
    [InlineData("public sealed record Input(string Tenant);", false)]
    [InlineData("public sealed record Input(int Cents) { public int Cents { get; init; } = Cents * 100; }", true)]
    [InlineData("public sealed record Input(string Tenant, object Payload);", true)]
    public async Task What_a_process_is_started_with_is_in_the_contract_language(string input, bool refused)
    {
        var output = await Probe.CompileAsync("Application", $$"""
            using Levee;
            using System.Threading;
            using System.Threading.Tasks;
            {{input}}
            public sealed class Run : IProcess<Input>
            {
                public Task RunAsync(Input input, ProcessContext context, CancellationToken ct) => Task.CompletedTask;
            }
            """, "App.Application", [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration]);

        Assert.Equal(refused, output.Ids.Contains("LEVEE115"));
    }

    [Theory]
    [InlineData("public sealed record Receipt(int Number);", false)]
    [InlineData("public sealed record Receipt(int Number) { public string? Confirmation { get; init; } }", true)]
    public async Task What_a_step_answers_is_in_the_contract_language(string result, bool refused)
    {
        var output = await Probe.CompileAsync("Application", $$"""
            using Levee;
            using System.Threading;
            using System.Threading.Tasks;
            {{result}}
            public sealed record Input(string Tenant);
            public sealed class Run : IProcess<Input>
            {
                public async Task RunAsync(Input input, ProcessContext context, CancellationToken ct) =>
                    await context.StepAsync("pay", _ => Task.FromResult(Result<Receipt>.Success(new Receipt(1))), ct);
            }
            """, "App.Application", [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration]);

        Assert.Equal(refused, output.Ids.Contains("LEVEE115"));
    }

    [Fact]
    public async Task A_step_that_sends_an_operation_answers_its_contracts_view()
    {
        var contracts = await Probe.CompileAsync("Contracts", """
            using Levee;
            public sealed record NoteView(System.Guid Id, string Text);
            [Operation("notes.create", "member", Effects = Effects.Write, Idempotency = Idempotency.Conflicts, Platform = true)]
            public sealed record CreateNote(System.Guid Id, string Text) : IOperation<NoteView>;
            """, "App.Contracts", [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration]);
        Assert.Empty(contracts.Errors);

        var application = await Probe.CompileAsync("Application", """
            using Levee;
            using System.Threading;
            using System.Threading.Tasks;
            public sealed record Input(string Tenant);
            public sealed class Run : IProcess<Input>
            {
                public async Task RunAsync(Input input, ProcessContext context, CancellationToken ct) =>
                    await context.StepAsync("note", token => context.SendAsync(new CreateNote(System.Guid.NewGuid(), "x"), token), ct);
            }
            """, "App.Application", [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration, contracts.Reference()]);

        Assert.Empty(application.Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""
        public sealed record Reference<T>(System.Guid Id);
        [Operation("fees.find", "member", Platform = true)] public sealed record FindFee(System.Guid Id) : IOperation<Reference<Fee>>;
        """)]
    public async Task A_step_that_answers_a_contracts_record_no_operation_carries_is_judged_as_its_own(string alongside)
    {
        var contracts = await Probe.CompileAsync("Contracts", $$"""
            using Levee;
            public sealed record Fee(int Cents) { public int Cents { get; init; } = Cents * 100; }
            {{alongside}}
            """, "App.Contracts", [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration]);
        Assert.Empty(contracts.Errors);

        var application = await Probe.CompileAsync("Application", """
            using Levee;
            using System.Threading;
            using System.Threading.Tasks;
            public sealed record Input(string Tenant);
            public sealed class Run : IProcess<Input>
            {
                public async Task RunAsync(Input input, ProcessContext context, CancellationToken ct) =>
                    await context.StepAsync("fee", _ => Task.FromResult(Result<Fee>.Success(new Fee(1))), ct);
            }
            """, "App.Application", [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration, contracts.Reference()]);

        Assert.Contains(application.Errors, e => e.Id == "LEVEE115" && e.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("no operation there carries it", StringComparison.Ordinal)
            && e.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("as a plain positional record beside the process", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_input_a_process_is_started_with_is_in_the_contract_language()
    {
        var output = await Probe.CompileAsync("Application", """
            using Levee;
            using System.Threading;
            using System.Threading.Tasks;
            public sealed record Input(string Tenant);
            public sealed record Other(int Cents) { public int Cents { get; init; } = Cents * 100; }
            public sealed class Run : IProcess<Input>, IProcess<Other>
            {
                public Task RunAsync(Input input, ProcessContext context, CancellationToken ct) => Task.CompletedTask;
                public Task RunAsync(Other input, ProcessContext context, CancellationToken ct) => Task.CompletedTask;
            }
            """, "App.Application", [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration]);

        Assert.Contains("LEVEE115", output.Ids);
    }

    [Theory]
    [InlineData("public sealed class Line { public int Units { get; set; } }", true)]
    [InlineData("public sealed record Line(System.Guid Id, int Units);", false)]
    [InlineData("public class Allotted { public int Units { get; set; } } public sealed class Line : Allotted;", true)]
    [InlineData("public interface IAllocation { int Units { get; set; } } public sealed class Allocation : IAllocation { public int Units { get; set; } } public sealed class Line { public IReadOnlyList<IAllocation> Allocations { get; } = []; }", true)]
    [InlineData("public struct Place { public decimal Lat; public decimal Lng { get; set; } } public sealed record Line(Place Place);", false)]
    [InlineData("public struct Place { public List<int> Marks { get; init; } } public sealed record Line(Place Place);", true)]
    [InlineData("public sealed class Street { public string Name { get; set; } = \"\"; } public struct Place { public Street Street; } public sealed record Line(Place Place);", true)]
    [InlineData("public struct Place { public List<string> Notes { get; init; } } public sealed record Line(Place? Place);", true)]
    [InlineData("public abstract class Priced { protected List<int> Cents { get; } = []; } public sealed class Line : Priced { public int Total => Cents.Sum(); }", false)]
    [InlineData("public abstract class Counted { protected internal int Units { get; set; } } public sealed class Line : Counted;", true)]
    [InlineData("public struct Slot { public List<string> Instructions { get; init; } } public sealed class Line { public IReadOnlyList<Slot?> Slots { get; } = []; }", true)]
    [InlineData("public abstract class Adjusted { private readonly List<int> _adjustments = []; protected internal List<int> GetAdjustments() => _adjustments; } public sealed class Line : Adjusted;", true)]
    public async Task A_part_a_root_hands_out_changes_only_through_the_root(string line, bool refused)
    {
        var output = await Probe.CompileAsync("Domain", $$"""
            using Levee;
            using System.Collections.Generic;
            using System.Linq;
            {{line}}
            public sealed class Order : IAggregateRoot
            {
                private readonly List<Line> _lines = [];
                public IReadOnlyList<Line> Lines => _lines;
            }
            """, "App.Domain", [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration]);

        Assert.Equal(refused ? ["LEVEE007"] : [], output.Ids.Distinct());
    }

    [Fact]
    public async Task A_part_inheriting_what_only_its_own_project_reaches_is_guarded_by_that_project()
    {
        var shared = await Probe.CompileAsync("Domain", """
            public abstract class Counted { public int Units { get; protected internal set; } }
            """, "Shared.Domain", [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration]);
        Assert.Empty(shared.Errors);

        var output = await Probe.CompileAsync("Domain", """
            using Levee;
            using System.Collections.Generic;
            public sealed class Line : Counted;
            public sealed class Order : IAggregateRoot
            {
                private readonly List<Line> _lines = [];
                public IReadOnlyList<Line> Lines => _lines;
            }
            """, "App.Domain", [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration, shared.Reference()]);

        Assert.Empty(output.Errors);
    }
}
