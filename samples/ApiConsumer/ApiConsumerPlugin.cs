using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Security;

namespace ApiConsumer;

/// <summary>
/// A small plugin that uses Monitor's public API, to prove it through a real Quasar host, with Monitor
/// both installed and not. It's also the example docs/PublicApi.md points to.
/// </summary>
public sealed class ApiConsumerPlugin : IQuasarPlugin
{
    public string Id => "senx.monitor.sample";

    public string DisplayName => "Monitor API sample";

    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context)
    {
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context)
    {
    }

    public IEnumerable<Assembly> GetRazorAssemblies()
    {
        yield return typeof(ApiConsumerPlugin).Assembly;
    }

    public IEnumerable<QuasarNavItem> GetNavItems()
    {
        yield return new QuasarNavItem("Monitor API sample", "/monitor-api-sample", string.Empty, QuasarNavZones.Tools, 900, QuasarPolicyNames.CanView);
    }

    public IEnumerable<QuasarExtensionContribution> GetExtensions() => [];
}
