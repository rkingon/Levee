using System.Globalization;
using System.Text.RegularExpressions;

namespace Levee.Cli;

/// <summary>
/// <c>dotnet levee generate operation</c>: the contract in the Contracts project, a handler in Application that refuses
/// until it's written, and a test in the Test project holding the catalog to what was declared. Everything the generator
/// checks is asked for up front, so what's written builds.
/// </summary>
internal static partial class GenerateOperation
{
    public static void Write(Args args, string workingDirectory, TextWriter output)
    {
        var name = args.Positional(1, "the operation's name (notes.create)");
        var type = args.Positional(2, "the operation's type (CreateNote)");
        if (!OperationName().IsMatch(name))
        {
            throw new CliException($"'{name}' isn't an operation name: lowercase and dotted, like notes.create.");
        }
        if (!TypeName().IsMatch(type))
        {
            throw new CliException($"'{type}' isn't a type name.");
        }
        var access = args.Required("access", "what the caller needs, as the app's access policy names it");
        var summary = args.Required("summary", "what the operation does, for every projection and agent that lists it");
        var platform = args.Flag("platform");
        var scopedBy = args.Option("scoped-by");
        if (platform == scopedBy is not null)
        {
            throw new CliException("Say whose rows it sees: --scoped-by <Property> (its scope key, e.g. Tenant) or --platform (every scope).");
        }
        if (scopedBy is not null && !TypeName().IsMatch(scopedBy))
        {
            throw new CliException($"'{scopedBy}' isn't a property name.");
        }
        var writes = args.Flag("writes");
        var idempotency = args.Option("idempotency");
        if (writes && idempotency is null)
        {
            throw new CliException("An operation that writes says whether repeating it is safe: --idempotency idempotent|conflicts|none.");
        }
        if (!writes && idempotency is not null)
        {
            throw new CliException("--idempotency is for an operation that --writes; one that only reads is safe to repeat.");
        }
        var idempotencyMember = idempotency switch
        {
            null => null,
            "idempotent" => "Idempotent",
            "conflicts" => "Conflicts",
            "none" => "None",
            _ => throw new CliException($"--idempotency is idempotent, conflicts or none, not '{idempotency}'."),
        };
        var errors = (args.Option("errors") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (errors.FirstOrDefault(e => !OperationName().IsMatch(e)) is { } badCode)
        {
            throw new CliException($"'{badCode}' isn't an error code: lowercase and dotted, like notes.too_long.");
        }
        var result = args.Option("result") ?? "Unit";
        if (!TypeName().IsMatch(result))
        {
            throw new CliException($"'{result}' isn't a type name.");
        }

        var root = Files.AppRoot(workingDirectory);
        var contracts = Files.OneProjectOf(root, "Contracts");
        var application = Files.OneProjectOf(root, "Application");
        var tests = Files.ProjectsOf(root, "Test");
        var area = Area(name);
        var contractsNamespace = $"{Files.RootNamespace(contracts)}.{area}";

        var metadata = new List<string> { $"Summary = {Literal(summary)}" };
        if (writes)
        {
            metadata.Add("Effects = Effects.Write");
            metadata.Add($"Idempotency = Idempotency.{idempotencyMember}");
        }
        if (platform)
        {
            metadata.Add("Platform = true");
        }
        if (errors.Length > 0)
        {
            metadata.Add($"Errors = [{string.Join(", ", errors.Select(Literal))}]");
        }
        var parameters = scopedBy is null ? "" : $"string {scopedBy}";
        var scopeKey = scopedBy is null ? ";" : $$"""

            {
                string IScopedOperation.ScopeKey => {{scopedBy}};
            }
            """;
        // A result another operation already declared (a view two operations answer with) is reused, not declared again.
        var declaredIn = result == "Unit" ? null : Directory.EnumerateFiles(Path.GetDirectoryName(contracts)!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .FirstOrDefault(text => System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b(record|class|struct|interface)\s+{System.Text.RegularExpressions.Regex.Escape(result)}\b"));
        // Declared in another area: the files that name it need that area's namespace.
        var resultNamespace = declaredIn is null ? null
            : System.Text.RegularExpressions.Regex.Match(declaredIn, @"^\s*namespace\s+([\w.]+)", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } m
                ? m.Groups[1].Value : null;
        var declared = result == "Unit" || declaredIn is not null;
        var resultDeclaration = declared ? "" : $"""

            public sealed record {result}();

            """;

        var files = new Dictionary<string, string>
        {
            [Path.Combine(Path.GetDirectoryName(contracts)!, area, $"{type}.cs")] = $$"""
                using Levee;

                namespace {{contractsNamespace}};
                {{resultDeclaration}}
                [Operation({{Literal(name)}}, {{Literal(access)}}, {{string.Join(", ", metadata)}})]
                public sealed record {{type}}({{parameters}}) : IOperation<{{result}}>{{(scopedBy is null ? "" : ", IScopedOperation")}}{{scopeKey}}

                """,
            [Path.Combine(Path.GetDirectoryName(application)!, area, $"{type}Handler.cs")] = $$"""
                using Levee;
                using {{contractsNamespace}};

                namespace {{Files.RootNamespace(application)}}.{{area}};

                internal sealed class {{type}}Handler : IOperationHandler<{{type}}, {{result}}>
                {
                    public Task<Result<{{result}}>> HandleAsync({{type}} operation, OperationContext context, CancellationToken ct) =>
                        Task.FromResult<Result<{{result}}>>(Error.Unavailable(LeveeErrors.NotImplemented, {{Literal($"{name} isn't implemented yet.")}}));
                }

                """,
        };
        if (tests.Count == 1)
        {
            files[Path.Combine(Path.GetDirectoryName(tests[0])!, area, $"{type}Tests.cs")] = $$"""
                using Levee;
                using Levee.Generated;

                namespace {{Files.RootNamespace(tests[0])}}.{{area}};

                public sealed class {{type}}Tests
                {
                    [Fact]
                    public void Is_in_the_catalog_as_declared()
                    {
                        var operation = LeveeComposition.Catalog.Find({{Literal(name)}});

                        Assert.NotNull(operation);
                        Assert.Equal(({{Literal(access)}}, Effects.{{(writes ? "Write" : "Read")}}, {{(platform ? "true" : "false")}}), (operation.Access, operation.Effects, operation.Platform));
                    }
                }

                """;
        }
        if (resultNamespace is not null && resultNamespace != contractsNamespace)
        {
            files = files.ToDictionary(f => f.Key, f => f.Value.Replace("using Levee;\n", $"using Levee;\nusing {resultNamespace};\n", StringComparison.Ordinal));
        }
        Files.WriteNew(files, output);
    }

    /// <summary>The name's first segment as a folder and namespace: <c>user_profiles.create</c> is UserProfiles.</summary>
    private static string Area(string name) =>
        string.Concat(name.Split('.')[0].Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..]));

    private static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$")]
    private static partial Regex OperationName();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex TypeName();
}
