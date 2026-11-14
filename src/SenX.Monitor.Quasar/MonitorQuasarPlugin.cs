using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Security;
using SenX.Monitor.Components.Entities;
using SenX.Monitor.Contracts;
using SenX.Monitor.Pages;
using SenX.Monitor.Services;

namespace SenX.Monitor.Quasar;

public sealed class MonitorQuasarPlugin : IQuasarPlugin
{
    private const string NavIcon =
        "<path d=\"M0 0h24v24H0z\" fill=\"none\"/><path d=\"M3.5 18.49l6-6.01 4 4L22 6.92l-1.41-1.41-7.09 7.97-4-4L2 16.99z\"/>";

    private const int ExtensionOrder = 700;

    public string Id => MonitorProtocol.PluginId;

    public string DisplayName => "Monitor";

    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context)
    {
        services.AddSingleton<IMonitorServers, QuasarMonitorServers>();
        services.AddMonitorUi(Path.Combine(context.InstallDirectory, "monitor"), "/_quasar/plugins/" + MonitorProtocol.PluginId + "/");
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context) =>
        endpoints.MapMonitorApi(QuasarPolicyNames.CanView);

    public IEnumerable<Assembly> GetRazorAssemblies()
    {
        yield return typeof(MonitorPage).Assembly;
    }

    public IEnumerable<QuasarNavItem> GetNavItems()
    {
        yield return new QuasarNavItem("Monitor", "/monitor", NavIcon, QuasarNavZones.Main, 700, QuasarPolicyNames.CanView);
    }

    public IEnumerable<QuasarExtensionContribution> GetExtensions()
    {
        yield return new QuasarExtensionContribution(QuasarExtensionTargets.EntityViewerColumnHeader, typeof(EntityCostHeader), QuasarPatchMode.After, ExtensionOrder, Id, QuasarPolicyNames.CanView);
        yield return new QuasarExtensionContribution(QuasarExtensionTargets.EntityViewerColumnCell, typeof(EntityCostCell), QuasarPatchMode.After, ExtensionOrder, Id, QuasarPolicyNames.CanView);
        yield return new QuasarExtensionContribution(QuasarExtensionTargets.EntityActions, typeof(EntityProfileAction), QuasarPatchMode.Before, ExtensionOrder, Id, QuasarPolicyNames.CanControlServers);
    }
}
