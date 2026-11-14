using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>
/// A server that can be reached now. <paramref name="Id"/> addresses requests, and a replaced cluster
/// node gets a new one. <paramref name="HistoryId"/> is what its history is kept under, which a
/// replacement shares, so its charts carry on. <paramref name="Cluster"/> names its cluster, if any.
/// </summary>
public sealed record MonitorServer(string Id, string Name, string HistoryId, string Cluster = "")
{
    /// <summary>The agent behind this server, which is how Quasar's entity pages name it.</summary>
    public string AgentId { get; init; } = string.Empty;
}

/// <summary>
/// The servers Monitor can reach. Quasar's adapter talks to real ones, and the preview host simulates
/// them, so these pages and the collector run the same in both.
/// </summary>
public interface IMonitorServers
{
    IReadOnlyList<MonitorServer> Connected();

    Task<RuntimeInfoResponse> RuntimeInfoAsync(string serverId, CancellationToken cancellationToken);

    Task<byte[]> PullHistoryAsync(string serverId, HistoryPullRequest request, CancellationToken cancellationToken);

    Task<IdentityResponse> LookUpAsync(string serverId, IdentityRequest request, CancellationToken cancellationToken);

    Task<RunStartResponse> StartRunAsync(string serverId, RunStartRequest request, CancellationToken cancellationToken);

    Task<RunResultResponse> RunResultAsync(string serverId, RunResultRequest request, CancellationToken cancellationToken);

    Task<IncidentResultResponse> IncidentResultAsync(string serverId, IncidentResultRequest request, CancellationToken cancellationToken);

    Task<TriggerSetResponse> SetTriggersAsync(string serverId, TriggerSettings settings, CancellationToken cancellationToken);

    Task<DrillRootsResponse> DrillRootsAsync(string serverId, DrillRootsRequest request, CancellationToken cancellationToken);

    Task<DrillStartResponse> StartDrillAsync(string serverId, DrillStartRequest request, CancellationToken cancellationToken);

    Task<DrillResultResponse> DrillResultAsync(string serverId, DrillResultRequest request, CancellationToken cancellationToken);

    Task<PatchListResponse> ListPatchesAsync(string serverId, PatchListRequest request, CancellationToken cancellationToken);

    Task<AuditStartResponse> StartAuditAsync(string serverId, AuditStartRequest request, CancellationToken cancellationToken);

    Task<AuditResultResponse> AuditResultAsync(string serverId, AuditResultRequest request, CancellationToken cancellationToken);
}
