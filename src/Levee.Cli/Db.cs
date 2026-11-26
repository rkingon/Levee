using System.Text.RegularExpressions;
using Levee.EntityFrameworkCore;
using Oxbow;

namespace Levee.Cli;

/// <summary><c>dotnet levee db</c>: the SQL the schema's owner runs. Printed, never applied: migrations are the app's.</summary>
internal static partial class Db
{
    public static void Run(Args args, TextWriter output)
    {
        var schema = Identifier(args.Option("schema") ?? "public", "schema");
        switch (args.Positional(0, "schema, isolate or grants"))
        {
            case "schema":
                output.WriteLine("-- Levee: scope functions and the audit");
                output.WriteLine(LeveeSchema.Create(schema));
                if (args.Flag("jobs"))
                {
                    foreach (var migration in OxbowSchema.GetMigrations(0))
                    {
                        output.WriteLine($"-- Oxbow schema version {migration.Version}");
                        output.WriteLine(migration.Sql);
                    }
                }
                return;
            case "isolate":
                output.WriteLine(LeveeSchema.Isolation(
                    $"{schema}.{Identifier(args.Positional(1, "the table"), "table")}",
                    Identifier(args.Positional(2, "the scope column"), "column"),
                    Identifier(args.Option("type") ?? "text", "type"),
                    schema));
                return;
            case "grants":
                var role = Identifier(args.Positional(1, "the app's role"), "role");
                output.WriteLine(LeveeSchema.Grants(role, schema));
                if (args.Flag("jobs"))
                {
                    output.WriteLine(OxbowSchema.GetGrants(role));
                    output.WriteLine($"revoke insert, update, delete on oxbow_migration from {role};");
                }
                return;
            case var other:
                throw new CliException($"There's no db command {other}: schema, isolate or grants.");
        }
    }

    private static string Identifier(string value, string what) =>
        SqlName().IsMatch(value) ? value : throw new CliException($"'{value}' isn't a {what} name: lowercase letters, digits and underscores.");

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SqlName();
}
