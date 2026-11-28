using Microsoft.CodeAnalysis;

namespace Levee.Analyzers.Tests;

/// <summary>LEVEE114: an edit held to the version its caller saw writes, and loads the root it changes.</summary>
public sealed class VersionedOperationTests
{
    private static Task<Probe.Output> Compile(string role, string code, params MetadataReference[] more) =>
        Probe.CompileAsync(role, code, "App." + role, [GeneratorTests.Levee, GeneratorTests.Di, GeneratorTests.Configuration, .. more]);

    private static string Edit(string effects) => $$"""
        using Levee;
        [Operation("notes.edit", "member"{{effects}})]
        public sealed record EditNote(string Tenant, string Text, uint Version) : IOperation<int>, IScopedOperation, IVersionedOperation
        {
            string IScopedOperation.ScopeKey => Tenant;
            uint IVersionedOperation.ExpectedVersion => Version;
        }
        """;

    [Theory]
    [InlineData("", true)]
    [InlineData(", Effects = Effects.Write, Idempotency = Idempotency.Idempotent", false)]
    public async Task A_versioned_operation_writes(string effects, bool refused)
    {
        var contracts = await Compile("Contracts", Edit(effects));

        Assert.Equal(refused ? ["LEVEE114"] : [], contracts.Ids.Distinct());
    }

    [Fact]
    public async Task How_its_handler_reaches_the_root_is_left_to_the_application()
    {
        var contracts = await Compile("Contracts", Edit(", Effects = Effects.Write, Idempotency = Idempotency.Idempotent"));
        var domain = await Compile("Domain", "public sealed class Note : Levee.IAggregateRoot;");

        // Through a helper of Application's that holds the repository: held at run time, not by the build.
        var application = await Compile("Application", """
            using Levee;
            using System.Threading;
            using System.Threading.Tasks;
            public interface INotes : IRepository<Note>;
            internal sealed class NoteStore(INotes notes)
            {
                public INotes Notes => notes;
            }
            internal sealed class EditNoteHandler(NoteStore store) : IOperationHandler<EditNote, int>
            {
                public Task<Result<int>> HandleAsync(EditNote operation, OperationContext context, CancellationToken ct) => Task.FromResult<Result<int>>(1);
            }
            """, contracts.Reference(), domain.Reference());

        Assert.Empty(application.Errors);
    }
}
