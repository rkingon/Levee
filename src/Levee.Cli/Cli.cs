using System.Reflection;

namespace Levee.Cli;

internal static class Cli
{
    public const string Usage = """
        Usage:
          dotnet levee new <Name> [--output DIR] [--package-source SOURCE]
              An app with a project per role, wired to Levee, building clean.
          dotnet levee generate operation <name> <Type> --access A --summary S (--platform | --scoped-by PROPERTY)
                                          [--writes --idempotency idempotent|conflicts|none] [--result TYPE] [--errors CODE,...]
              The contract, its handler and a catalog test, in the app's Contracts, Application and Test projects.
          dotnet levee catalog [PROJECT | ASSEMBLY | DIRECTORY] [--no-build] [--json]
              Every operation a Host (by default, the app's one Host) can see, read from its build.
          dotnet levee check [DIRECTORY] [--json]
              Every LEVEE rule the app breaks, project by project: compiles everything once without the analyzers, then
              analyzes each project alone, so one project's errors never hide another's.
          dotnet levee explain [LEVEEnnn]
              A rule's why, the shape it refuses and the shape to write instead; every rule, without an id.
          dotnet levee db schema [--jobs] [--schema S]    The owner's DDL: Levee's functions and audit (and Oxbow's tables).
          dotnet levee db isolate <table> <column> [--type TYPE] [--schema S]   A table's row-level security policy.
          dotnet levee db grants <role> [--jobs] [--schema S]   What the app's runtime role needs.
          dotnet levee version
        """;

    public static string Version { get; } =
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            await output.WriteLineAsync(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var a = Args.Parse(args.Skip(1));
            switch (args[0])
            {
                case "new":
                    NewApp.Write(a, workingDirectory, output);
                    return 0;
                case "generate" when a.Positional(0, "what to generate") == "operation":
                    GenerateOperation.Write(a, workingDirectory, output);
                    return 0;
                case "generate":
                    throw new CliException($"Levee generates operations only: dotnet levee generate operation ...");
                case "catalog":
                    await CatalogCommand.RunAsync(a, workingDirectory, output);
                    return 0;
                case "check":
                    return await Check.RunAsync(a, workingDirectory, output);
                case "explain":
                    Explain.Run(a, output);
                    return 0;
                case "db":
                    Db.Run(a, output);
                    return 0;
                case "version":
                    await output.WriteLineAsync(Version);
                    return 0;
                default:
                    throw new CliException($"There's no command {args[0]}.");
            }
        }
        catch (CliException e)
        {
            await error.WriteLineAsync(e.Message);
            return 1;
        }
    }
}

internal sealed class CliException(string message) : Exception(message);

/// <summary>Positional arguments, <c>--name value</c> options and the known <c>--flags</c>.</summary>
internal sealed class Args
{
    private static readonly HashSet<string> Flags = ["platform", "writes", "jobs", "json", "no-build"];
    private readonly List<string> _positionals = [];
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public static Args Parse(IEnumerable<string> tokens)
    {
        var args = new Args();
        using var e = tokens.GetEnumerator();
        while (e.MoveNext())
        {
            if (!e.Current.StartsWith("--", StringComparison.Ordinal))
            {
                args._positionals.Add(e.Current);
                continue;
            }
            var name = e.Current[2..];
            if (Flags.Contains(name))
            {
                args._flags.Add(name);
            }
            else if (e.MoveNext())
            {
                args._options[name] = e.Current;
            }
            else
            {
                throw new CliException($"--{name} needs a value.");
            }
        }
        return args;
    }

    public int Count => _positionals.Count;

    public string Positional(int index, string what) =>
        index < _positionals.Count ? _positionals[index] : throw new CliException($"Missing {what}. See dotnet levee help.");

    public string? Option(string name) => _options.GetValueOrDefault(name);

    public string Required(string name, string why) => Option(name) ?? throw new CliException($"--{name} is required: {why}.");

    public bool Flag(string name) => _flags.Contains(name);
}

internal static class Files
{
    /// <summary>The nearest directory at or above <paramref name="start"/> holding a solution.</summary>
    public static string AppRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.slnx").Any() || dir.EnumerateFiles("*.sln").Any())
            {
                return dir.FullName;
            }
        }
        throw new CliException($"No solution at or above {start}: run this inside a Levee app.");
    }

    /// <summary>The app's projects of <paramref name="role"/>, by their <c>LeveeRole</c>.</summary>
    public static IReadOnlyList<string> ProjectsOf(string root, string role) =>
        [.. Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains($"<LeveeRole>{role}</LeveeRole>", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    public static string OneProjectOf(string root, string role)
    {
        var found = ProjectsOf(root, role);
        return found.Count switch
        {
            1 => found[0],
            0 => throw new CliException($"The app has no {role} project (<LeveeRole>{role}</LeveeRole>)."),
            _ => throw new CliException($"The app has {found.Count} {role} projects ({string.Join(", ", found.Select(Path.GetFileName))}); this needs exactly one."),
        };
    }

    public static string RootNamespace(string csproj)
    {
        var text = File.ReadAllText(csproj);
        var start = text.IndexOf("<RootNamespace>", StringComparison.Ordinal);
        return start < 0
            ? Path.GetFileNameWithoutExtension(csproj)
            : text[(start + "<RootNamespace>".Length)..text.IndexOf("</RootNamespace>", start, StringComparison.Ordinal)].Trim();
    }

    /// <summary>Writes every file, refusing to overwrite any: nothing is written if one exists.</summary>
    public static void WriteNew(IReadOnlyDictionary<string, string> files, TextWriter output)
    {
        if (files.Keys.FirstOrDefault(File.Exists) is { } existing)
        {
            throw new CliException($"{existing} already exists; nothing was written.");
        }
        foreach (var (path, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.ReplaceLineEndings("\n"));
            output.WriteLine($"  create  {path}");
        }
    }
}
