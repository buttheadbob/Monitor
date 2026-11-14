using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Api;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>
/// The public API other plugins get from Quasar's services. Everything here turns Monitor's own types
/// into the public ones, so those stay free to change. Only a run reaches a game server. Events go
/// through one queue, so a slow or broken plugin can't hold up collection, and the queue drops the
/// oldest if it's left behind. Nothing here is built until a plugin asks for it.
/// </summary>
public sealed class MonitorApi : IMonitorApi
{
    private const int MaxTimelinePoints = 1200;
    private const int MaxRunSeconds = 600;
    private const int QueuedEvents = 256;
    internal const string NotConnected = "That server isn't connected.";

    private readonly IMonitorServers _servers;
    private readonly HistoryStore _store;
    private readonly HistoryWindows _windows;
    private readonly MonitorRuns _runs;
    private readonly MonitorCollector _collector;
    private readonly MonitorIncidents _incidents;
    private readonly MonitorSettings _settings;
    private readonly ILogger<MonitorApi> _logger;
    private readonly ConcurrentDictionary<string, MonitorTools> _tools = new();
    private readonly ConcurrentDictionary<string, DateTime> _nextInspect = new();
    private readonly Channel<Action> _queue = Channel.CreateBounded<Action>(new BoundedChannelOptions(QueuedEvents)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    public MonitorApi(
        IMonitorServers servers,
        HistoryStore store,
        HistoryWindows windows,
        MonitorRuns runs,
        MonitorCollector collector,
        MonitorIncidents incidents,
        MonitorSettings settings,
        IHostApplicationLifetime lifetime,
        ILogger<MonitorApi> logger)
    {
        _servers = servers;
        _store = store;
        _windows = windows;
        _runs = runs;
        _collector = collector;
        _incidents = incidents;
        _settings = settings;
        _logger = logger;

        store.SampleKept += (server, time, since) => Queue(SampleKept, (server, time, since));
        runs.Saved += (server, id) => Queue(RunFinished, new MonitorRunFinished(server, id));
        store.MarkerAdded += (server, marker) => Queue(EventRecorded, ToEvent(server, marker));
        incidents.Changed += (server, incident) => Queue(IncidentChanged, new MonitorIncident(server, incident.Id, incident.Opened, incident.Closed, incident.Reason, incident.LowestSim));

        _ = Task.Run(() => DeliverAsync(lifetime.ApplicationStopping));
    }

    public event Action<MonitorRunFinished>? RunFinished;

    public event Action<MonitorEvent>? EventRecorded;

    public event Action<MonitorIncident>? IncidentChanged;

    /// <summary>For the tools, which build the sample only if someone wants it.</summary>
    internal event Action<(string Server, DateTime Time, DateTime Since)>? SampleKept;

    /// <summary>Puts a request that changes the server in the log, with who asked.</summary>
    internal void NoteRequest(string requestedBy, string what) => _logger.LogInformation("{RequestedBy} asked Monitor to {What}.", requestedBy, what);

    public int Version => 4;

    public IMonitorTools For(string server) => ToolsFor(server);

    /// <summary>The tools for one server. Torch's plugin hands its own to other plugins, as what they get from <see cref="MonitorServerApi"/>.</summary>
    public MonitorTools ToolsFor(string server) =>
        _tools.GetOrAdd(server, id => new MonitorTools(id, this, _servers, _store, _runs, _collector, _incidents, _settings, _nextInspect));

    public IReadOnlyList<MonitorServerInfo> Servers()
    {
        DateTime now = DateTime.UtcNow;
        var servers = new Dictionary<string, MonitorServerInfo>();

        foreach (KnownServer known in _store.KnownServers())
            servers[known.Id] = new MonitorServerInfo(known.Id, known.Name, known.Cluster, false, known.LastSeen);

        foreach (MonitorServer server in _servers.Connected())
            servers[server.HistoryId] = new MonitorServerInfo(server.HistoryId, server.Name, server.Cluster, true, now);

        return servers.Values.OrderBy(server => server.Name, StringComparer.Ordinal).ToList();
    }

    public MonitorLatest? Latest(string server)
    {
        if (_store.Latest(server) is not { } point)
            return null;

        double[] split = FrameParts.Split(point);
        var parts = new Dictionary<string, double>(split.Length);

        for (int part = 0; part < split.Length; part++)
            parts[FrameParts.All[part].Name] = split[part];

        double frame = HistoryScopes.Frame < point.Scopes.Length ? point.Scopes[HistoryScopes.Frame] : 0d;
        StoredCensus? census = _store.LatestCensusRow(server);

        return new MonitorLatest(
            point.Time,
            new MonitorFrame(point.SimSpeed, frame, point.FrameMax, FrameParts.Available(point)),
            point.FrameP99,
            parts,
            census == null ? null : ToWorld(census.Time, census.Row))
        {
            Network = new MonitorNetwork(point.SentKb, point.ReceivedKb, point.RpcsIn),
        };
    }

    public IReadOnlyList<MonitorSavedRun> SavedRuns(string server) =>
        _runs.List(server).Select(run => new MonitorSavedRun(run.Id, run.Time, run.Seconds, run.SimSpeed, run.AverageFrameMs)).ToList();

    public void KeepFresh(string server) => _collector.Watch(server);

    public async Task<MonitorReply> RequestAsync(MonitorRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Source switch
            {
                MonitorSource.Run => await RunAsync(request, cancellationToken).ConfigureAwait(false),
                MonitorSource.History => FromHistory(request),
                _ => FromSavedRun(request),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A server restarting mid-request ends up here, however its connection happened to fail.
            _logger.LogDebug(exception, "A Monitor API request failed.");
            return ReplyBuilder.Failed(request, "The server stopped answering: " + exception.Message);
        }
    }

    private async Task<MonitorReply> RunAsync(MonitorRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RequestedBy))
            return ReplyBuilder.Refused(request, "A run needs to say who asked for it.");

        if (request.Seconds < 1 || request.Seconds > MaxRunSeconds)
            return ReplyBuilder.Refused(request, "Seconds must be from 1 to " + MaxRunSeconds + ".");

        if (_servers.Connected().FirstOrDefault(server => server.HistoryId == request.Server) is not { } target)
            return ReplyBuilder.Refused(request, NotConnected);

        // Clusters change how physics is scheduled, so they only run when they're wanted.
        var start = new RunStartRequest
        {
            Seconds = request.Seconds,
            GridId = request.GridId,
            Player = request.Player,
            Faction = request.Faction,
            BlockType = request.BlockType,
            Clusters = ReplyBuilder.ListsFor(request).Contains(MonitorList.Clusters),
        };
        RunOutcome outcome = await _runs.RunAsync(target, start, request.RequestedBy, cancellationToken).ConfigureAwait(false);

        if (outcome.Refused != null)
            return ReplyBuilder.Refused(request, outcome.Refused, outcome.RetryAfter);

        return outcome.Result == null
            ? ReplyBuilder.Failed(request, outcome.Problem ?? "The run failed. The details are in the server log.")
            : ReplyBuilder.FromRun(request, outcome.Result, outcome.RunId, DateTime.UtcNow);
    }

    private MonitorReply FromSavedRun(MonitorRequest request)
    {
        SavedRunInfo? saved = request.RunId == null
            ? _runs.List(request.Server).FirstOrDefault()
            : _runs.List(request.Server).FirstOrDefault(run => run.Id == request.RunId);

        if (saved == null || _runs.Load(request.Server, saved.Id) is not { } run)
            return ReplyBuilder.Refused(request, "There's no saved run like that.");

        return ReplyBuilder.FromRun(request, run, saved.Id, saved.Time);
    }

    private MonitorReply FromHistory(MonitorRequest request)
    {
        DateTime oldest = DateTime.UtcNow - _settings.Retention;
        DateTime from = request.From < oldest ? oldest : request.From;
        DateTime to = request.To;

        if (to <= from)
            return ReplyBuilder.Refused(request, "A range has to end after it starts, and history only goes back " + _settings.RetentionDays + " days.");

        WindowSummary summary = _windows.Summarize(request.Server, from, to);
        IReadOnlyCollection<MonitorList> lists = ReplyBuilder.ListsFor(request);
        Dictionary<HistoryDimension, List<WindowRow>> rows = _windows.Lists(request.Server, lists.Select(ReplyBuilder.HistoryDimensionOf).OfType<HistoryDimension>().ToList(), from, to);

        return new MonitorReply
        {
            State = MonitorReplyState.Finished,
            Server = request.Server,
            Source = MonitorSource.History,
            Accuracy = MonitorAccuracy.Background,
            From = from,
            To = to,
            Frame = ToFrame(summary),
            PartsMs = ToParts(summary),
            Lists = lists.Select(list => HistoryList(list, rows)).ToList(),
            Timeline = _store.Points(request.Server, from, to, MaxTimelinePoints).Select(ToPoint).ToList(),
            World = _store.Census(request.Server, from, to).Select(row => ToWorld(row.Time, row.Row)).ToList(),
            Events = _store.Markers(request.Server, from, to).Select(marker => ToEvent(request.Server, marker)).ToList(),
        };
    }

    private static MonitorListReply HistoryList(MonitorList list, Dictionary<HistoryDimension, List<WindowRow>> rows)
    {
        if (ReplyBuilder.HistoryDimensionOf(list) is not { } dimension)
            return new MonitorListReply { List = list, Available = false, Why = ReplyBuilder.NotInHistory };

        return new MonitorListReply
        {
            List = list,
            Available = true,
            Rows = rows[dimension]
                .Take(ReplyBuilder.MostRows(list))
                .Select(row => new MonitorRow(row.Name, ReplyBuilder.StableId(list, row.Key), row.MainMs, row.WorkerMs, row.PeakMs, row.CallsPerFrame, row.SeenShare)
                {
                    Thread = MonitorThreads.Of(row.MainMs, row.WorkerMs),
                    BytesPerSecond = row.BytesPerSecond,
                    Blocks = row.Blocks,
                    Grids = row.Grids,
                })
                .ToList(),
        };
    }

    private static MonitorPoint ToPoint(HistoryPoint point) =>
        new(point.Time, point.SimSpeed, point.FrameP50, point.FrameP95, point.FrameP99, point.FrameMax, FrameParts.Available(point))
        {
            SentKb = point.SentKb,
            ReceivedKb = point.ReceivedKb,
        };

    internal static MonitorFrame? ToFrame(WindowSummary summary) =>
        summary.HasData ? new MonitorFrame(summary.SimSpeed, summary.AverageFrameMs, summary.WorstFrameMs, summary.AvailableMs) : null;

    internal static Dictionary<string, double> ToParts(WindowSummary summary)
    {
        var parts = new Dictionary<string, double>(summary.Parts.Count);

        if (!summary.HasData)
            return parts;

        for (int part = 0; part < summary.Parts.Count; part++)
            parts[FrameParts.All[part].Name] = summary.Parts[part];

        return parts;
    }

    internal static MonitorWorldPoint ToWorld(DateTime time, HistoryCensus row) =>
        new(time, row.OnlinePlayers, row.Grids, row.Blocks, row.Pcu, row.PingMean)
        {
            ConcealedGrids = row.ConcealedGrids,
            ConcealedBlocks = row.ConcealedBlocks,
            StaticGrids = row.StaticGrids,
            Asteroids = row.Asteroids,
            Planets = row.Planets,
            CpuPercent = row.CpuPercent,
            MemoryMb = row.WorkingSetMb,
            ManagedMemoryMb = row.ManagedMb,
            Uptime = TimeSpan.FromTicks(row.UnitsSinceStart),
        };

    internal static MonitorEvent ToEvent(string server, StoredMarker marker) => new(server, marker.Time, marker.Kind switch
    {
        StoredMarkerKind.Restart => MonitorEventKind.Restart,
        StoredMarkerKind.Change => MonitorEventKind.Change,
        StoredMarkerKind.Lag => MonitorEventKind.Lag,
        StoredMarkerKind.Measuring => MonitorEventKind.Measuring,
        StoredMarkerKind.JoinHitch => MonitorEventKind.JoinHitch,
        StoredMarkerKind.ThreadJump => MonitorEventKind.ThreadJump,
        _ => MonitorEventKind.Save,
    }, marker.Text);

    // These run under other services' locks, so nothing here may wait.
    private void Queue<T>(Action<T>? handlers, T value)
    {
        if (handlers != null)
            _queue.Writer.TryWrite(() => Deliver(handlers, value));
    }

    internal void Deliver<T>(Action<T> handlers, T value)
    {
        foreach (Action<T> handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception)
            {
                // Someone else's code. It failing must not stop the next plugin hearing about this.
                _logger.LogWarning(exception, "A plugin's Monitor event handler failed.");
            }
        }
    }

    private async Task DeliverAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (Action delivery in _queue.Reader.ReadAllAsync(stopping).ConfigureAwait(false))
                delivery();
        }
        catch (OperationCanceledException)
        {
            // Quasar is shutting down.
        }
    }
}
