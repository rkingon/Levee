using Levee.Cli;

namespace Levee.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("levee-cli-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<(int Code, string Output, string Error)> Run(string workingDirectory, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Levee.Cli.Cli.RunAsync(args, output, error, workingDirectory);
        return (code, output.ToString(), error.ToString());
    }

    private string Read(params string[] path) => File.ReadAllText(Path.Combine([_dir, .. path]));

    [Fact]
    public void Check_takes_the_solutions_projects_not_every_project_on_disk()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src", "App"));
        Directory.CreateDirectory(Path.Combine(_dir, "templates", "Starter"));
        File.WriteAllText(Path.Combine(_dir, "src", "App", "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_dir, "templates", "Starter", "Starter.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_dir, "App.slnx"), """<Solution><Project Path="src/App/App.csproj" /></Solution>""");
        File.WriteAllText(Path.Combine(_dir, "App.sln"), "Project(\"{FAE04EC0}\") = \"App\", \"src\\App\\App.csproj\", \"{1}\"\nEndProject\n");

        Assert.Equal([Path.Combine(_dir, "src", "App", "App.csproj")], Check.ProjectsOf(Path.Combine(_dir, "App.slnx")).Select(Path.GetFullPath));
        Assert.Equal([Path.Combine(_dir, "src", "App", "App.csproj")], Check.ProjectsOf(Path.Combine(_dir, "App.sln")).Select(Path.GetFullPath));
    }

    [Fact]
    public async Task A_project_a_Levee_project_references_has_a_role_and_runs_the_rules()
    {
        var build = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(build!.FullName, "Levee.slnx")))
        {
            build = build.Parent;
        }
        var levee = Path.Combine(build.FullName, "src", "Levee", "build");
        // Only the role check is under test here: these stand-ins have no Levee analyzers to run, so they build as check's own pass
        // does, with its token in the property and the environment.
        const string Token = "levee-cli-test";
        void Project(string name, string? role, params string[] references) =>
            Write(name, role, $"<LeveeCheck>{Token}</LeveeCheck>{(role == "Test" ? "<IsTestProject>true</IsTestProject>" : "")}", references);
        void Write(string name, string? role, string properties, params string[] references)
        {
            Directory.CreateDirectory(Path.Combine(_dir, name));
            var imports = role is null ? "" : $"""<Import Project="{levee}/Levee.props" /><PropertyGroup><LeveeRole>{role}</LeveeRole>{properties}</PropertyGroup>""";
            var items = string.Concat(references.Select(r => $"""<ProjectReference Include="../{r}/{r}.csproj" />"""));
            File.WriteAllText(Path.Combine(_dir, name, $"{name}.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  {imports}
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>{items}</ItemGroup>
                  {(role is null ? "" : $"""<Import Project="{levee}/Levee.targets" />""")}
                </Project>
                """);
        }
        Project("Plain", null);
        Project("Transport", "Infrastructure");
        Project("Vendors", "Adapter", "Plain");
        Project("Payments", "Adapter", "Transport");
        Project("Tests", "Test", "Plain");
        Write("Unanalyzed", "Adapter", "<RunAnalyzers>false</RunAnalyzers>");
        Write("Bare", "Adapter", "");
        Write("Forged", "Adapter", "<LeveeCheck>not-the-token</LeveeCheck><RunAnalyzers>false</RunAnalyzers>");
        Write("Pretend", "Test", "");
        Write("Labelled", "Test", "<IsTestProject>true</IsTestProject><OutputType>Exe</OutputType>");
        Write("SelfIssued", "Adapter", "<LeveeCheck>x</LeveeCheck><LEVEE_CHECK_TOKEN>x</LEVEE_CHECK_TOKEN><RunAnalyzers>false</RunAnalyzers>");
        Write("Reconfigured", "Adapter", "");
        // A global config is found from the project's sources, so the stand-in has one.
        File.WriteAllText(Path.Combine(_dir, "Reconfigured", "Code.cs"), "internal static class Code { }");
        File.WriteAllText(Path.Combine(_dir, "Reconfigured", ".globalconfig"), "is_global = true\nglobal_level = 1000\nbuild_property.LeveeRole = Test\n");

        static async Task<(int Code, string Output)> Build(string project)
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet", ["build", project, "-nologo", "-v:q", "-nodeReuse:false"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["LEVEE_CHECK_TOKEN"] = Token;
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            return (process.ExitCode, output);
        }

        var vendors = await Build(Path.Combine(_dir, "Vendors", "Vendors.csproj"));
        var payments = await Build(Path.Combine(_dir, "Payments", "Payments.csproj"));
        var tests = await Build(Path.Combine(_dir, "Tests", "Tests.csproj"));

        Assert.NotEqual(0, vendors.Code);
        Assert.Contains("error LEVEE001: This Adapter project references Plain, which doesn't use Levee", vendors.Output, StringComparison.Ordinal);
        Assert.Equal(0, payments.Code);
        Assert.Equal(0, tests.Code);
        Assert.Contains("error LEVEE006: This project turns its analyzers off", (await Build(Path.Combine(_dir, "Unanalyzed", "Unanalyzed.csproj"))).Output, StringComparison.Ordinal);
        Assert.Contains("error LEVEE009: This project uses Levee without its analyzers", (await Build(Path.Combine(_dir, "Bare", "Bare.csproj"))).Output, StringComparison.Ordinal);
        Assert.Contains("error LEVEE006: This project turns its analyzers off", (await Build(Path.Combine(_dir, "Forged", "Forged.csproj"))).Output, StringComparison.Ordinal);
        Assert.Contains("error LEVEE006: This project's role is Test", (await Build(Path.Combine(_dir, "Pretend", "Pretend.csproj"))).Output, StringComparison.Ordinal);
        Assert.Contains("error LEVEE006: This project's role is Test", (await Build(Path.Combine(_dir, "Labelled", "Labelled.csproj"))).Output, StringComparison.Ordinal);
        Assert.Contains("error LEVEE006: This project turns its analyzers off", (await Build(Path.Combine(_dir, "SelfIssued", "SelfIssued.csproj"))).Output, StringComparison.Ordinal);
        Assert.Contains("error LEVEE006: An analyzer config file sets what Levee's analyzers read", (await Build(Path.Combine(_dir, "Reconfigured", "Reconfigured.csproj"))).Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_prints_a_rule_and_lists_them_all()
    {
        var (code, output, _) = await Run(_dir, "explain", "levee004");
        var (_, all, _) = await Run(_dir, "explain");
        var (unknown, _, error) = await Run(_dir, "explain", "LEVEE999");

        Assert.Equal(0, code);
        Assert.StartsWith("## LEVEE004: Only the dispatcher saves and commits", output, StringComparison.Ordinal);
        Assert.Contains("IFlush", output, StringComparison.Ordinal);
        Assert.DoesNotContain("## LEVEE005", output, StringComparison.Ordinal);
        Assert.Equal(Levee.Cli.Explain.Rules().Count, all.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("LEVEE017: A Tool is a process", all, StringComparison.Ordinal);
        Assert.Equal(1, unknown);
        Assert.Contains("There's no rule LEVEE999", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_writes_a_project_per_role_on_this_version_of_Levee()
    {
        var (code, _, error) = await Run(_dir, "new", "Acme.Billing");

        Assert.Equal((0, ""), (code, error));
        foreach (var (project, role) in new[]
        {
            ("src/Acme.Billing.Contracts/Acme.Billing.Contracts.csproj", "Contracts"),
            ("src/Acme.Billing.Domain/Acme.Billing.Domain.csproj", "Domain"),
            ("src/Acme.Billing.Application/Acme.Billing.Application.csproj", "Application"),
            ("src/Acme.Billing.Adapters/Acme.Billing.Adapters.csproj", "Adapter"),
            ("src/Acme.Billing.Host/Acme.Billing.Host.csproj", "Host"),
            ("tests/Acme.Billing.Tests/Acme.Billing.Tests.csproj", "Test"),
        })
        {
            Assert.Contains($"<LeveeRole>{role}</LeveeRole>", Read("Acme.Billing", project), StringComparison.Ordinal);
        }
        Assert.Contains($"""<PackageVersion Include="Levee" Version="{Levee.Cli.Cli.Version}" />""", Read("Acme.Billing", "Directory.Packages.props"), StringComparison.Ordinal);
        Assert.Contains($"\"version\": \"{Levee.Cli.Cli.Version}\"", Read("Acme.Billing", ".config", "dotnet-tools.json"), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "Acme.Billing", "nuget.config")));
    }

    [Fact]
    public async Task New_refuses_a_bad_name_and_a_directory_in_use()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Taken"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "Taken", "keep.txt"), "mine", TestContext.Current.CancellationToken);

        Assert.Equal(1, (await Run(_dir, "new", "acme")).Code);
        Assert.Equal(1, (await Run(_dir, "new", "Taken")).Code);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "Taken")));
    }

    [Fact]
    public async Task Generate_writes_the_contract_handler_and_test_as_declared()
    {
        await Run(_dir, "new", "Shop");
        var app = Path.Combine(_dir, "Shop", "src");

        var (code, _, error) = await Run(app, "generate", "operation", "order_lines.add", "AddOrderLine", "--access", "member", "--summary", "Adds a \"line\".",
            "--scoped-by", "Tenant", "--writes", "--idempotency", "conflicts", "--result", "OrderLineView", "--errors", "order_lines.closed, order_lines.too_many");

        Assert.Equal((0, ""), (code, error));
        var contract = Read("Shop", "src", "Shop.Contracts", "OrderLines", "AddOrderLine.cs");
        Assert.Contains("namespace Shop.Contracts.OrderLines;", contract, StringComparison.Ordinal);
        Assert.Contains("public sealed record OrderLineView();", contract, StringComparison.Ordinal);
        Assert.Contains("""[Operation("order_lines.add", "member", Summary = "Adds a \"line\".", Effects = Effects.Write, Idempotency = Idempotency.Conflicts, Errors = ["order_lines.closed", "order_lines.too_many"])]""", contract, StringComparison.Ordinal);
        Assert.Contains("public sealed record AddOrderLine(string Tenant) : IOperation<OrderLineView>, IScopedOperation", contract, StringComparison.Ordinal);
        Assert.Contains("string IScopedOperation.ScopeKey => Tenant;", contract, StringComparison.Ordinal);
        Assert.Contains("internal sealed class AddOrderLineHandler : IOperationHandler<AddOrderLine, OrderLineView>", Read("Shop", "src", "Shop.Application", "OrderLines", "AddOrderLineHandler.cs"), StringComparison.Ordinal);
        Assert.Contains("""Assert.Equal(("member", Effects.Write, false)""", Read("Shop", "tests", "Shop.Tests", "OrderLines", "AddOrderLineTests.cs"), StringComparison.Ordinal);

        // A second operation answering with the same view reuses it.
        var (again, _, _) = await Run(app, "generate", "operation", "order_lines.get", "GetOrderLine", "--access", "member", "--summary", "One line.",
            "--scoped-by", "Tenant", "--result", "OrderLineView");
        Assert.Equal(0, again);
        Assert.DoesNotContain("record OrderLineView", Read("Shop", "src", "Shop.Contracts", "OrderLines", "GetOrderLine.cs"), StringComparison.Ordinal);
        // From another area, it's named by its own.
        await Run(app, "generate", "operation", "orders.last_line", "LastLine", "--access", "member", "--summary", "The last line.",
            "--scoped-by", "Tenant", "--result", "OrderLineView");
        Assert.Contains("using Shop.Contracts.OrderLines;", Read("Shop", "src", "Shop.Contracts", "Orders", "LastLine.cs"), StringComparison.Ordinal);
        Assert.Contains("using Shop.Contracts.OrderLines;", Read("Shop", "src", "Shop.Application", "Orders", "LastLineHandler.cs"), StringComparison.Ordinal);
        await Run(app, "generate", "operation", "orders.notes", "OrderNotes", "--access", "member", "--summary", "Two\nlines.", "--scoped-by", "Tenant");
        Assert.Contains("Summary = \"Two\\nlines.\"", Read("Shop", "src", "Shop.Contracts", "Orders", "OrderNotes.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_a_platform_read()
    {
        await Run(_dir, "new", "Shop");

        var (code, _, _) = await Run(Path.Combine(_dir, "Shop"), "generate", "operation", "tenants.list", "ListTenants", "--access", "admin", "--summary", "Every tenant.", "--platform");

        Assert.Equal(0, code);
        var contract = Read("Shop", "src", "Shop.Contracts", "Tenants", "ListTenants.cs");
        Assert.Contains("""[Operation("tenants.list", "admin", Summary = "Every tenant.", Platform = true)]""", contract, StringComparison.Ordinal);
        Assert.Contains("public sealed record ListTenants() : IOperation<Unit>;", contract, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--summary", "s", "--writes", "--idempotency", "none")]
    [InlineData("--summary", "s", "--platform", "--scoped-by", "Tenant")]
    [InlineData("--summary", "s", "--platform", "--writes")]
    [InlineData("--summary", "s", "--platform", "--idempotency", "none")]
    [InlineData("--summary", "s", "--platform", "--writes", "--idempotency", "maybe")]
    [InlineData("--platform")]
    [InlineData("--summary", "s", "--platform", "--errors", "Notes Closed")]
    public async Task Generate_asks_for_what_the_build_would_refuse(params string[] options)
    {
        await Run(_dir, "new", "Shop");

        var (code, _, error) = await Run(Path.Combine(_dir, "Shop"), ["generate", "operation", "notes.create", "CreateNote", "--access", "member", .. options]);

        Assert.Equal(1, code);
        Assert.NotEmpty(error);
        Assert.False(Directory.Exists(Path.Combine(_dir, "Shop", "src", "Shop.Contracts", "Notes")));
    }

    [Fact]
    public async Task Generate_never_overwrites()
    {
        await Run(_dir, "new", "Shop");
        string[] generate = ["generate", "operation", "notes.list", "ListNotes", "--access", "member", "--summary", "s", "--platform"];
        await Run(Path.Combine(_dir, "Shop"), generate);
        var handler = Path.Combine(_dir, "Shop", "src", "Shop.Application", "Notes", "ListNotesHandler.cs");
        await File.WriteAllTextAsync(handler, "// written", TestContext.Current.CancellationToken);

        var (code, _, error) = await Run(Path.Combine(_dir, "Shop"), generate);

        Assert.Equal(1, code);
        Assert.Contains("already exists", error, StringComparison.Ordinal);
        Assert.Equal("// written", await File.ReadAllTextAsync(handler, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Generate_needs_an_app()
    {
        Assert.Equal(1, (await Run(_dir, "generate", "operation", "notes.list", "ListNotes", "--access", "a", "--summary", "s", "--platform")).Code);
    }

    [Fact]
    public async Task Catalog_reads_every_operation_a_build_can_see()
    {
        var (code, output, _) = await Run(_dir, "catalog", AppContext.BaseDirectory);

        Assert.Equal(0, code);
        var create = output.Split('\n').Single(l => l.StartsWith("notes.create ", StringComparison.Ordinal));
        Assert.Matches(@"^notes\.create\s+member\s+write\s+conflicts\s+scoped\s+yes\s+Writes a note\.$", create.TrimEnd());
    }

    [Fact]
    public async Task Catalog_as_json()
    {
        var (_, output, _) = await Run(_dir, "catalog", AppContext.BaseDirectory, "--json");

        Assert.Contains("""{"name":"notes.list","access":"member","effects":"read","idempotency":"safe","platform":false,"internal":false,"streams":false,"handled":true,""", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Db_prints_what_the_owner_runs()
    {
        var schema = (await Run(_dir, "db", "schema")).Output;
        var withJobs = (await Run(_dir, "db", "schema", "--jobs")).Output;
        var isolate = (await Run(_dir, "db", "isolate", "notes", "tenant_id", "--type", "uuid")).Output;
        var grants = (await Run(_dir, "db", "grants", "app", "--jobs")).Output;

        Assert.Contains("create table public.levee_audit", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("oxbow_job", schema, StringComparison.Ordinal);
        Assert.Contains("oxbow_job", withJobs, StringComparison.Ordinal);
        Assert.Contains("tenant_id = nullif(public.levee_scope(), '')::uuid", isolate, StringComparison.Ordinal);
        Assert.Contains("create table elsewhere.levee_audit", (await Run(_dir, "db", "schema", "--schema", "elsewhere")).Output, StringComparison.Ordinal);
        Assert.Contains("grant usage on schema elsewhere to app;", (await Run(_dir, "db", "grants", "app", "--schema", "elsewhere")).Output, StringComparison.Ordinal);
        Assert.Contains("revoke insert, update, delete on public.levee_audit from app;", grants, StringComparison.Ordinal);
        Assert.Contains("grant execute on function public.levee_audit_write", grants, StringComparison.Ordinal);
        Assert.Contains("revoke insert, update, delete on public.oxbow_migration from app;", grants, StringComparison.Ordinal);
        // Oxbow's tables stay in public whatever the app's schema is.
        Assert.Contains("on public.oxbow_migration from app;", (await Run(_dir, "db", "grants", "app", "--jobs", "--schema", "elsewhere")).Output, StringComparison.Ordinal);
        Assert.Contains("create policy levee_shared_read on public.plans for select using (true);", (await Run(_dir, "db", "share", "plans")).Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("db", "isolate", "notes; drop table x", "tenant")]
    [InlineData("db", "grants", "App")]
    [InlineData("db", "nope")]
    [InlineData("nope")]
    public async Task Bad_input_is_refused(params string[] args)
    {
        Assert.Equal(1, (await Run(_dir, args)).Code);
    }
}

public sealed class CheckTests
{
    [Fact]
    public void Findings_are_read_from_build_output_once_each()
    {
        const string Output = """
            /app/src/App.Domain/Note.cs(12,19): error LEVEE007: Text can be changed from outside aggregate root Note [/app/src/App.Domain/App.Domain.csproj]
            /app/src/App.Domain/Note.cs(12,19): error LEVEE007: Text can be changed from outside aggregate root Note (https://rkingon.github.io/Levee/rules/levee007/) [/app/src/App.Domain/App.Domain.csproj]
            CSC : error LEVEE002: A Domain project may not reference App.Adapters (Adapter) [/app/src/App.Domain/App.Domain.csproj]
            /app/src/App.Domain/Note.cs(3,1): error CS0246: The type or namespace name 'X' could not be found [/app/src/App.Domain/App.Domain.csproj]
            """;

        var findings = Levee.Cli.Check.Parse(Output).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal(new Levee.Cli.Check.Finding("LEVEE007", "App.Domain", "/app/src/App.Domain/Note.cs", 12, "Text can be changed from outside aggregate root Note"), findings[0]);
        Assert.Equal(new Levee.Cli.Check.Finding("LEVEE002", "App.Domain", null, null, "A Domain project may not reference App.Adapters (Adapter)"), findings[1]);
    }

    [Fact]
    public void Projects_that_dont_compile_are_named_once_each_beside_Levees_findings()
    {
        const string Output = """
            /app/src/App.Host/Program.cs(3,1): error CS0246: The type or namespace name 'X' could not be found [/app/src/App.Host/App.Host.csproj]
            /app/src/App.Host/Program.cs(4,1): error CS0103: The name 'y' does not exist in the current context [/app/src/App.Host/App.Host.csproj]
            /app/src/App.Domain/Note.cs(12,19): error LEVEE007: Text can be changed from outside aggregate root Note [/app/src/App.Domain/App.Domain.csproj]
            """;

        Assert.Equal(["App.Host"], Levee.Cli.Check.Broken(Output));
    }

    [Fact]
    public void What_the_code_suppressed_is_read_from_the_error_log_with_its_reason()
    {
        var dir = Directory.CreateTempSubdirectory("levee-sarif-").FullName;
        try
        {
            var source = Path.Combine(dir, "Clock.cs");
            File.WriteAllLines(source, [
                "internal static class Clock",
                "{",
                "#pragma warning disable LEVEE003 // the vendor's SDK reads the wall clock",
                "    public static System.DateTimeOffset A() => System.DateTimeOffset.UtcNow;",
                "}",
            ]);
            var uri = new Uri(source).AbsoluteUri;
            var log = Path.Combine(dir, "App.Application~net10.0.sarif");
            File.WriteAllText(log, """
                {"version":"2.1.0","runs":[{"results":[
                  {"ruleId":"LEVEE003","message":{"text":"DateTimeOffset.UtcNow reads the wall clock"},"suppressions":[{"kind":"inSource"}],
                   "locations":[{"physicalLocation":{"artifactLocation":{"uri":"URI"},"region":{"startLine":4}}}]},
                  {"ruleId":"LEVEE018","message":{"text":"Opens its own connection"},"suppressions":[{"kind":"inSource","justification":"LISTEN outlives a transaction"}],
                   "locations":[{"physicalLocation":{"artifactLocation":{"uri":"URI"},"region":{"startLine":2}}}]},
                  {"ruleId":"LEVEE005","message":{"text":"Locates services"},
                   "locations":[{"physicalLocation":{"artifactLocation":{"uri":"URI"},"region":{"startLine":2}}}]},
                  {"ruleId":"CA1822","message":{"text":"Mark as static"},"suppressions":[{"kind":"inSource"}]}
                ]}]}
                """.Replace("URI", uri, StringComparison.Ordinal));

            var suppressed = Levee.Cli.Check.Suppressed(log, "App.Application").ToList();

            Assert.Equal([
                new Levee.Cli.Check.Finding("LEVEE003", "App.Application", source, 4, "DateTimeOffset.UtcNow reads the wall clock", "the vendor's SDK reads the wall clock"),
                new Levee.Cli.Check.Finding("LEVEE018", "App.Application", source, 2, "Opens its own connection", "LISTEN outlives a transaction"),
            ], suppressed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
