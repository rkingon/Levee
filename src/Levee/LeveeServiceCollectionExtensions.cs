using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Levee;

public static class LeveeServiceCollectionExtensions
{
    /// <summary>
    /// The pipeline over <paramref name="catalog"/>. Called by the generated <c>AddLeveeOperations</c>, which also registers
    /// every handler; the app adds its <see cref="IAccessPolicy"/> and a unit of work (<c>AddLeveePostgres</c>).
    /// </summary>
    public static IServiceCollection AddLevee(this IServiceCollection services, OperationCatalog catalog, Action<LeveeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);
        var options = new LeveeOptions();
        configure?.Invoke(options);
        services.AddSingleton(new OperationCatalogPart(catalog.All, catalog.Refusals));
        services.TryAddSingleton(provider =>
        {
            var parts = provider.GetServices<OperationCatalogPart>().ToList();
            return new OperationCatalog(parts.SelectMany(p => p.Operations), parts.SelectMany(p => p.Refusals));
        });
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Dispatcher>();
        services.AddScoped<CurrentOperation>();
        return services;
    }

    /// <summary>
    /// More operations in the catalog: a test's own, on top of the app's composition. The catalog is the sum of every part;
    /// a name in two parts fails when it's first resolved.
    /// </summary>
    public static IServiceCollection AddLeveePart(this IServiceCollection services, IEnumerable<OperationDescriptor> operations)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(operations);
        return services.AddSingleton(new OperationCatalogPart([.. operations]));
    }
}
