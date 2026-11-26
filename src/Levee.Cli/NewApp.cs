using System.Text.RegularExpressions;

namespace Levee.Cli;

/// <summary>
/// <c>dotnet levee new</c>: an app with one project per role, every one referencing Levee (so its rules run everywhere),
/// one platform operation end to end, an access policy that refuses what it doesn't know, and the tool pinned.
/// </summary>
internal static partial class NewApp
{
    public static void Write(Args args, string workingDirectory, TextWriter output)
    {
        var name = args.Positional(0, "the app's name");
        if (!AppName().IsMatch(name))
        {
            throw new CliException($"'{name}' isn't an app name: PascalCase, dotted if you like (Acme.Billing).");
        }
        var root = Path.GetFullPath(args.Option("output") ?? Path.Combine(workingDirectory, name));
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new CliException($"{root} isn't empty.");
        }

        var files = Templates(name, Cli.Version, args.Option("package-source"))
            .ToDictionary(f => Path.Combine(root, f.Key.Replace('/', Path.DirectorySeparatorChar)), f => f.Value);
        Files.WriteNew(files, output);
        output.WriteLine($"""

            {name} is ready. Next:
              cd {Path.GetRelativePath(workingDirectory, root)}
              dotnet tool restore && dotnet build
              dotnet levee db schema --jobs      # the owner's DDL; then each table's: dotnet levee db isolate <table> <column> (or share <table>)
              dotnet levee db grants <app_role> --jobs
            """);
    }

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*(\\.[A-Z][A-Za-z0-9]*)*$")]
    private static partial Regex AppName();

    internal static Dictionary<string, string> Templates(string name, string version, string? packageSource)
    {
        var files = new Dictionary<string, string>
        {
            [$"{name}.slnx"] = $$"""
                <Solution>
                  <Folder Name="/src/">
                    <Project Path="src/{{name}}.Contracts/{{name}}.Contracts.csproj" />
                    <Project Path="src/{{name}}.Domain/{{name}}.Domain.csproj" />
                    <Project Path="src/{{name}}.Application/{{name}}.Application.csproj" />
                    <Project Path="src/{{name}}.Adapters/{{name}}.Adapters.csproj" />
                    <Project Path="src/{{name}}.Host/{{name}}.Host.csproj" />
                  </Folder>
                  <Folder Name="/tests/">
                    <Project Path="tests/{{name}}.Tests/{{name}}.Tests.csproj" />
                  </Folder>
                </Solution>

                """,
            ["Directory.Build.props"] = """
                <Project>
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <LangVersion>latest</LangVersion>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                    <AnalysisLevel>latest-recommended</AnalysisLevel>
                    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <!-- In every project, directly: Levee's analyzers don't flow through project references. -->
                    <PackageReference Include="Levee" />
                  </ItemGroup>
                </Project>

                """,
            ["Directory.Packages.props"] = $$"""
                <Project>
                  <ItemGroup>
                    <!-- Levee's packages and tool move together. -->
                    <PackageVersion Include="Levee" Version="{{version}}" />
                    <PackageVersion Include="Levee.EntityFrameworkCore" Version="{{version}}" />
                    <PackageVersion Include="Levee.Oxbow" Version="{{version}}" />
                    <PackageVersion Include="Levee.AspNetCore" Version="{{version}}" />
                    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />

                    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
                    <PackageVersion Include="xunit.v3" Version="3.2.2" />
                    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
                  </ItemGroup>
                </Project>

                """,
            [".config/dotnet-tools.json"] = $$"""
                {
                  "version": 1,
                  "isRoot": true,
                  "tools": {
                    "levee.cli": {
                      "version": "{{version}}",
                      "commands": ["dotnet-levee"],
                      "rollForward": false
                    }
                  }
                }

                """,
            [".editorconfig"] = """
                root = true

                [*]
                charset = utf-8
                end_of_line = lf
                insert_final_newline = true
                indent_style = space
                indent_size = 4
                trim_trailing_whitespace = true

                [*.{json,props,targets,csproj,slnx}]
                indent_size = 2

                [*.cs]
                csharp_style_namespace_declarations = file_scoped:warning

                [tests/**.cs]
                # Test names read as sentences, and xunit needs public classes.
                dotnet_diagnostic.CA1707.severity = none
                dotnet_diagnostic.CA1515.severity = none

                """,
            [".gitignore"] = """
                bin/
                obj/
                TestResults/
                *.user
                .vs/
                .idea/
                .DS_Store

                """,
            ["AGENTS.md"] = $$"""
                # {{name}}

                A Levee app: the platform is a catalog of operations, and every call goes through one pipeline. Wrong code
                fails the build (LEVEE rules) or fails at the database; fix the code, never the rule.

                - **Every capability is an operation.** `dotnet levee generate operation <name> <Type> ...` writes the
                  contract (Contracts), its handler (Application) and a catalog test. The Host, API and jobs pick it up.
                - **Handlers never authorize.** `src/{{name}}.Host/AccessPolicy.cs` decides, once, before the unit of work
                  opens; a check that needs the transaction is an `IAdmission`.
                - **Declare what an operation does.** Only reads unless it says `Effects.Write`; a write through a read is
                  refused by Postgres. Scoped (`IScopedOperation`) unless it says `Platform = true`.
                - **Nothing saves but the pipeline.** No `SaveChanges`, no transactions: the dispatcher commits once.
                - **Time is `TimeProvider`**, or `OperationContext.Now` in a handler.
                - **A job is an operation**: `IJobs.EnqueueAsync`, in the caller's transaction. Only operations safe to repeat.
                - **Ports and adapters.** Anything the application needs from outside (data, a vendor, a store) is a `[Port]`
                  in Application, or a repository (`IRepository<T>`). Adapters implement ports in the Adapters project and
                  register themselves; a port with several (`[Adapter("s3")]`, `[Adapter("embedded")]`) is chosen by its
                  `[Port(SelectedBy = "Setting:Key")]` setting. Contracts, Domain and Application do no I/O of their own.
                - **Roles**: Contracts and Domain reference nothing of the app's; Application references them; Adapters
                  implement Application's ports and never each other; the Host composes and reaches the application only
                  through the dispatcher. `dotnet levee catalog` lists what the Host serves.
                - **Suppress a LEVEE rule only beside the code, with a reason** (`Justification = "..."`, or `// why` on the
                  pragma). Project-wide suppression fails the build.

                """,

            [$"src/{name}.Contracts/{name}.Contracts.csproj"] = Project("Contracts"),
            [$"src/{name}.Contracts/Health/Ping.cs"] = $$"""
                using Levee;

                namespace {{name}}.Contracts.Health;

                public sealed record Pong(DateTimeOffset At);

                [Operation("health.ping", "anyone", Summary = "Answers with the platform's time.", Platform = true)]
                public sealed record Ping : IOperation<Pong>;

                """,

            [$"src/{name}.Domain/{name}.Domain.csproj"] = Project("Domain"),

            [$"src/{name}.Application/{name}.Application.csproj"] = Project("Application", references: [$"../{name}.Contracts/{name}.Contracts.csproj", $"../{name}.Domain/{name}.Domain.csproj"]),
            [$"src/{name}.Application/Health/PingHandler.cs"] = $$"""
                using Levee;
                using {{name}}.Contracts.Health;

                namespace {{name}}.Application.Health;

                internal sealed class PingHandler : IOperationHandler<Ping, Pong>
                {
                    public Task<Result<Pong>> HandleAsync(Ping operation, OperationContext context, CancellationToken ct) =>
                        Task.FromResult<Result<Pong>>(new Pong(context.Now));
                }

                """,

            [$"src/{name}.Adapters/{name}.Adapters.csproj"] = Project("Adapter", references: [$"../{name}.Application/{name}.Application.csproj"],
                packages: ["Levee.EntityFrameworkCore"]),
            [$"src/{name}.Adapters/AppDbContext.cs"] = $$"""
                using Levee.EntityFrameworkCore;
                using Microsoft.EntityFrameworkCore;

                namespace {{name}}.Adapters;

                /// <summary>Each operation's unit of work: one instance per operation, one transaction, committed by the pipeline.</summary>
                public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
                {
                    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddLeveeAudit();
                }

                """,

            [$"src/{name}.Host/{name}.Host.csproj"] = Project("Host", sdk: "Microsoft.NET.Sdk.Web", references: [$"../{name}.Adapters/{name}.Adapters.csproj"],
                packages: ["Levee.Oxbow", "Levee.AspNetCore"]),
            [$"src/{name}.Host/Program.cs"] = $$"""
                using Levee;
                using Levee.AspNetCore;
                using Levee.EntityFrameworkCore;
                using Levee.Generated;
                using Levee.Oxbow;
                using Microsoft.EntityFrameworkCore;
                using Npgsql;
                using {{name}}.Adapters;
                using {{name}}.Host;

                var builder = WebApplication.CreateBuilder(args);
                var postgres = builder.Configuration.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required: the app's role, which owns nothing and doesn't bypass row-level security.");

                builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres));
                builder.Services.AddDbContext<AppDbContext>((services, options) => options.UseNpgsql(services.GetRequiredService<NpgsqlDataSource>()));
                builder.Services.AddLeveeOperations(builder.Configuration);
                builder.Services.AddLeveePostgres<AppDbContext>();
                builder.Services.AddLeveeOxbow().AddLeveeJobs();
                builder.Services.AddSingleton<IAccessPolicy, AccessPolicy>();

                var app = builder.Build();
                app.MapLeveeOperations(Authentication.Principal);
                app.Run();

                """,
            [$"src/{name}.Host/AccessPolicy.cs"] = $$"""
                using Levee;

                namespace {{name}}.Host;

                /// <summary>Who a request runs as. Replace with the app's authentication; until then, everyone is anonymous.</summary>
                internal static class Authentication
                {
                    public static Principal? Principal(HttpContext http) => Anonymous.Instance;
                }

                internal sealed record Anonymous() : Principal("anonymous")
                {
                    public static readonly Anonymous Instance = new();
                }

                /// <summary>The app's authorization. Refuses every access it doesn't know, so a new one is closed until it's written here.</summary>
                [Refuses("access.unconfigured")]
                internal sealed class AccessPolicy : IAccessPolicy
                {
                    public ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct) =>
                        ValueTask.FromResult(request.Operation.Access switch
                        {
                            "anyone" => null,
                            var access => Error.Forbidden("access.unconfigured", $"Nothing grants '{access}' yet: write it in AccessPolicy."),
                        });
                }

                """,
            [$"src/{name}.Host/appsettings.json"] = """
                {
                  "ConnectionStrings": {
                    "Postgres": ""
                  },
                  "Logging": {
                    "LogLevel": {
                      "Default": "Information",
                      "Microsoft.AspNetCore": "Warning"
                    }
                  }
                }

                """,

            [$"tests/{name}.Tests/{name}.Tests.csproj"] = Project("Test", references: [$"../../src/{name}.Application/{name}.Application.csproj"],
                packages: ["Microsoft.NET.Test.Sdk", "xunit.v3", "xunit.runner.visualstudio"], test: true),
            [$"tests/{name}.Tests/CatalogTests.cs"] = $$"""
                using Levee.Generated;

                namespace {{name}}.Tests;

                public sealed class CatalogTests
                {
                    [Fact]
                    public void Every_operation_says_what_it_does() =>
                        Assert.All(LeveeComposition.Catalog.All, o => Assert.False(string.IsNullOrWhiteSpace(o.Summary), $"{o.Name} has no summary."));
                }

                """,
        };
        if (packageSource is not null)
        {
            files["nuget.config"] = $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <add key="levee" value="{{packageSource}}" />
                  </packageSources>
                </configuration>

                """;
        }
        return files;
    }

    private static string Project(string role, string sdk = "Microsoft.NET.Sdk", string[]? references = null, string[]? packages = null, bool test = false)
    {
        var items = (references ?? []).Select(r => $"""    <ProjectReference Include="{r}" />""")
            .Concat((packages ?? []).Select(p => $"""    <PackageReference Include="{p}" />"""))
            .ToList();
        var properties = test ? $"""
                <LeveeRole>{role}</LeveeRole>
                <OutputType>Exe</OutputType>
            """ : $"""
                <LeveeRole>{role}</LeveeRole>
            """;
        var itemGroup = items.Count == 0 ? "" : $"""
              <ItemGroup>
            {string.Join("\n", items)}
              </ItemGroup>

            """;
        var usings = test ? """
              <ItemGroup>
                <Using Include="Xunit" />
              </ItemGroup>

            """ : "";
        return $"""
            <Project Sdk="{sdk}">
              <PropertyGroup>
            {properties}
              </PropertyGroup>
            {itemGroup}{usings}</Project>

            """;
    }
}
