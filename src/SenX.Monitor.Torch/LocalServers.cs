using Newtonsoft.Json;
using Sandbox;
using SenX.Monitor.Contracts;
using SenX.Monitor.Magnetar;
using SenX.Monitor.Magnetar.Patching;
using SenX.Monitor.Magnetar.Reporting;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch;

/// <summary>
/// The one server Torch has, which is the process this runs in. Every answer comes from the same
/// worker code that answers Quasar on Magnetar, through the same shapes, so the store and pages need
/// no second path. The JSON round trips are that path too: they're rare, and it's one thing to prove.
/// </summary>
public sealed class LocalServers : IMonitorServers
{
    /// <summary>The one server's id, which is also its history id.</summary>
    public const string LocalId = "local";

    private readonly List<MonitorServer> _servers;

    public LocalServers(string name) => _servers = [new MonitorServer(LocalId, name, LocalId)];

    public IReadOnlyList<MonitorServer> Connected() => _servers;

    public Task<RuntimeInfoResponse> RuntimeInfoAsync(string serverId, CancellationToken cancellationToken) =>
        Task.FromResult(Worker.ReadRuntimeInfo());

    public Task<byte[]> PullHistoryAsync(string serverId, HistoryPullRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Worker.PackHistory(request, Copy));

    public Task<IdentityResponse> LookUpAsync(string serverId, IdentityRequest request, CancellationToken cancellationToken) =>
        IdentityService.LookupAsync(request);

    public Task<RunStartResponse> StartRunAsync(string serverId, RunStartRequest request, CancellationToken cancellationToken) =>
        RunService.StartAsync(request);

    public Task<RunResultResponse> RunResultAsync(string serverId, RunResultRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Read<RunResultResponse>(RunService.ResultJson(request.RunId)));

    public Task<IncidentResultResponse> IncidentResultAsync(string serverId, IncidentResultRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Read<IncidentResultResponse>(IncidentService.ResultJson(request.Id)));

    public Task<TriggerSetResponse> SetTriggersAsync(string serverId, TriggerSettings settings, CancellationToken cancellationToken)
    {
        IncidentService.Configure(settings);
        return Task.FromResult(new TriggerSetResponse { Applied = true });
    }

    public Task<DrillRootsResponse> DrillRootsAsync(string serverId, DrillRootsRequest request, CancellationToken cancellationToken) =>
        OnGameThread(() => new DrillRootsResponse { Roots = DrillDown.Roots(request.Text) });

    public Task<DrillStartResponse> StartDrillAsync(string serverId, DrillStartRequest request, CancellationToken cancellationToken) =>
        OnGameThread(() => DrillDown.Start(request));

    public Task<DrillResultResponse> DrillResultAsync(string serverId, DrillResultRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Read<DrillResultResponse>(DrillDown.ResultJson()));

    public Task<PatchListResponse> ListPatchesAsync(string serverId, PatchListRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(PatchList.Page(request.Skip));

    public Task<AuditStartResponse> StartAuditAsync(string serverId, AuditStartRequest request, CancellationToken cancellationToken) =>
        OnGameThread(() => PatchAudit.Start(request));

    public Task<AuditResultResponse> AuditResultAsync(string serverId, AuditResultRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Read<AuditResultResponse>(PatchAudit.ResultJson()));

    private static byte[] Copy(byte[] bytes, int length)
    {
        var copy = new byte[length];
        Buffer.BlockCopy(bytes, 0, copy, 0, length);
        return copy;
    }

    private static T Read<T>(string json) where T : class, new() => JsonConvert.DeserializeObject<T>(json) ?? new T();

    // Patching belongs on the game thread, between frames, and so does reading what's in the session.
    private static Task<T> OnGameThread<T>(Func<T> work)
    {
        if (MySandboxGame.Static == null)
            return Task.FromException<T>(new InvalidOperationException("The game isn't running yet."));

        // Without this, whoever awaits it would carry on on the game thread.
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        MySandboxGame.Static.Invoke(
            () =>
            {
                try
                {
                    answer.SetResult(work());
                }
                catch (Exception exception)
                {
                    answer.SetException(exception);
                }
            },
            "Monitor request");

        return answer.Task;
    }
}
