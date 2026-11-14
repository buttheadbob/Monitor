using System.Collections.Concurrent;
using SenX.Monitor.Api;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>
/// The tools for one server, from the full store. Quasar builds one for each server a plugin asks about, and
/// Torch builds one for itself, which is what its plugins get. Only runs, drill-downs and audits change anything
/// on the server. Everything else reads what's already been collected, and anything that can take a while does
/// it off the caller's thread, because on Torch that thread could be the game's.
/// </summary>
public sealed class MonitorTools : IMonitorServerApi
{
    private const int MostTop = 100;

    private static readonly TimeSpan s_drillPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_auditPoll = TimeSpan.FromSeconds(2);

    // Past the longest gap between two samples, to find the one before a range.
    private static readonly TimeSpan s_sampleLookBack = TimeSpan.FromMinutes(3);

    private readonly MonitorApi _api;
    private readonly IMonitorServers _servers;
    private readonly HistoryStore _store;
    private readonly MonitorRuns _runs;
    private readonly MonitorCollector _collector;
    private readonly MonitorIncidents _incidents;
    private readonly MonitorSettings _settings;
    private readonly ConcurrentDictionary<string, DateTime> _nextInspect;
    private readonly Pace _findPace = new(ToolBuilder.FindGap);
    private readonly object _inspectGate = new();

    // Shared by every server's tools, or a plugin could draw one per server all at once.
    private static readonly SemaphoreSlim s_imageGate = new(1, 1);

    private readonly ChartBuilder _charts;

    internal MonitorTools(
        string server,
        MonitorApi api,
        IMonitorServers servers,
        HistoryStore store,
        MonitorRuns runs,
        MonitorCollector collector,
        MonitorIncidents incidents,
        MonitorSettings settings,
        ConcurrentDictionary<string, DateTime> nextInspect)
    {
        Server = server;
        _api = api;
        _servers = servers;
        _store = store;
        _runs = runs;
        _collector = collector;
        _incidents = incidents;
        _settings = settings;
        _nextInspect = nextInspect;
        _charts = new ChartBuilder(store, servers);

        api.RunFinished += finished =>
        {
            if (finished.Server == Server && RunFinished is { } handlers)
                api.Deliver(handlers, finished);
        };

        api.EventRecorded += recorded =>
        {
            if (recorded.Server == Server && EventRecorded is { } handlers)
                api.Deliver(handlers, recorded);
        };

        api.IncidentChanged += incident =>
        {
            if (incident.Server == Server && IncidentChanged is { } handlers)
                api.Deliver(handlers, incident);
        };

        api.SampleKept += kept =>
        {
            if (kept.Server != Server || SampleRecorded is not { } handlers)
                return;

            List<MonitorSample> samples = ReadSamples(kept.Time, kept.Time, ReplyBuilder.ListsInHistory, ReplyBuilder.RowsPerList, kept.Since);

            if (samples.Count > 0)
                api.Deliver(handlers, samples[0]);
        };
    }

    public event Action<MonitorSample>? SampleRecorded;

    public event Action<MonitorRunFinished>? RunFinished;

    public event Action<MonitorEvent>? EventRecorded;

    public event Action<MonitorIncident>? IncidentChanged;

    public string Server { get; }

    public int Version => _api.Version;

    /// <summary>
    /// The worker's own tools, when it's in this process, as it is on Torch. Its rings are newer than the
    /// store, and reading them can't wait on anything.
    /// </summary>
    public IMonitorTools? Live { get; set; }

    public Task<MonitorReply> RequestAsync(MonitorRequest request, CancellationToken cancellationToken) =>
        Task.Run(() => _api.RequestAsync(request with { Server = Server }, cancellationToken), cancellationToken);

    public MonitorLatest? Latest() => Live?.Latest() ?? _api.Latest(Server);

    public MonitorStatus Status()
    {
        MonitorStatus? live = Live?.Status();
        long started = live?.Started?.Ticks ?? _store.StartedUtcTicks(Server);

        return new MonitorStatus(
            _servers.Connected().Any(server => server.HistoryId == Server),
            started == 0L ? null : new DateTime(started, DateTimeKind.Utc),
            live?.LastData ?? _store.Latest(Server)?.Time,
            live?.RunGoing ?? _runs.Current(Server) != null,
            _incidents.OpenSince(Server),
            _settings.Retention);
    }

    public Task<IReadOnlyList<MonitorPlayerNow>> PlayersAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorPlayerNow>>(
            () =>
            {
                if (Target() == null)
                    return [];

                return _store.World(Server).Players
                    .Select(player => new MonitorPlayerNow(
                        Named(HistoryDimension.Owners, player.IdentityId),
                        player.IdentityId,
                        Named(HistoryDimension.Factions, player.FactionId),
                        player.PingMs,
                        player.SentBytesPerSecond / 1024d,
                        player.ReceivedBytesPerSecond / 1024d,
                        player.RpcsPerSecond,
                        player.QueuedBytes / 1024d,
                        player.FailedSends))
                    .ToList();
            },
            cancellationToken);

    public Task<IReadOnlyList<MonitorFactionNow>> FactionsAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorFactionNow>>(
            () =>
            {
                if (Target() == null)
                    return [];

                return _store.World(Server).Factions
                    .Select(faction => new MonitorFactionNow(Named(HistoryDimension.Factions, faction.FactionId), faction.FactionId, faction.OnlineMembers, faction.Grids, faction.Blocks, faction.Pcu))
                    .ToList();
            },
            cancellationToken);

    private string Named(HistoryDimension kind, long key) =>
        _collector.NameOf(Server, kind, key) ?? key.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public Task<IReadOnlyList<MonitorRow>> TopAsync(MonitorList list, int count, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorRow>>(
            () => ReplyBuilder.HistoryDimensionOf(list) is not { } dimension
                ? []
                : _store.LatestGroups(Server, dimension)
                    .Take(Math.Clamp(count, 1, MostTop))
                    .Select(row => ToRow(list, row))
                    .ToList(),
            cancellationToken);

    private static MonitorRow ToRow(MonitorList list, StoredGroup row) => new(row.Name, ReplyBuilder.StableId(list, row.Key), row.MainMs, row.WorkerMs, row.PeakMs, row.CallsPerFrame, 1d)
    {
        BytesPerSecond = row.BytesPerSecond,
        Blocks = row.Blocks,
        Grids = row.Grids,
    };

    public Task<IReadOnlyList<MonitorSample>> SamplesAsync(DateTime from, DateTime to, IReadOnlyCollection<MonitorList> lists, int top, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorSample>>(
            () =>
            {
                DateTime oldest = DateTime.UtcNow - _settings.Retention;

                return ReadSamples(from < oldest ? oldest : from, to, lists.Count == 0 ? ReplyBuilder.ListsInHistory : lists, Math.Clamp(top, 1, ReplyBuilder.RowsPerList), default);
            },
            cancellationToken);

    // A sample's figures go back to the sample before, which can be just outside the range. The very first there's ever been goes back one normal step.
    private List<MonitorSample> ReadSamples(DateTime from, DateTime to, IReadOnlyCollection<MonitorList> lists, int top, DateTime since)
    {
        List<HistoryDimension> wanted = lists.Select(ReplyBuilder.HistoryDimensionOf).OfType<HistoryDimension>().ToList();
        DateTime reach = since != default ? from : from - s_sampleLookBack;

        List<IGrouping<DateTime, StoredGroup>> found = _store.SampleGroups(Server, wanted, reach, to).GroupBy(row => row.Time).OrderBy(total => total.Key).ToList();
        List<IGrouping<DateTime, StoredGroup>> totals = found.Where(total => total.Key >= from).ToList();
        var samples = new List<MonitorSample>(totals.Count);

        if (totals.Count == 0)
            return samples;

        DateTime before = since != default
            ? since
            : found.Count > totals.Count ? found[found.Count - totals.Count - 1].Key : totals[0].Key - SampleRate.Normal;

        List<HistoryPoint> points = _store.Points(Server, before, to, int.MaxValue);
        List<StoredCensus> census = _store.Census(Server, before, to);
        int point = 0;
        int nearest = -1;

        foreach (IGrouping<DateTime, StoredGroup> total in totals)
        {
            int first = point;

            while (point < points.Count && points[point].Time <= total.Key)
                point++;

            while (nearest + 1 < census.Count && census[nearest + 1].Time <= total.Key)
                nearest++;

            WindowSummary summary = HistoryWindows.Summarize(before, total.Key, points.GetRange(first, point - first));
            List<StoredGroup> rows = total.ToList();

            samples.Add(new MonitorSample(
                Server,
                total.Key,
                MonitorApi.ToFrame(summary),
                MonitorApi.ToParts(summary),
                nearest < 0 ? null : MonitorApi.ToWorld(census[nearest].Time, census[nearest].Row),
                lists.Select(list => SampleList(list, rows, top)).ToList())
            {
                Seconds = summary.Seconds,
                Frames = summary.Frames,
            });

            before = total.Key;
        }

        return samples;
    }

    private static MonitorListReply SampleList(MonitorList list, List<StoredGroup> rows, int top)
    {
        if (ReplyBuilder.HistoryDimensionOf(list) is not { } dimension)
            return new MonitorListReply { List = list, Available = false, Why = ReplyBuilder.NotInHistory };

        return new MonitorListReply
        {
            List = list,
            Available = true,
            Rows = rows
                .Where(row => row.Dimension == dimension)
                .OrderByDescending(row => row.MainMs + row.WorkerMs)
                .ThenByDescending(row => row.BytesPerSecond)
                .Take(top)
                .Select(row => ToRow(list, row))
                .ToList(),
        };
    }

    public async Task<MonitorHistory> HistoryAsync(DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        // A list the store can't answer for costs nothing, and this leaves the figures across the range.
        MonitorReply reply = await RequestAsync(ToolBuilder.CostsRequest(Server, from, to, [MonitorList.Clusters]), cancellationToken).ConfigureAwait(false);

        return new MonitorHistory(new MonitorRange(reply.From, reply.To), reply.Frame, reply.Timeline, reply.World, reply.Events);
    }

    public Task<MonitorReply> CostsAsync(DateTime from, DateTime to, IReadOnlyCollection<MonitorList> lists, CancellationToken cancellationToken) =>
        RequestAsync(ToolBuilder.CostsRequest(Server, from, to, lists), cancellationToken);

    public Task<IReadOnlyList<MonitorCostPoint>> CostHistoryAsync(MonitorList list, string name, DateTime from, DateTime to, CancellationToken cancellationToken) =>
        ReadCostPointsAsync(list, row => row.Name == name, from, to, cancellationToken);

    public Task<IReadOnlyList<MonitorCostPoint>> CostHistoryAsync(MonitorList list, long id, DateTime from, DateTime to, CancellationToken cancellationToken) =>
        ReadCostPointsAsync(list, row => row.Key == id, from, to, cancellationToken);

    private Task<IReadOnlyList<MonitorCostPoint>> ReadCostPointsAsync(MonitorList list, Func<StoredGroup, bool> wanted, DateTime from, DateTime to, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorCostPoint>>(
            () => ReplyBuilder.HistoryDimensionOf(list) is not { } dimension
                ? []
                : _store.Groups(Server, dimension, from, to)
                    .Where(wanted)
                    .OrderBy(row => row.Time)
                    .Select(row => new MonitorCostPoint(row.Time, row.MainMs, row.WorkerMs, row.PeakMs, row.CallsPerFrame) { BytesPerSecond = row.BytesPerSecond })
                    .ToList(),
            cancellationToken);

    public async Task<byte[]?> ChartImageAsync(MonitorChart chart, DateTime from, DateTime to, int width, int height, CancellationToken cancellationToken)
    {
        DateTime oldest = DateTime.UtcNow - _settings.Retention;
        DateTime start = from < oldest ? oldest : from;

        if (to <= start)
            return null;

        // One at a time, because on Torch this is in the game's process, and a plugin could ask for dozens at once.
        await s_imageGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await Task.Run(() => ChartImages.Draw(_charts, Server, chart, start, to, width, height), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s_imageGate.Release();
        }
    }

    public Task<IReadOnlyList<MonitorEvent>> EventsAsync(DateTime from, DateTime to, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorEvent>>(() => _store.Markers(Server, from, to).Select(marker => MonitorApi.ToEvent(Server, marker)).ToList(), cancellationToken);

    public async Task<MonitorComparison> CompareAsync(MonitorRange before, MonitorRange after, IReadOnlyCollection<MonitorList> lists, CancellationToken cancellationToken)
    {
        MonitorReply first = await CostsAsync(before.From, before.To, lists, cancellationToken).ConfigureAwait(false);
        MonitorReply second = await CostsAsync(after.From, after.To, lists, cancellationToken).ConfigureAwait(false);

        return ToolBuilder.Compare(first, second);
    }

    public Task<MonitorReply> MeasureAsync(MonitorRunOptions options, CancellationToken cancellationToken) =>
        RequestAsync(ToolBuilder.RunRequest(Server, options), cancellationToken);

    public MonitorRunProgress? CurrentRun()
    {
        (string RequestedBy, DateTime Ends)? known = _runs.Current(Server);

        // The worker knows how long is left, and what started here knows who asked.
        if (Live != null)
        {
            MonitorRunProgress? live = Live.CurrentRun();

            return live != null && known is { RequestedBy.Length: > 0 } ? live with { RequestedBy = known.Value.RequestedBy } : live;
        }

        if (known is not { } run)
            return null;

        return new MonitorRunProgress(run.RequestedBy, Math.Max(0, (int)Math.Ceiling((run.Ends - DateTime.UtcNow).TotalSeconds)));
    }

    public Task<IReadOnlyList<MonitorSavedRun>> SavedRunsAsync(CancellationToken cancellationToken) =>
        Task.Run(() => _api.SavedRuns(Server), cancellationToken);

    public Task<MonitorReply> SavedRunAsync(string? id, IReadOnlyCollection<MonitorList> lists, CancellationToken cancellationToken) =>
        RequestAsync(ToolBuilder.SavedRunRequest(Server, id, lists), cancellationToken);

    public Task<IReadOnlyList<MonitorIncident>> IncidentsAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorIncident>>(
            () => _incidents.List(Server)
                .Select(incident => new MonitorIncident(Server, incident.Id, incident.Opened, incident.Closed, incident.Reason, incident.LowestSim))
                .ToList(),
            cancellationToken);

    public async Task<IReadOnlyList<MonitorMethod>> FindMethodsAsync(string text, CancellationToken cancellationToken)
    {
        await _findPace.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (Target() is not { } target)
            return [];

        try
        {
            DrillRootsResponse found = await _servers.DrillRootsAsync(target.Id, new DrillRootsRequest { Text = text }, cancellationToken).ConfigureAwait(false);
            return found.Roots.Select(ToolBuilder.ToMethod).ToList();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    public async Task<MonitorInside> LookInsideAsync(string methodId, int seconds, string requestedBy, CancellationToken cancellationToken)
    {
        if (Target() is not { } target)
            return ToolBuilder.InsideRefused(MonitorApi.NotConnected);

        if (ClaimInspect(requestedBy, seconds, ToolBuilder.MaxInsideSeconds) is { } problem)
            return ToolBuilder.InsideRefused(problem);

        _api.NoteRequest(requestedBy, "look inside a method for " + seconds + " s");

        try
        {
            DrillStartResponse started = await _servers.StartDrillAsync(target.Id, new DrillStartRequest { Id = methodId, Seconds = seconds }, cancellationToken).ConfigureAwait(false);

            if (!started.Started)
                return ToolBuilder.InsideRefused(started.Message ?? "It didn't start.");

            DrillResultResponse result = await ToolBuilder.PollAsync(
                () => _servers.DrillResultAsync(target.Id, new DrillResultRequest(), cancellationToken),
                drill => drill.State == RunState.Running,
                s_drillPoll,
                cancellationToken).ConfigureAwait(false);

            return ToolBuilder.ToInside(result, started.Number);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ToolBuilder.InsideFailed("The server stopped answering: " + exception.Message);
        }
    }

    public Task<IReadOnlyList<MonitorPatchedMethod>> PatchesAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorPatchedMethod>>(
            async () =>
            {
                if (Target() is not { } target)
                    return [];

                var methods = new List<PatchedMethodRow>();

                try
                {
                    while (true)
                    {
                        PatchListResponse page = await _servers.ListPatchesAsync(target.Id, new PatchListRequest { Skip = methods.Count }, cancellationToken).ConfigureAwait(false);
                        methods.AddRange(page.Methods);

                        if (page.Methods.Count == 0 || methods.Count >= page.Total)
                            return methods.Select(ToolBuilder.ToPatched).ToList();
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return [];
                }
            },
            cancellationToken);

    public async Task<MonitorAudit> AuditAsync(IReadOnlyCollection<string> methods, int seconds, string requestedBy, CancellationToken cancellationToken)
    {
        if (Target() is not { } target)
            return ToolBuilder.AuditRefused(MonitorApi.NotConnected);

        if (ClaimInspect(requestedBy, seconds, ToolBuilder.MaxAuditSeconds) is { } problem)
            return ToolBuilder.AuditRefused(problem);

        _api.NoteRequest(requestedBy, "audit " + methods.Count + " patched methods for " + seconds + " s");

        try
        {
            var request = new AuditStartRequest { Methods = [.. methods], Seconds = seconds };
            AuditStartResponse started = await _servers.StartAuditAsync(target.Id, request, cancellationToken).ConfigureAwait(false);

            if (!started.Started)
                return ToolBuilder.AuditRefused(started.Message ?? "The audit didn't start.");

            AuditResultResponse result = await ToolBuilder.PollAsync(
                () => _servers.AuditResultAsync(target.Id, new AuditResultRequest(), cancellationToken),
                audit => audit.State == RunState.Running,
                s_auditPoll,
                cancellationToken).ConfigureAwait(false);

            return ToolBuilder.ToAudit(result, started.Number);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ToolBuilder.AuditFailed("The server stopped answering: " + exception.Message);
        }
    }

    private MonitorServer? Target() => _servers.Connected().FirstOrDefault(server => server.HistoryId == Server);

    // These rebuild methods on a running server, so a plugin only gets one a minute on each server.
    private string? ClaimInspect(string requestedBy, int seconds, int mostSeconds)
    {
        lock (_inspectGate)
        {
            DateTime now = DateTime.UtcNow;
            DateTime next = _nextInspect.GetValueOrDefault(Server);

            if (ToolBuilder.InspectProblem(requestedBy, seconds, mostSeconds, next, now) is { } problem)
                return problem;

            _nextInspect[Server] = now + ToolBuilder.PluginGap;
            return null;
        }
    }
}
