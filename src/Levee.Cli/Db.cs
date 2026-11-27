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
        switch (args.Positional(0, "schema, isolate, share or grants"))
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
                    Table(args.Positional(1, "the table"), schema),
                    Identifier(args.Positional(2, "the scope column"), "column"),
                    Identifier(args.Option("type") ?? "text", "type"),
                    schema));
                return;
            case "share":
                output.WriteLine(LeveeSchema.Shared(Table(args.Positional(1, "the table"), schema), schema));
                return;
            case "grants":
                var role = Identifier(args.Positional(1, "the app's role"), "role");
                // One statement, so Levee's and Oxbow's grants hold together or not at all, standalone or in a caller's transaction.
                // Oxbow's tables are in public whatever the app's schema is.
                output.WriteLine(LeveeSchema.Grants(role, schema, args.Flag("jobs")
                    ? OxbowSchema.GetGrants(role) + $"\nrevoke insert, update, delete on public.oxbow_migration from {role};"
                    : null));
                return;
            case var other:
                throw new CliException($"There's no db command {other}: schema, isolate, share or grants.");
        }
    }

    /// <summary>The table qualified: by its own schema when it names one (billing.invoices), else by Levee's.</summary>
    private static string Table(string value, string schema) => value.Split('.') switch
    {
        [var table] => $"{schema}.{Identifier(table, "table")}",
        [var own, var table] => $"{Identifier(own, "schema")}.{Identifier(table, "table")}",
        _ => throw new CliException($"'{value}' isn't a table name: table, or schema.table."),
    };

    private static string Identifier(string value, string what) =>
        SqlName().IsMatch(value) ? value : throw new CliException($"'{value}' isn't a {what} name: lowercase letters, digits and underscores.");

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SqlName();
}
