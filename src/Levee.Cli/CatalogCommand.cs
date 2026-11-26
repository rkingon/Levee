using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Levee.Cli;

/// <summary>
/// <c>dotnet levee catalog</c>: the operations a Host serves, read from its build's metadata. Nothing of the app runs: the
/// same attributes the generator reads, from every roled assembly beside the Host's.
/// </summary>
internal static class CatalogCommand
{
    public static async Task RunAsync(Args args, string workingDirectory, TextWriter output)
    {
        var target = args.Count > 0 ? Path.GetFullPath(args.Positional(0, "the host"), workingDirectory) : null;
        var directory = target switch
        {
            null => Path.GetDirectoryName(await BuildAsync(Files.OneProjectOf(Files.AppRoot(workingDirectory), "Host"), args.Flag("no-build")))!,
            _ when Directory.Exists(target) => target,
            _ when target.EndsWith(".csproj", StringComparison.Ordinal) => Path.GetDirectoryName(await BuildAsync(target, args.Flag("no-build")))!,
            _ when File.Exists(target) => Path.GetDirectoryName(target)!,
            _ => throw new CliException($"{target} isn't a project, an assembly or a directory."),
        };

        var operations = Read(directory);
        if (args.Flag("json"))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(operations, LeveeJson.Options));
            return;
        }
        if (operations.Count == 0)
        {
            await output.WriteLineAsync($"No operations in {directory}.");
            return;
        }
        string[] header = ["NAME", "ACCESS", "EFFECTS", "IDEMPOTENCY", "SCOPE", "HANDLED", "SUMMARY"];
        var rows = operations.Select(o => new[]
        {
            o.Name, o.Access, o.Effects, o.Idempotency, (o.Platform ? "platform" : "scoped") + (o.Internal ? ", internal" : "") + (o.Streams ? ", streams" : ""),
            o.Handled ? "yes" : "NO", o.Summary,
        }).ToList();
        var widths = header.Select((h, i) => rows.Select(r => r[i].Length).Append(h.Length).Max()).ToArray();
        foreach (var row in rows.Prepend(header))
        {
            await output.WriteLineAsync(string.Join("  ", row.Select((cell, i) => i == row.Length - 1 ? cell : cell.PadRight(widths[i]))).TrimEnd());
        }
    }

    internal sealed record OperationView(string Name, string Access, string Effects, string Idempotency, bool Platform, bool Internal, bool Streams, bool Handled,
        string Summary, string Type, string Result);

    internal static IReadOnlyList<OperationView> Read(string directory)
    {
        var assemblies = Directory.GetFiles(directory, "*.dll");
        var runtime = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll");
        using var context = new MetadataLoadContext(new PathAssemblyResolver(runtime.Concat(assemblies)));

        var roled = assemblies.Select(path => Load(context, path)).OfType<Assembly>()
            .Where(a => a.GetCustomAttributesData().Any(d => d.AttributeType.FullName == "Levee.LeveeRoleAttribute"))
            .ToList();
        var handled = roled.SelectMany(a => a.GetCustomAttributesData())
            .Where(d => d.AttributeType.FullName == "Levee.LeveeHandlesAttribute")
            .Select(d => ((Type)d.ConstructorArguments[0].Value!).FullName)
            .ToHashSet(StringComparer.Ordinal);

        return [.. roled.SelectMany(Types)
            .Select(t => (Type: t, Attribute: t.GetCustomAttributesData().FirstOrDefault(d => d.AttributeType.FullName == "Levee.OperationAttribute")))
            .Where(x => x.Attribute is not null)
            .Select(x => View(x.Type, x.Attribute!, handled.Contains(x.Type.FullName!)))
            .OrderBy(o => o.Name, StringComparer.Ordinal)];
    }

    private static OperationView View(Type type, CustomAttributeData attribute, bool handled)
    {
        var named = attribute.NamedArguments.ToDictionary(a => a.MemberName, a => a.TypedValue.Value);
        var effects = named.TryGetValue("Effects", out var e) ? Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture) : 1;
        var idempotency = named.TryGetValue("Idempotency", out var i) ? Convert.ToInt32(i, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var contract = type.GetInterfaces().FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition().FullName is "Levee.IOperation`1" or "Levee.ISubscription`1");
        var streams = contract?.GetGenericTypeDefinition().FullName == "Levee.ISubscription`1";
        var result = contract?.GetGenericArguments()[0].Name ?? "?";
        return new OperationView(
            (string)attribute.ConstructorArguments[0].Value!,
            (string)attribute.ConstructorArguments[1].Value!,
            string.Join("|", new[] { (1, "read"), (2, "write"), (4, "external") }.Where(f => (effects & f.Item1) != 0).Select(f => f.Item2)),
            ((Idempotency)idempotency).ToString().ToLowerInvariant(),
            named.TryGetValue("Platform", out var p) && p is true,
            named.TryGetValue("Internal", out var internalValue) && internalValue is true,
            streams,
            handled,
            named.TryGetValue("Summary", out var s) && s is string summary ? summary : "",
            type.FullName!,
            result);
    }

    private static Assembly? Load(MetadataLoadContext context, string path)
    {
        try
        {
            return context.LoadFromAssemblyPath(path);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }

    /// <summary>Builds the project (unless told not to) and returns its assembly's path.</summary>
    private static async Task<string> BuildAsync(string project, bool noBuild)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        // -getProperty alone only evaluates; with a target it reports the property after running it.
        foreach (var argument in noBuild ? ["msbuild", project, "-getProperty:TargetPath"] : new[] { "build", project, "-t:Build", "-getProperty:TargetPath" })
        {
            start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add("-nologo");
        using var process = Process.Start(start) ?? throw new CliException("Couldn't start dotnet.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var path = stdout.Trim().Split('\n').LastOrDefault()?.Trim();
        if (process.ExitCode != 0 || path is null || !File.Exists(path))
        {
            throw new CliException($"Building {Path.GetFileName(project)} failed:{Environment.NewLine}{stdout}{stderr}".TrimEnd());
        }
        return path;
    }
}
