using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>
/// Pulls history from every connected server every 30 seconds, whether or not anyone is watching, so
/// the charts have no holes. A server someone is looking at gets pulled every second. The worker
/// never depends on this: if it stops, the worker's rings overwrite themselves and history shows a gap.
/// </summary>
public sealed class MonitorCollector(
    IMonitorServers servers,
    HistoryStore store,
    MonitorIncidents incidents,
    MonitorRuns runs,
    MonitorSettings settings,
    ILogger<MonitorCollector> logger) : BackgroundService
{
    private static readonly TimeSpan s_tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_period = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_faultLogEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_watching = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_quietPruneEvery = TimeSpan.FromHours(1);

    // A backlog comes in pieces. This stops one server taking over if something goes wrong.
    private const int MaxBatchesPerPull = 500;

    private readonly ConcurrentDictionary<string, DateTime> _nextPull = new();
    private DateTime _nextQuietPrune;
    private DateTime _nextFaultLog;
    private readonly ConcurrentDictionary<string, DateTime> _watched = new();
    private readonly ConcurrentDictionary<(string Server, HistoryDimension Kind, long Key), string> _names = new();
    private readonly ConcurrentDictionary<string, string> _problems = new();
    private readonly ConcurrentDictionary<string, long> _startsNoted = new();
    private readonly ConcurrentDictionary<string, (long Started, long Version)> _triggersSent = new();

    /// <summary>Says a page is showing this server, so it should stay fresh.</summary>
    public void Watch(string serverId) => _watched[serverId] = DateTime.UtcNow;

    /// <summary>Why the last pull failed, if it did. Usually the worker isn't installed on that server.</summary>
    public string? Problem(string serverId) => _problems.GetValueOrDefault(serverId);

    /// <summary>The name of something in history, once it's known.</summary>
    public string? NameOf(string serverId, HistoryDimension kind, long key) =>
        _names.GetValueOrDefault((serverId, kind, key));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(s_tick);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // A background service that throws stops all of Quasar, and that's far worse than a gap in history.
                    if (DateTime.UtcNow >= _nextFaultLog)
                    {
                        _nextFaultLog = DateTime.UtcNow + s_faultLogEvery;
                        logger.LogWarning(exception, "Monitor's collection failed, and it will try again.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            // An open incident is only written every so often, and its latest figures shouldn't go with the process.
            incidents.SaveOpen();
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        DateTime now = DateTime.UtcNow;

        if (settings.TakeProblem() is { } problem)
            logger.LogWarning("{Problem}", problem);

        if (now >= _nextQuietPrune)
        {
            _nextQuietPrune = now + s_quietPruneEvery;
            Prune();
        }

        foreach (MonitorServer server in servers.Connected())
        {
            if (!Due(server.HistoryId, now))
                continue;

            try
            {
                await PullAsync(server, stoppingToken).ConfigureAwait(false);
                _problems.TryRemove(server.HistoryId, out _);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One server failing must not stop the others being collected.
                _problems[server.HistoryId] = exception.Message;
                logger.LogDebug(exception, "Monitor could not pull history from {Server}.", server.Name);
            }
        }
    }

    // Retention can't wait for someone to open a tab, because these hold player names and positions.
    private void Prune()
    {
        store.PruneQuietServers();

        // After that, because a server it removed has nothing left to prune.
        foreach (KnownServer server in store.KnownServers())
        {
            try
            {
                runs.Prune(server.Id);
                incidents.Prune(server.Id);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // One server's folder shouldn't make the rest wait another hour.
                logger.LogWarning(exception, "Monitor could not clear old runs and incidents for {Server}.", server.Name);
            }
        }
    }

    private bool Due(string serverId, DateTime now)
    {
        bool watched = _watched.TryGetValue(serverId, out DateTime seen) && now - seen < s_watching;
        if (!watched && now < _nextPull.GetValueOrDefault(serverId))
            return false;

        _nextPull[serverId] = now + s_period;
        return true;
    }

    internal async Task PullAsync(MonitorServer server, CancellationToken cancellationToken)
    {
        string history = server.HistoryId;
        store.NoteServer(server);

        for (int batchNumber = 0; batchNumber < MaxBatchesPerPull; batchNumber++)
        {
            CollectorPosition position = store.Position(history);

            var request = new HistoryPullRequest
            {
                FromRollup = position.NextRollup,
                FromCensus = position.NextCensus,
                FromGroups = position.NextGroups,
                FromDetail = position.NextDetail,
                FromMarkers = position.NextMarkers,
            };

            byte[] bytes = await servers.PullHistoryAsync(server.Id, request, cancellationToken).ConfigureAwait(false);
            HistoryBatch batch = HistoryReader.Read(bytes) ?? throw new InvalidDataException("This server's Monitor is a different version.");

            // The world is still loading, so there's nothing yet, and no start to compare with the last one.
            if (batch.StartedUtcTicks == 0L)
                return;

            // A restart or a replaced cluster node. Sequence numbers started again, and type keys now mean something else.
            if (position.StartedUtcTicks != 0L && batch.StartedUtcTicks != position.StartedUtcTicks)
            {
                ForgetProcessKeys(history);
                runs.ForgetRuns(history);
                store.Restart(history, batch.StartedUtcTicks);
                incidents.ServerRestarted(history, batch.StartedUtcTicks);
                continue;
            }

            await ResolveNamesAsync(server, batch, cancellationToken).ConfigureAwait(false);
            store.Add(history, batch, (kind, key) => NameOf(history, kind, key) ?? key.ToString(CultureInfo.InvariantCulture), (kind, key) => NameOf(history, kind, key));
            incidents.Apply(history, batch);

            if (batch.Markers.Any(marker => marker.Kind == HistoryMarkerKind.LagTripped))
                runs.NoteTriggeredRun(history, TimeSpan.FromSeconds(settings.Triggers.RunSeconds));

            foreach (HistoryMarker marker in batch.Markers.Where(marker => marker.Kind == HistoryMarkerKind.RunStarted))
                runs.NoteRunStarted(history, new DateTime(batch.StartedUtcTicks + marker.UnitsSinceStart, DateTimeKind.Utc), (int)marker.Value);

            foreach (HistoryMarker marker in batch.Markers.Where(marker => marker.Kind == HistoryMarkerKind.RunFinished))
                runs.NoteRunFinished(history, marker.Value);

            await runs.CollectFinishedRunsAsync(server, cancellationToken).ConfigureAwait(false);

            if (_startsNoted.GetValueOrDefault(history) != batch.StartedUtcTicks)
                await NoteStartAsync(server, batch.StartedUtcTicks, cancellationToken).ConfigureAwait(false);

            if (batch.NextRollup >= batch.RollupHead && batch.NextCensus >= batch.CensusHead
                && batch.NextGroups >= batch.GroupHead && batch.NextMarkers >= batch.MarkerHead)
            {
                await SendTriggersAsync(server, batch.StartedUtcTicks, cancellationToken).ConfigureAwait(false);
                await incidents.CollectEvidenceAsync(server, batch.StartedUtcTicks, cancellationToken, run => runs.SaveLagRun(history, run)).ConfigureAwait(false);
                return;
            }
        }
    }

    // A restarted worker goes back to its defaults, so it needs telling again as well as when they change.
    private async Task SendTriggersAsync(MonitorServer server, long startedUtcTicks, CancellationToken cancellationToken)
    {
        (long, long) wanted = (startedUtcTicks, settings.TriggersVersion);

        if (_triggersSent.TryGetValue(server.HistoryId, out (long, long) sent) && sent == wanted)
            return;

        await servers.SetTriggersAsync(server.Id, settings.Triggers, cancellationToken).ConfigureAwait(false);
        _triggersSent[server.HistoryId] = wanted;
    }

    // A server only knows what it's running, so saying what changed takes comparing it with last time.
    private async Task NoteStartAsync(MonitorServer server, long startedUtcTicks, CancellationToken cancellationToken)
    {
        RuntimeInfoResponse info = await servers.RuntimeInfoAsync(server.Id, cancellationToken).ConfigureAwait(false);

        // A server still starting up hasn't read them yet, and the next pull asks again.
        if (info.Plugins == null || info.Mods == null)
            return;

        string gcMode = (info.ServerGc ? "Server" : "Workstation") + ", " + info.GcLatencyMode;
        store.ServerStarted(server.HistoryId, startedUtcTicks, info.Plugins, info.Mods, gcMode);
        _startsNoted[server.HistoryId] = startedUtcTicks;
    }

    private void ForgetProcessKeys(string serverId)
    {
        foreach ((string Server, HistoryDimension Kind, long Key) key in _names.Keys)
        {
            bool stable = key.Kind is HistoryDimension.Grids or HistoryDimension.Owners or HistoryDimension.Factions or HistoryDimension.Scripts;
            if (key.Server == serverId && !stable)
                _names.TryRemove(key, out _);
        }
    }

    private async Task ResolveNamesAsync(MonitorServer server, HistoryBatch batch, CancellationToken cancellationToken)
    {
        string history = server.HistoryId;
        var missing = new Dictionary<(HistoryDimension Kind, long Key), IdentityQuery>();

        void Need(HistoryDimension kind, long key, long representative)
        {
            if (!_names.ContainsKey((history, kind, key)))
                missing[(kind, key)] = new IdentityQuery { Kind = kind, Key = key, Representative = representative };
        }

        // Thread jumps aren't asked about. They cover every object, and the server answers on its game thread, so
        // they take names already known, from this total's own rows or before, and the store fills the rest in later.
        foreach (HistoryGroup row in batch.Groups)
        {
            if (row.Dimension != HistoryDimension.ThreadJumps)
                Need(row.Dimension, row.Key, row.Representative);
        }

        foreach (HistoryPlayer player in batch.Players)
        {
            Need(HistoryDimension.Owners, player.IdentityId, 0L);
            Need(HistoryDimension.Factions, player.FactionId, 0L);
        }

        foreach (HistoryFaction faction in batch.Factions)
            Need(HistoryDimension.Factions, faction.FactionId, 0L);

        foreach (IdentityQuery[] queries in missing.Values.Chunk(MonitorProtocol.MaxIdentityQueries))
        {
            IdentityResponse response = await servers.LookUpAsync(server.Id, new IdentityRequest { Queries = [.. queries] }, cancellationToken).ConfigureAwait(false);

            foreach (IdentityAnswer answer in response.Answers)
                _names[(history, answer.Kind, answer.Key)] = answer.Name;
        }
    }
}
