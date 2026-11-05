using Microsoft.Extensions.DependencyInjection;

namespace TheKrystalShip.KGSM.Watchdog.Client;

public static class WatchdogClientServiceCollectionExtensions
{
    /// <summary>Registers an <see cref="IWatchdogClient"/> on the control socket at <paramref name="socketPath"/>.</summary>
    public static IServiceCollection AddKgsmWatchdogClient(this IServiceCollection services, string socketPath) =>
        services.AddKgsmWatchdogClient(options => options.SocketPath = socketPath);

    /// <summary>Registers an <see cref="IWatchdogClient"/> configured by <paramref name="configure"/>.</summary>
    public static IServiceCollection AddKgsmWatchdogClient(this IServiceCollection services, Action<WatchdogClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WatchdogClientOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.SocketPath))
            throw new ArgumentException("The watchdog socket path cannot be empty.", nameof(configure));

        services.AddSingleton(options);
        services.AddSingleton<IWatchdogClient>(sp => new WatchdogClient(sp.GetRequiredService<WatchdogClientOptions>()));
        return services;
    }
}
