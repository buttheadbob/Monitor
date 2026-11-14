using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>One lag trip's evidence. No report yet means the worker is still gathering it.</summary>
public sealed class IncidentEvidence
{
    public long WorkerId { get; set; }

    public long WorkerStartedUtcTicks { get; set; }

    public DateTime Time { get; set; }

    public IncidentResultResponse? Report { get; set; }

    /// <summary>Why this will never have a report.</summary>
    public string? Lost { get; set; }
}

/// <summary>A stretch of lag: when it began, when it ended, how bad it got, and what caused it.</summary>
public sealed class StoredIncident
{
    public string Id { get; set; } = string.Empty;

    public DateTime Opened { get; set; }

    public DateTime? Closed { get; set; }

    /// <summary>Whether it recovered or the server restarted. Empty while it's open.</summary>
    public string ClosedBy { get; set; } = string.Empty;

    public double LowestSim { get; set; } = 1d;

    public double WorstFrameMs { get; set; }

    /// <summary>What looked unusual against this server's normal when it opened.</summary>
    public List<string> Unusual { get; set; } = [];

    /// <summary>Lag that outlasts the worker's cooldown trips again, so one incident can hold several.</summary>
    public List<IncidentEvidence> Evidence { get; set; } = [];
}

/// <summary>An incident in a list, without its evidence.</summary>
public sealed record IncidentInfo(string Id, DateTime Opened, DateTime? Closed, string ClosedBy, double LowestSim, double WorstFrameMs, string Reason)
{
    public TimeSpan? Length => Closed - Opened;
}

/// <summary>
/// Turns the worker's lag trips into incidents. One opens at a trip, gathers the evidence, and closes
/// once the server has stayed healthy for a while, so a brief recovery in the middle of lag doesn't
/// split it in two. Incidents are kept for as long as the retention setting says. A failure here never
/// stops history being collected.
/// </summary>
public sealed class MonitorIncidents(IMonitorServers servers, string directory, MonitorSettings settings, MonitorBaselines baselines, ILogger<MonitorIncidents> logger)
{
    /// <summary>Raised when an incident opens or closes, with its server. Listeners mustn't wait, because this is under a lock.</summary>
    public event Action<string, IncidentInfo>? Changed;

    /// <summary>Raised when an incident gets its evidence, with its server and id.</summary>
    public event Action<string, string>? EvidenceArrived;

    private const string IdFormat = "yyyyMMdd-HHmmss";
    private const int MostEvidence = 32;

    private static readonly TimeSpan s_recovery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_saveOpenEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_giveUp = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, ServerState> _servers = [];

    // A copy of what's open, because saving happens under the lock and this gets read from places that mustn't wait.
    private readonly ConcurrentDictionary<string, DateTime?> _openSince = new();

    private sealed class ServerState
    {
        public required string Folder { get; init; }

        public List<StoredIncident> Incidents { get; } = [];

        public StoredIncident? Open { get; set; }

        public DateTime? FirstGood { get; set; }

        public DateTime NextSave { get; set; }
    }

    /// <summary>Takes a batch of history: trips open incidents, and healthy seconds close them.</summary>
    public void Apply(string serverId, HistoryBatch batch)
    {
        var started = new DateTime(batch.StartedUtcTicks, DateTimeKind.Utc);
        TriggerSettings limits = settings.Triggers;

        lock (_gate)
        {
            ServerState state = StateOf(serverId);
            bool changed = false;
            bool tripped = false;

            foreach (HistoryMarker marker in batch.Markers)
            {
                if (marker.Kind == HistoryMarkerKind.LagTripped)
                {
                    Trip(serverId, state, started.AddTicks(marker.UnitsSinceStart), marker.Value, batch.StartedUtcTicks);
                    changed = true;
                    tripped = true;
                }
            }

            foreach (HistoryRollup row in batch.Rollups)
                changed |= Second(serverId, state, row, started.AddTicks(row.UnitsSinceStart), limits);

            // Every row counts as a change while one's open, and writing all of it each pull would cost the most
            // during the worst lag, which on Torch is in the game's own process.
            DateTime now = DateTime.UtcNow;

            if (changed && state.Open != null && (tripped || now >= state.NextSave))
            {
                state.NextSave = now + s_saveOpenEvery;
                Save(state, state.Open);
            }
        }
    }

    /// <summary>A restarted server's incident is over, whatever state it was in.</summary>
    public void ServerRestarted(string serverId, long startedUtcTicks)
    {
        lock (_gate)
        {
            ServerState state = StateOf(serverId);

            if (state.Open is { } open)
                Close(serverId, state, open, new DateTime(startedUtcTicks, DateTimeKind.Utc), "Server restarted");
        }
    }

    /// <summary>
    /// Asks the worker for any evidence still missing. Failing just means asking again next pull.
    /// <paramref name="runFound"/> hears of each run as it arrives, so one failing later doesn't lose it.
    /// </summary>
    public async Task CollectEvidenceAsync(MonitorServer server, long startedUtcTicks, CancellationToken cancellationToken, Action<RunResultResponse>? runFound = null)
    {
        foreach ((StoredIncident incident, IncidentEvidence evidence) in Waiting(server.HistoryId))
        {
            IncidentResultResponse? report = null;
            string? lost = null;

            if (evidence.WorkerStartedUtcTicks != startedUtcTicks)
            {
                lost = "The server restarted before it was collected.";
            }
            else
            {
                report = await servers.IncidentResultAsync(server.Id, new IncidentResultRequest { Id = evidence.WorkerId }, cancellationToken).ConfigureAwait(false);

                if (report.State == RunState.Running || (report.State == RunState.Unknown && DateTime.UtcNow - evidence.Time < s_giveUp))
                    continue;

                if (report.State == RunState.Unknown)
                {
                    lost = "The worker no longer has it.";
                    report = null;
                }
            }

            lock (_gate)
            {
                evidence.Report = report;
                evidence.Lost = lost;
                Save(StateOf(server.HistoryId), incident);
            }

            if (report != null)
                EvidenceArrived?.Invoke(server.HistoryId, incident.Id);

            if (report?.Run != null)
                runFound?.Invoke(report.Run);
        }
    }

    /// <summary>Writes out what's open, as the host stops.</summary>
    public void SaveOpen()
    {
        lock (_gate)
        {
            foreach (ServerState state in _servers.Values)
            {
                if (state.Open is { } open)
                    Save(state, open);
            }
        }
    }

    /// <summary>Deletes incidents past the retention setting.</summary>
    public void Prune(string serverId)
    {
        lock (_gate)
            Prune(StateOf(serverId));
    }

    /// <summary>This server's incidents, newest first. Ones past the retention setting are deleted on the way.</summary>
    public List<IncidentInfo> List(string serverId)
    {
        lock (_gate)
        {
            ServerState state = StateOf(serverId);
            Prune(state);
            return state.Incidents.OrderByDescending(incident => incident.Opened).Select(Summarize).ToList();
        }
    }

    /// <summary>An incident with its evidence. This is a copy, so the collector can carry on changing the original.</summary>
    public StoredIncident? Load(string serverId, string id)
    {
        lock (_gate)
        {
            StoredIncident? incident = StateOf(serverId).Incidents.Find(row => row.Id == id);
            return incident == null ? null : JsonSerializer.Deserialize<StoredIncident>(JsonSerializer.Serialize(incident));
        }
    }

    /// <summary>When the server's open incident began, if it has one. This only waits the first time a server's asked about.</summary>
    public DateTime? OpenSince(string serverId)
    {
        if (_openSince.TryGetValue(serverId, out DateTime? opened))
            return opened;

        lock (_gate)
            return StateOf(serverId).Open?.Opened;
    }

    private static IncidentInfo Summarize(StoredIncident incident) => new(
        incident.Id,
        incident.Opened,
        incident.Closed,
        incident.ClosedBy,
        incident.LowestSim,
        incident.WorstFrameMs,
        ReasonOf(incident));

    /// <summary>What set an incident off, in words. Saying "lag" is all there is until the evidence arrives.</summary>
    public static string ReasonOf(StoredIncident incident)
    {
        IncidentResultResponse? report = incident.Evidence.Find(evidence => evidence.Report != null)?.Report;

        return report?.Reason switch
        {
            "SimSpeed" => "Sim speed fell to " + report.Value.ToString("0.00", CultureInfo.InvariantCulture),
            "FrameTime" => "Frames reached " + report.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms",
            "Spike" => "One frame took " + report.Value.ToString("0", CultureInfo.InvariantCulture) + " ms",
            _ => "Lag noticed",
        };
    }

    private void Trip(string serverId, ServerState state, DateTime time, uint workerId, long workerStartedUtcTicks)
    {
        var evidence = new IncidentEvidence { WorkerId = workerId, WorkerStartedUtcTicks = workerStartedUtcTicks, Time = time };

        if (state.Open is { } open)
        {
            if (open.Evidence.Count < MostEvidence)
                open.Evidence.Add(evidence);

            state.FirstGood = null;
            return;
        }

        var incident = new StoredIncident
        {
            Id = time.ToString(IdFormat, CultureInfo.InvariantCulture),
            Opened = time,
            Unusual = baselines.UnusualNow(serverId),
            Evidence = [evidence],
        };

        state.Incidents.Add(incident);
        state.Open = incident;
        state.FirstGood = null;
        _openSince[serverId] = incident.Opened;
        Changed?.Invoke(serverId, Summarize(incident));
    }

    private bool Second(string serverId, ServerState state, HistoryRollup row, DateTime time, TriggerSettings limits)
    {
        if (state.Open is not { } open || time < open.Opened)
            return false;

        open.LowestSim = Math.Min(open.LowestSim, row.SimSpeed);
        open.WorstFrameMs = Math.Max(open.WorstFrameMs, row.FrameMax / 10000d);

        bool healthy = row.SimSpeed >= limits.SimBelow && (limits.FrameP95AboveMs <= 0d || row.FrameP95 / 10000d <= limits.FrameP95AboveMs);

        if (!healthy)
        {
            state.FirstGood = null;
            return true;
        }

        state.FirstGood ??= time;

        if (time - state.FirstGood.Value >= s_recovery)
            Close(serverId, state, open, state.FirstGood.Value, "Recovered");

        return true;
    }

    private void Close(string serverId, ServerState state, StoredIncident incident, DateTime time, string how)
    {
        incident.Closed = time;
        incident.ClosedBy = how;
        state.Open = null;
        state.FirstGood = null;
        _openSince[serverId] = null;
        Save(state, incident);
        Changed?.Invoke(serverId, Summarize(incident));
    }

    private List<(StoredIncident Incident, IncidentEvidence Evidence)> Waiting(string serverId)
    {
        lock (_gate)
        {
            return StateOf(serverId).Incidents
                .SelectMany(incident => incident.Evidence.Where(evidence => evidence.Report == null && evidence.Lost == null).Select(evidence => (incident, evidence)))
                .ToList();
        }
    }

    private ServerState StateOf(string serverId)
    {
        if (_servers.TryGetValue(serverId, out ServerState? state))
            return state;

        state = new ServerState { Folder = Path.Combine(directory, StateFiles.FolderFor(serverId), "incidents") };
        LoadAll(state);
        Prune(state);
        _servers[serverId] = state;
        _openSince[serverId] = state.Open?.Opened;
        return state;
    }

    private void LoadAll(ServerState state)
    {
        if (!Directory.Exists(state.Folder))
            return;

        foreach (string path in Directory.GetFiles(state.Folder, "*.json"))
        {
            try
            {
                StoredIncident? incident = JsonSerializer.Deserialize<StoredIncident>(File.ReadAllText(path));

                if (incident == null)
                    continue;

                state.Incidents.Add(incident);

                if (incident.Closed == null)
                    state.Open = incident;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Monitor could not read a saved incident.");
            }
        }
    }

    private void Save(ServerState state, StoredIncident incident)
    {
        try
        {
            Directory.CreateDirectory(state.Folder);
            StateFiles.Write(Path.Combine(state.Folder, incident.Id + ".json"), JsonSerializer.Serialize(incident));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Monitor could not save an incident.");
        }
    }

    private void Prune(ServerState state)
    {
        DateTime oldest = DateTime.UtcNow - settings.Retention;

        foreach (StoredIncident incident in state.Incidents.Where(row => row.Opened < oldest && row.Closed != null).ToList())
        {
            state.Incidents.Remove(incident);

            try
            {
                File.Delete(Path.Combine(state.Folder, incident.Id + ".json"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Monitor could not delete an old incident.");
            }
        }
    }
}
