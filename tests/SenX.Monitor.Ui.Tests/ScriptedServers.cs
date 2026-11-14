using Microsoft.Extensions.Hosting;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Ui.Tests;

internal sealed class NoLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication()
    {
    }
}

/// <summary>A server that does what a test says. Runs finish straight away unless held.</summary>
internal sealed class ScriptedServers : IMonitorServers
{
    private readonly object _gate = new();
    private int _starts;

    public List<MonitorServer> Servers { get; } = [];

    public RunResultResponse Result { get; set; } = new()
    {
        State = RunState.Finished,
        Seconds = 10d,
        Frames = 600L,
        SimSpeed = 0.9d,
        AverageFrameMs = 15d,
        WorstFrameMs = 40d,
        TimingMsPerFrame = 0.5d,
        Coverage = 1d,
        Rows =
        [
            new RunRow { List = RunLists.Grids, Name = "Big Ship", Key = 7L, MainMsPerFrame = 3d, WorkerMsPerFrame = 1d, PeakMs = 9d, CallsPerFrame = 12d },
            new RunRow { List = RunLists.BlockTypes, Name = "MyDrill", Key = 8L, MainMsPerFrame = 2d, PeakMs = 4d, CallsPerFrame = 5d },
        ],
        SlowCalls = [new SlowCallRow { Frame = 1L, Ms = 4d, Where = "Entity before", Name = "Drill on 'Big Ship'", MainThread = true }],
    };

    /// <summary>While set and not finished, a run stays running.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public string? Refuse { get; set; }

    /// <summary>How long a refusal says the run in the way has left.</summary>
    public int RefuseSeconds { get; set; }

    /// <summary>While true, asking for a run's result fails, the way a dropped connection does.</summary>
    public bool FailResults { get; set; }

    /// <summary>While set and not finished, a request to start doesn't come back.</summary>
    public TaskCompletionSource? HoldStart { get; set; }

    public int Starts => _starts;

    public List<RunStartRequest> Started { get; } = [];

    public Dictionary<long, IncidentResultResponse> Incidents { get; } = [];

    public IReadOnlyList<MonitorServer> Connected() => Servers;

    public async Task<RunStartResponse> StartRunAsync(string serverId, RunStartRequest request, CancellationToken cancellationToken)
    {
        if (HoldStart != null)
            await HoldStart.Task.WaitAsync(cancellationToken);

        lock (_gate)
        {
            Started.Add(request);
            _starts++;
            return new RunStartResponse { Started = Refuse == null, RunId = _starts, Message = Refuse, RetrySeconds = RefuseSeconds };
        }
    }

    public Task<RunResultResponse> RunResultAsync(string serverId, RunResultRequest request, CancellationToken cancellationToken)
    {
        if (FailResults)
            return Task.FromException<RunResultResponse>(new TimeoutException("A test said so."));

        return Task.FromResult(Hold is { Task.IsCompleted: false } ? new RunResultResponse { State = RunState.Running } : Result);
    }

    public Task<IncidentResultResponse> IncidentResultAsync(string serverId, IncidentResultRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Incidents.TryGetValue(request.Id, out IncidentResultResponse? report) ? report : new IncidentResultResponse { State = RunState.Running });

    public Task<TriggerSetResponse> SetTriggersAsync(string serverId, TriggerSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult(new TriggerSetResponse { Applied = true });

    public Task<RuntimeInfoResponse> RuntimeInfoAsync(string serverId, CancellationToken cancellationToken) =>
        Task.FromResult(new RuntimeInfoResponse { Plugins = [], Mods = [] });

    public Task<DrillRootsResponse> DrillRootsAsync(string serverId, DrillRootsRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<DrillStartResponse> StartDrillAsync(string serverId, DrillStartRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<DrillResultResponse> DrillResultAsync(string serverId, DrillResultRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<PatchListResponse> ListPatchesAsync(string serverId, PatchListRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<AuditStartResponse> StartAuditAsync(string serverId, AuditStartRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<AuditResultResponse> AuditResultAsync(string serverId, AuditResultRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<byte[]> PullHistoryAsync(string serverId, HistoryPullRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IdentityResponse> LookUpAsync(string serverId, IdentityRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
