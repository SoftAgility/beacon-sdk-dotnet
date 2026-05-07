using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SoftAgility.Beacon;

/// <summary>
/// Extension methods for registering Beacon in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="BeaconTracker"/> as a singleton in the DI container,
    /// accessible via <see cref="IBeaconTracker"/>. Configuration is applied via
    /// the <paramref name="configure"/> action.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure <see cref="BeaconOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddBeacon(
        this IServiceCollection services,
        Action<BeaconOptions> configure)
    {
        services.Configure(configure);

        services.TryAddSingleton<BeaconTracker>();
        services.TryAddSingleton<IBeaconTracker>(sp => sp.GetRequiredService<BeaconTracker>());

        return services;
    }
}
