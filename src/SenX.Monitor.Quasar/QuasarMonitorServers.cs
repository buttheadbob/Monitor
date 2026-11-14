using Quasar.Plugin.Abstractions.Companion;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Quasar;

/// <summary>Reaches real servers through Quasar's companion channel.</summary>
internal sealed class QuasarMonitorServers(IQuasarCompanionChannel channel) : IMonitorServers
{
    // While a replaced node and its replacement are both connected, one of them is followed until the old one goes.
    public IReadOnlyList<MonitorServer> Connected() =>
        channel.GetConnectedServers().Select(ToMonitorServer).DistinctBy(server => server.HistoryId).ToList();

    // A cluster slot stays the same when its node is replaced, and a cluster server id doesn't.
    private static MonitorServer ToMonitorServer(QuasarCompanionServer server) => new(
        server.ServerId,
        server.DisplayName,
        server.ClusterId.Length > 0 && server.ClusterSlot.Length > 0 ? server.ClusterId + "/" + server.ClusterSlot : server.ServerId,
        server.ClusterDisplayName)
    {
        AgentId = server.AgentId,
    };

    public Task<RuntimeInfoResponse> RuntimeInfoAsync(string serverId, CancellationToken cancellationToken) =>
        channel.SendAsync<RuntimeInfoRequest, RuntimeInfoResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.RuntimeInfoOperation, new RuntimeInfoRequest(), cancellationToken);

    public Task<byte[]> PullHistoryAsync(string serverId, HistoryPullRequest request, CancellationToken cancellationToken) =>
        channel.SendAsync<HistoryPullRequest, byte[]>(serverId, MonitorProtocol.PluginId, MonitorProtocol.HistoryPullOperation, request, cancellationToken);

    public Task<IdentityResponse> LookUpAsync(string serverId, IdentityRequest request, CancellationToken cancellationToken) =>
        channel.SendAsync<IdentityRequest, IdentityResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.IdentityLookupOperation, request, cancellationToken);

    public Task<RunStartResponse> StartRunAsync(string serverId, RunStartRequest request, CancellationToken cancellationToken) =>
        channel.SendAsync<RunStartRequest, RunStartResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.RunStartOperation, request, cancellationToken);

    public Task<RunResultResponse> RunResultAsync(string serverId, RunResultRequest request, CancellationToken cancellationToken) =>
        channel.SendAsync<RunResultRequest, RunResultResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.RunResultOperation, request, cancellationToken);

    public Task<IncidentResultResponse> IncidentResultAsync(string serverId, IncidentResultRequest request, CancellationToken cancellationToken) =>
        channel.SendAsync<IncidentResultRequest, IncidentResultResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.IncidentResultOperation, request, cancellationToken);

    public Task<DrillRootsResponse> DrillRootsAsync(string serverId, DrillRootsRequest request, CancellationToken cancellationToken) =>
        channel.SendAsync<DrillRootsRequest, DrillRootsResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.DrillRootsOperation, request, cancellationToken);

    public Task<DrillStartResponse> StartDrillAsync(string serverId, DrillStartRequest request, CancellationToken cancellationToken) =>
        channel.SendAsync<DrillStartRequest, DrillStartResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.DrillStartOperation, request, cancellationToken);

    public Task<DrillResultResponse> DrillResultAsync(string serverId, DrillResultRequest request, CancellationToken cancellationToken) =>
        channel.SendAsync<DrillResultRequest, DrillResultResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.DrillResultOperation, request, cancellationToken);

    public Task<PatchListResponse> ListPatchesAsync(string serverId, PatchListRequest request, CancellationToken cancellationToken) =>
        channel.SendAsync<PatchListRequest, PatchListResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.PatchListOperation, request, cancellationToken);

    public Task<AuditStartResponse> StartAuditAsync(string serverId, AuditStartRequest request, CancellationToken cancellationToken) =>
        channel.SendAsync<AuditStartRequest, AuditStartResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.AuditStartOperation, request, cancellationToken);

    public Task<AuditResultResponse> AuditResultAsync(string serverId, AuditResultRequest request, CancellationToken cancellationToken) =>
        channel.SendAsync<AuditResultRequest, AuditResultResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.AuditResultOperation, request, cancellationToken);

    public Task<TriggerSetResponse> SetTriggersAsync(string serverId, TriggerSettings settings, CancellationToken cancellationToken) =>
        channel.SendAsync<TriggerSettings, TriggerSetResponse>(serverId, MonitorProtocol.PluginId, MonitorProtocol.TriggerSetOperation, settings, cancellationToken);
}
