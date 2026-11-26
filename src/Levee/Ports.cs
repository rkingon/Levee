using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Levee;

/// <summary>
/// What the application needs from outside it: data, a vendor, a store. Declared in Application, where handlers depend on
/// it and nothing else does I/O; implemented in an Adapter project by an <see cref="AdapterAttribute"/> class, bound by the
/// Host's generated composition. A repository (<see cref="IRepository{TAggregate}"/>) is a port without the attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class PortAttribute : Attribute
{
    /// <summary>
    /// The setting whose value names the adapter that serves this port (<c>Providers:HomePrices</c> = <c>s3</c>), so
    /// which source answers is configuration, never code. Without it, the port has exactly one adapter.
    /// </summary>
    public string? SelectedBy { get; set; }

    /// <summary>
    /// A set, not a choice: every adapter serves it, and the application takes them all (<c>IEnumerable&lt;TPort&gt;</c>):
    /// integrations, channels, modules. Whatever routes among them is the application's.
    /// </summary>
    public bool Many { get; set; }
}

public enum AdapterLifetime
{
    /// <summary>One per operation, sharing its unit of work. The default.</summary>
    Scoped,

    /// <summary>One per process: a connection held open, a listener, a key.</summary>
    Singleton,

    Transient,
}

/// <summary>
/// Names an adapter of a port that is <see cref="PortAttribute.SelectedBy"/> a setting, or sets its lifetime. An unnamed,
/// scoped adapter needs no attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AdapterAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;

    public AdapterLifetime Lifetime { get; set; } = AdapterLifetime.Scoped;
}

/// <summary>
/// The named adapters of a set (<see cref="PortAttribute.Many"/>), each made only when it's asked for: what a router over
/// them takes (a launcher per workbench, by configuration), instead of locating services.
/// </summary>
public interface IAdapters<TPort>
    where TPort : class
{
    IReadOnlyCollection<string> Names { get; }

    /// <summary>The adapter named <paramref name="name"/>; throws naming the others when there's none.</summary>
    TPort Get(string name);
}

/// <summary>Registered by the generated composition for every set with named adapters.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class Adapters<TPort>(IServiceProvider services, IReadOnlyCollection<string> names) : IAdapters<TPort>
    where TPort : class
{
    public IReadOnlyCollection<string> Names => names;

    public TPort Get(string name) =>
        names.Contains(name)
            ? services.GetRequiredKeyedService<TPort>(name)
            : throw new KeyNotFoundException($"No {typeof(TPort).Name} adapter is named '{name}': {string.Join(", ", names)}.");
}

/// <summary>Called by the generated composition, at startup, for every port bound by a setting.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class PortSelection
{
    /// <returns>The configured adapter's name, one of <paramref name="adapters"/>.</returns>
    public static string Select(IConfiguration? configuration, string setting, string port, IReadOnlyCollection<string> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        if (configuration is null)
        {
            throw new InvalidOperationException($"{port} is chosen by the setting {setting}: pass the configuration to AddLeveeOperations.");
        }
        var value = configuration[setting]?.Trim();
        return value is not null && adapters.Contains(value)
            ? value
            : throw new InvalidOperationException(
                $"{setting} chooses the adapter for {port}: one of {string.Join(", ", adapters)}, not {(string.IsNullOrEmpty(value) ? "nothing" : $"'{value}'")}.");
    }

    /// <summary>
    /// Whether <paramref name="adapter"/> is the one <paramref name="setting"/> chooses, or may be: with no configuration or no
    /// setting, every adapter is registered and <see cref="Select"/> reports the missing setting at startup.
    /// </summary>
    public static bool Wants(IConfiguration? configuration, string setting, string adapter) =>
        configuration?[setting]?.Trim() is not { Length: > 0 } chosen || string.Equals(chosen, adapter, StringComparison.Ordinal);

    /// <summary>Binds <typeparamref name="TPort"/> to the adapter registered under <paramref name="selected"/>, for as long as that adapter lives.</summary>
    public static void Bind<TPort>(IServiceCollection services, string selected, AdapterLifetime lifetime)
        where TPort : class
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Add(new ServiceDescriptor(typeof(TPort), provider => provider.GetRequiredKeyedService<TPort>(selected), lifetime switch
        {
            AdapterLifetime.Singleton => ServiceLifetime.Singleton,
            AdapterLifetime.Transient => ServiceLifetime.Transient,
            _ => ServiceLifetime.Scoped,
        }));
    }
}
