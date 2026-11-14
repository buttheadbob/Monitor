using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Api;

namespace SenX.Monitor.Services;

public static class MonitorUiServiceCollectionExtensions
{
    /// <summary>
    /// One registration shared by the Quasar adapter and the preview host. Each brings its own
    /// <see cref="IMonitorServers"/>, and history is stored under <paramref name="dataDirectory"/>.
    /// <paramref name="assetBase"/> is where this plugin's browser files are served from.
    /// </summary>
    public static IServiceCollection AddMonitorUi(this IServiceCollection services, string dataDirectory, string assetBase)
    {
        services.AddSingleton(new MonitorAssets(assetBase));
        services.AddSingleton<IHistoryReads>(provider => provider.GetRequiredService<HistoryStore>());
        services.AddSingleton<ChartBuilder>();
        services.AddSingleton<BoardBuilder>();
        services.AddSingleton<BoardBuilder>();
        services.AddSingleton<HistoryWindows>();
        services.AddSingleton(new MonitorSettings(dataDirectory));
        services.AddSingleton(provider => new HistoryStore(dataDirectory, provider.GetRequiredService<MonitorSettings>(), provider.GetRequiredService<ILogger<HistoryStore>>()));
        services.AddSingleton<MonitorBaselines>();
        services.AddSingleton(provider => new MonitorIncidents(
            provider.GetRequiredService<IMonitorServers>(),
            dataDirectory,
            provider.GetRequiredService<MonitorSettings>(),
            provider.GetRequiredService<MonitorBaselines>(),
            provider.GetRequiredService<ILogger<MonitorIncidents>>()));
        services.AddSingleton<MonitorCollector>();
        services.AddSingleton(provider => new MonitorRuns(
            provider.GetRequiredService<IMonitorServers>(),
            dataDirectory,
            provider.GetRequiredService<MonitorSettings>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            provider.GetRequiredService<ILogger<MonitorRuns>>()));
        services.AddHostedService(provider => provider.GetRequiredService<MonitorCollector>());
        services.AddSingleton(provider => new MonitorWebhooks(
            provider.GetRequiredService<MonitorIncidents>(),
            provider.GetRequiredService<MonitorSettings>(),
            provider.GetRequiredService<HistoryStore>(),
            new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
            provider.GetRequiredService<ILogger<MonitorWebhooks>>()));
        services.AddHostedService(provider => provider.GetRequiredService<MonitorWebhooks>());
        services.TryAddSingleton<IMonitorApi, MonitorApi>();
        return services;
    }
}
