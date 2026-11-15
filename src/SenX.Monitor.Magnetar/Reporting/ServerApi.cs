using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Sandbox;
using SenX.Monitor.Api;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Patching;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>
/// The tools for other plugins on a Magnetar server. They only ever answer about this server, from the
/// worker's own rings, and never reach Quasar. Rings are read off the caller's thread, and names, patching
/// and starting a run go to the game thread. The game thread never waits for anything in here: what it
/// shares with other threads is swapped whole, without a lock. Limits match Quasar's, so plugins can't
/// keep costing the server.
/// </summary>
internal sealed class ServerApi : IMonitorServerApi
{
    public static readonly ServerApi Instance = new();

    private const int RowsPerList = ReplyBuilder.RowsPerList;
    private const int MostTop = 100;
    private const int MaxTimelinePoints = 1200;
    private const int MaxRunSeconds = 600;
    private const int MostQueuedEvents = 256;

    // More than the totals the ring holds, even if all of them were kept.
    private const int KeptSamples = 1 << 8;

    private static readonly TimeSpan s_ringReach = TimeSpan.FromHours(1);
    private static readonly TimeSpan s_drillPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_auditPoll = TimeSpan.FromSeconds(2);

    // Long enough for the worker to total a big world after the measuring ends.
    private static readonly TimeSpan s_runGrace = TimeSpan.FromMinutes(2);

    // A hitch can outlast any wait, and a world unloading can drop what's queued, so nothing waits on the game for ever.
    private static readonly TimeSpan s_gameThreadLimit = TimeSpan.FromSeconds(30);

    // Names barely change, and asking the game for them on every call would cost it a little each time.
    private static readonly TimeSpan s_namesFor = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<(HistoryDimension, long), string> s_names = new();
    private static DateTime s_namesUntil;
    private static int s_gameThreadCalls;

    private readonly object _inspectGate = new();
    private readonly SampleRate _sampleRate = new();
    private readonly long[] _keptUnits = new long[KeptSamples];
    private readonly Pace _findPace = new(ToolBuilder.FindGap);
    private readonly ConcurrentQueue<Action> _events = new();
    private int _keptCount;
    private int _pumping;
    private ActiveRun? _active;
    private LastRun? _lastRun;
    private long _nextRunTicks;
    private DateTime _nextInspect;
    private long _seenMarkers;
    private long _seenSweeps;
    private long _seenRollups;

    private sealed class ActiveRun
    {
        public ActiveRun(RunStartRequest request, string requestedBy, DateTime now)
        {
            Request = request;
            RequestedBy = requestedBy;
            Ends = now + TimeSpan.FromSeconds(request.Seconds);
        }

        public RunStartRequest Request { get; }

        public string RequestedBy { get; }

        /// <summary>Set on the game thread as soon as the run has started.</summary>
        public long RunId;

        public DateTime Ends { get; }

        /// <summary>When this is past, the run isn't coming back.</summary>
        public DateTime GiveUpAt => Ends + s_runGrace;

        public string? Problem { get; set; }

        public TimeSpan? RetryAfter { get; set; }

        public TaskCompletionSource<RunResultResponse?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Swapped whole, so a reader gets one run's result and id together.
    private sealed class LastRun
    {
        public LastRun(RunResultResponse result, string id, DateTime time)
        {
            Result = result;
            Id = id;
            Time = time;
        }

        public RunResultResponse Result { get; }

        public string Id { get; }

        public DateTime Time { get; }
    }

    private sealed class Totals
    {
        public long Key;
        public long Representative;
        public double Main;
        public double Worker;
        public double Peak;
        public double Calls;
        public double Bytes;
        public int Seen;
        public long Blocks;
        public long Grids;
    }

    /// <summary>How a stretch of seconds went, the way Quasar's history would say it.</summary>
    private sealed unsafe class Stretch
    {
        private readonly double[] _scopes = new double[Scope.Count];
        private double _sim;
        private double _worst;
        private double _available;
        private long _frames;
        private long _units;
        private int _points;

        public double Seconds => _units / (double)Clock.UnitsPerSecond;

        public long Frames => _frames;

        public void Add(ref RollupRecord row)
        {
            double frames = Math.Max(row.Frames, 1U);

            for (int scope = 1; scope < Scope.Count; scope++)
                _scopes[scope] += row.ScopeUnits[scope] / frames / 10000d;

            _sim += row.SimSpeed;
            _worst = Math.Max(_worst, row.FrameMax / 10000d);
            _available += Math.Max(0d, FrameSplit.BudgetMs - (row.ScopeUnits[Scope.Frame] / frames / 10000d));
            _frames += row.SimulatedFrames;
            _units += row.WindowUnits;
            _points++;
        }

        public MonitorFrame? Frame =>
            _points == 0 ? null : new MonitorFrame(_sim / _points, _scopes[Scope.Frame] / _points, _worst, _available / _points);

        public IReadOnlyDictionary<string, double> Parts
        {
            get
            {
                var parts = new Dictionary<string, double>(FrameSplit.Names.Length);

                if (_points == 0)
                    return parts;

                double[] split = FrameSplit.Of(id => id < _scopes.Length ? _scopes[id] / _points : 0d);

                for (int part = 0; part < split.Length; part++)
                    parts[FrameSplit.Names[part]] = split[part];

                return parts;
            }
        }
    }

    public event Action<MonitorSample>? SampleRecorded;

    public event Action<MonitorRunFinished>? RunFinished;

    public event Action<MonitorEvent>? EventRecorded;

    /// <summary>Quasar keeps incidents, so this never happens here.</summary>
    public event Action<MonitorIncident>? IncidentChanged
    {
        add { }
        remove { }
    }

    public string Server => string.Empty;

    public int Version => 3;

    public MonitorLatest? Latest()
    {
        if (!FrameEngine.TryReadLatest(out RollupRecord row))
            return null;

        var stretch = new Stretch();
        stretch.Add(ref row);

        return new MonitorLatest(TimeOf(row.UnitsSinceStart), stretch.Frame!, row.FrameP99 / 10000d, stretch.Parts, LatestWorld())
        {
            Network = new MonitorNetwork(row.SentKb, row.ReceivedKb, row.RpcsIn),
        };
    }

    public MonitorStatus Status()
    {
        DateTime? started = HistoryWriter.StartedUtcTicks == 0L ? null : new DateTime(HistoryWriter.StartedUtcTicks, DateTimeKind.Utc);
        DateTime? last = FrameEngine.TryReadLatest(out RollupRecord row) ? TimeOf(row.UnitsSinceStart) : null;

        return new MonitorStatus(true, started, last, Capture.Active, null, s_ringReach);
    }

    private static unsafe MonitorWorldPoint? LatestWorld()
    {
        CensusRecord census = default;
        return Census.Rows.TryCopy(Census.Rows.Head - 1L, (byte*)&census) ? ToWorld(census) : null;
    }

    private static MonitorWorldPoint ToWorld(in CensusRecord row) =>
        new(TimeOf(row.UnitsSinceStart), row.OnlinePlayers, row.Grids, row.Blocks, row.Pcu, row.PingMeanTenths / 10d)
        {
            ConcealedGrids = row.ConcealedGrids,
            ConcealedBlocks = row.ConcealedBlocks,
            StaticGrids = row.StaticGrids,
            Asteroids = row.Asteroids,
            Planets = row.Planets,
            CpuPercent = row.CpuTenths / 10d,
            MemoryMb = row.WorkingSetMb,
            ManagedMemoryMb = row.ManagedMb,
            Uptime = TimeSpan.FromTicks(row.UnitsSinceStart),
        };

    private static DateTime TimeOf(long unitsSinceStart) => new(HistoryWriter.StartedUtcTicks + unitsSinceStart, DateTimeKind.Utc);

    private static long UnitsOf(DateTime time) => time.Ticks - HistoryWriter.StartedUtcTicks;

    public Task<IReadOnlyList<MonitorPlayerNow>> PlayersAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadPlayersAsync(cancellationToken), cancellationToken);

    private static async Task<IReadOnlyList<MonitorPlayerNow>> ReadPlayersAsync(CancellationToken cancellationToken)
    {
        CopyDetail(out CensusPlayer[] players, out _);

        var wanted = new List<(HistoryDimension, long, long)>(players.Length * 2);

        foreach (CensusPlayer player in players)
        {
            wanted.Add((HistoryDimension.Owners, player.IdentityId, 0L));
            wanted.Add((HistoryDimension.Factions, player.FactionId, 0L));
        }

        Dictionary<(HistoryDimension, long), string> names = await NamesAsync(wanted, cancellationToken).ConfigureAwait(false);

        return players
            .Select(player => new MonitorPlayerNow(
                names[(HistoryDimension.Owners, player.IdentityId)],
                player.IdentityId,
                names[(HistoryDimension.Factions, player.FactionId)],
                player.PingMs,
                player.SentBytesPerSecond / 1024d,
                player.ReceivedBytesPerSecond / 1024d,
                player.RpcsPerSecond,
                player.QueuedBytes / 1024d,
                player.FailedSends))
            .ToList();
    }

    public Task<IReadOnlyList<MonitorFactionNow>> FactionsAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadFactionsAsync(cancellationToken), cancellationToken);

    private static async Task<IReadOnlyList<MonitorFactionNow>> ReadFactionsAsync(CancellationToken cancellationToken)
    {
        CopyDetail(out _, out CensusFaction[] factions);

        Dictionary<(HistoryDimension, long), string> names = await NamesAsync(factions.Select(faction => (HistoryDimension.Factions, faction.FactionId, 0L)).ToList(), cancellationToken).ConfigureAwait(false);

        return factions
            .Select(faction => new MonitorFactionNow(names[(HistoryDimension.Factions, faction.FactionId)], faction.FactionId, faction.OnlineMembers, faction.Grids, faction.Blocks, faction.Pcu))
            .ToList();
    }

    private static unsafe void CopyDetail(out CensusPlayer[] players, out CensusFaction[] factions)
    {
        var allPlayers = new CensusPlayer[Census.MaxPlayers];
        var allFactions = new CensusFaction[Census.MaxFactions];
        int playerCount;
        int factionCount;

        fixed (CensusPlayer* playerRows = allPlayers)
        fixed (CensusFaction* factionRows = allFactions)
        {
            Census.TryCopyDetail(playerRows, factionRows, out playerCount, out factionCount, out _, out _);
        }

        players = allPlayers.Take(playerCount).ToArray();
        factions = allFactions.Take(factionCount).ToArray();
    }

    public Task<IReadOnlyList<MonitorRow>> TopAsync(MonitorList list, int count, CancellationToken cancellationToken) =>
        Task.Run(() => ReadTopAsync(list, count, cancellationToken), cancellationToken);

    private static async Task<IReadOnlyList<MonitorRow>> ReadTopAsync(MonitorList list, int count, CancellationToken cancellationToken)
    {
        if (!(ReplyBuilder.HistoryDimensionOf(list) is { } dimension) || !(LagReport.LatestSweep((GroupDimension)dimension, out _) is { } latest))
            return new List<MonitorRow>();

        List<GroupRecord> rows = latest.Take(Math.Min(Math.Max(count, 1), MostTop)).ToList();
        Dictionary<(HistoryDimension, long), string> names = await NamesAsync(rows.Select(row => (dimension, row.Key, row.Representative)).ToList(), cancellationToken).ConfigureAwait(false);

        return rows.Select(row => ToRow(list, row, names[(dimension, row.Key)])).ToList();
    }

    private static MonitorRow ToRow(MonitorList list, in GroupRecord row, string name) =>
        new(name, ReplyBuilder.StableId(list, row.Key), row.MainUnits / 10000d, row.WorkerUnits / 10000d, row.MaxUnits / 10000d, row.CentiCallsPerFrame / 100d, 1d)
        {
            Thread = MonitorThreads.Of(row.MainUnits, row.WorkerUnits),
            BytesPerSecond = row.BytesPerSecond,
            Blocks = row.Blocks,
            Grids = row.Grids,
        };

    public Task<IReadOnlyList<MonitorSample>> SamplesAsync(DateTime from, DateTime to, IReadOnlyCollection<MonitorList> lists, int top, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorSample>>(
            async () =>
            {
                List<List<GroupRecord>> sweeps = ReadSweeps(Math.Max(UnitsOf(from), 0L), UnitsOf(to), KeptTimes());

                return await BuildSamplesAsync(sweeps, lists.Count == 0 ? ReplyBuilder.ListsInHistory : lists, Math.Min(Math.Max(top, 1), RowsPerList), cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    // The game thread adds to these, and doesn't wait for anyone reading them.
    private HashSet<long> KeptTimes()
    {
        int count = Volatile.Read(ref _keptCount);
        var kept = new HashSet<long>();

        for (int index = Math.Max(0, count - KeptSamples); index < count; index++)
            kept.Add(Volatile.Read(ref _keptUnits[index & (KeptSamples - 1)]));

        return kept;
    }

    // The sample before this one, or one normal step back for the first there's ever been.
    private long KeptBefore(long units)
    {
        long before = long.MinValue;

        foreach (long kept in KeptTimes())
        {
            if (kept < units && kept > before)
                before = kept;
        }

        return before == long.MinValue ? units - SampleRate.Normal.Ticks : before;
    }

    // Each list keeps its rows in order of cost, so the top few are just the first few.
    private async Task<List<MonitorSample>> BuildSamplesAsync(List<List<GroupRecord>> sweeps, IReadOnlyCollection<MonitorList> lists, int top, CancellationToken cancellationToken)
    {
        var samples = new List<MonitorSample>(sweeps.Count);

        if (sweeps.Count == 0)
            return samples;

        long[] times = sweeps.Select(sweep => sweep[0].UnitsSinceStart).ToArray();
        Stretch[] stretches = ReadStretches(times, KeptBefore(times[0]));
        List<MonitorWorldPoint> world = ReadWorld(0L, times[times.Length - 1]);

        var chosen = new List<(MonitorList List, HistoryDimension? Dimension, List<GroupRecord> Rows)>[sweeps.Count];
        var named = new HashSet<(HistoryDimension, long)>();
        var wanted = new List<(HistoryDimension, long, long)>();

        for (int sweep = 0; sweep < sweeps.Count; sweep++)
        {
            chosen[sweep] = new List<(MonitorList, HistoryDimension?, List<GroupRecord>)>(lists.Count);

            foreach (MonitorList list in lists)
            {
                HistoryDimension? dimension = ReplyBuilder.HistoryDimensionOf(list);
                List<GroupRecord> rows = dimension == null
                    ? new List<GroupRecord>()
                    : sweeps[sweep].Where(row => row.Dimension == (GroupDimension)dimension.Value).Take(top).ToList();

                chosen[sweep].Add((list, dimension, rows));

                foreach (GroupRecord row in rows)
                {
                    if (named.Add((dimension!.Value, row.Key)))
                        wanted.Add((dimension.Value, row.Key, row.Representative));
                }
            }
        }

        Dictionary<(HistoryDimension, long), string> names = await NamesAsync(wanted, cancellationToken).ConfigureAwait(false);

        for (int sweep = 0; sweep < sweeps.Count; sweep++)
        {
            DateTime time = TimeOf(times[sweep]);

            samples.Add(new MonitorSample(
                string.Empty,
                time,
                stretches[sweep].Frame,
                stretches[sweep].Parts,
                world.LastOrDefault(point => point.Time <= time),
                chosen[sweep].Select(entry => entry.Dimension is { } dimension
                    ? new MonitorListReply { List = entry.List, Available = true, Rows = entry.Rows.Select(row => ToRow(entry.List, row, names[(dimension, row.Key)])).ToList() }
                    : new MonitorListReply { List = entry.List, Available = false, Why = ReplyBuilder.NotInHistory }).ToList())
            {
                Seconds = stretches[sweep].Seconds,
                Frames = stretches[sweep].Frames,
            });
        }

        return samples;
    }

    // Rows of one total share a time, and only whole totals are read.
    private static unsafe List<List<GroupRecord>> ReadSweeps(long start, long end, HashSet<long> kept)
    {
        var sweeps = new List<List<GroupRecord>>();
        RecordRing ring = Sweep.Rows;
        long whole = Sweep.WholeRows;
        GroupRecord row = default;

        // Once the ring has wrapped, the oldest total has usually lost its first rows, so it isn't whole.
        long cut = ring.Oldest > 0L && ring.TryCopy(ring.Oldest, (byte*)&row) ? row.UnitsSinceStart : -1L;

        for (long sequence = ring.Oldest; sequence < whole; sequence++)
        {
            if (!ring.TryCopy(sequence, (byte*)&row) || row.UnitsSinceStart == cut || row.UnitsSinceStart < start || row.UnitsSinceStart > end || !kept.Contains(row.UnitsSinceStart))
                continue;

            if (sweeps.Count == 0 || sweeps[sweeps.Count - 1][0].UnitsSinceStart != row.UnitsSinceStart)
                sweeps.Add(new List<GroupRecord>());

            sweeps[sweeps.Count - 1].Add(row);
        }

        return sweeps;
    }

    // What the seconds since the sample before say about each sample.
    private static unsafe Stretch[] ReadStretches(long[] times, long start)
    {
        var stretches = new Stretch[times.Length];

        for (int index = 0; index < stretches.Length; index++)
            stretches[index] = new Stretch();

        RecordRing ring = FrameEngine.Rollups;
        RollupRecord row = default;
        int at = 0;

        for (long sequence = ring.Oldest; sequence < ring.Head; sequence++)
        {
            if (!ring.TryCopy(sequence, (byte*)&row) || row.UnitsSinceStart <= start)
                continue;

            while (at < times.Length && row.UnitsSinceStart > times[at])
                at++;

            if (at == times.Length)
                break;

            stretches[at].Add(ref row);
        }

        return stretches;
    }

    public async Task<MonitorHistory> HistoryAsync(DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        // A list history doesn't keep costs nothing, and this leaves the figures across the range.
        MonitorReply reply = await RequestAsync(ToolBuilder.CostsRequest(Server, from, to, new[] { MonitorList.Clusters }), cancellationToken).ConfigureAwait(false);

        return new MonitorHistory(new MonitorRange(reply.From, reply.To), reply.Frame, reply.Timeline, reply.World, reply.Events);
    }

    public Task<MonitorReply> CostsAsync(DateTime from, DateTime to, IReadOnlyCollection<MonitorList> lists, CancellationToken cancellationToken) =>
        RequestAsync(ToolBuilder.CostsRequest(Server, from, to, lists), cancellationToken);

    public Task<IReadOnlyList<MonitorCostPoint>> CostHistoryAsync(MonitorList list, string name, DateTime from, DateTime to, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorCostPoint>>(
            async () =>
            {
                if (!(ReplyBuilder.HistoryDimensionOf(list) is { } dimension))
                    return new List<MonitorCostPoint>();

                Dictionary<long, long> keys = ReadKeys(dimension, Math.Max(UnitsOf(from), 0L), UnitsOf(to));
                Dictionary<(HistoryDimension, long), string> names = await NamesAsync(keys.Select(key => (dimension, key.Key, key.Value)).ToList(), cancellationToken).ConfigureAwait(false);

                return ReadCostPoints(dimension, Math.Max(UnitsOf(from), 0L), UnitsOf(to), new HashSet<long>(keys.Keys.Where(key => names[(dimension, key)] == name)));
            },
            cancellationToken);

    public Task<IReadOnlyList<MonitorCostPoint>> CostHistoryAsync(MonitorList list, long id, DateTime from, DateTime to, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorCostPoint>>(
            () => ReplyBuilder.HistoryDimensionOf(list) is { } dimension
                ? ReadCostPoints(dimension, Math.Max(UnitsOf(from), 0L), UnitsOf(to), new HashSet<long> { id })
                : new List<MonitorCostPoint>(),
            cancellationToken);

    // Each key with its representative, which is what a name needs.
    private static unsafe Dictionary<long, long> ReadKeys(HistoryDimension dimension, long start, long end)
    {
        var keys = new Dictionary<long, long>();
        RecordRing ring = Sweep.Rows;
        long whole = Sweep.WholeRows;
        GroupRecord row = default;

        for (long sequence = ring.Oldest; sequence < whole; sequence++)
        {
            if (ring.TryCopy(sequence, (byte*)&row) && row.Dimension == (GroupDimension)dimension && row.UnitsSinceStart >= start && row.UnitsSinceStart <= end)
                keys[row.Key] = row.Representative;
        }

        return keys;
    }

    private static unsafe List<MonitorCostPoint> ReadCostPoints(HistoryDimension dimension, long start, long end, HashSet<long> wanted)
    {
        var points = new List<MonitorCostPoint>();
        RecordRing ring = Sweep.Rows;
        long whole = Sweep.WholeRows;
        GroupRecord row = default;

        for (long sequence = ring.Oldest; sequence < whole; sequence++)
        {
            if (!ring.TryCopy(sequence, (byte*)&row) || row.Dimension != (GroupDimension)dimension || row.UnitsSinceStart < start || row.UnitsSinceStart > end || !wanted.Contains(row.Key))
                continue;

            points.Add(new MonitorCostPoint(TimeOf(row.UnitsSinceStart), row.MainUnits / 10000d, row.WorkerUnits / 10000d, row.MaxUnits / 10000d, row.CentiCallsPerFrame / 100d)
            {
                BytesPerSecond = row.BytesPerSecond,
            });
        }

        return points;
    }

    // Only Quasar and Torch hold the history, and drawing here would add to the game's own process.
    public Task<byte[]?> ChartImageAsync(MonitorChart chart, DateTime from, DateTime to, int width, int height, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]?>(null);

    public Task<IReadOnlyList<MonitorEvent>> EventsAsync(DateTime from, DateTime to, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorEvent>>(() => ReadAllEvents(Math.Max(UnitsOf(from), 0L), UnitsOf(to)), cancellationToken);

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
        if (!Capture.Active)
            return null;

        return new MonitorRunProgress(Volatile.Read(ref _active)?.RequestedBy ?? string.Empty, Capture.SecondsLeft);
    }

    public Task<IReadOnlyList<MonitorSavedRun>> SavedRunsAsync(CancellationToken cancellationToken)
    {
        LastRun? last = Volatile.Read(ref _lastRun);

        return Task.FromResult<IReadOnlyList<MonitorSavedRun>>(last == null
            ? new List<MonitorSavedRun>()
            : new List<MonitorSavedRun> { new(last.Id, last.Time, last.Result.Seconds, last.Result.SimSpeed, last.Result.AverageFrameMs) });
    }

    public Task<MonitorReply> SavedRunAsync(string? id, IReadOnlyCollection<MonitorList> lists, CancellationToken cancellationToken) =>
        RequestAsync(ToolBuilder.SavedRunRequest(Server, id, lists), cancellationToken);

    public Task<IReadOnlyList<MonitorIncident>> IncidentsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MonitorIncident>>(new List<MonitorIncident>());

    public async Task<IReadOnlyList<MonitorMethod>> FindMethodsAsync(string text, CancellationToken cancellationToken)
    {
        await _findPace.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            List<DrillRoot> found = await OnGameThreadAsync(() => DrillDown.Roots(text), cancellationToken).ConfigureAwait(false);
            return found.Select(ToolBuilder.ToMethod).ToList();
        }
        catch (Exception exception) when (!(exception is OperationCanceledException))
        {
            MonitorLog.Warning("Monitor could not search methods for a plugin: " + exception.Message);
            return new List<MonitorMethod>();
        }
    }

    public async Task<MonitorInside> LookInsideAsync(string methodId, int seconds, string requestedBy, CancellationToken cancellationToken)
    {
        if (ClaimInspect(requestedBy, seconds, ToolBuilder.MaxInsideSeconds) is { } problem)
            return ToolBuilder.InsideRefused(problem);

        MonitorLog.Info(requestedBy + " asked Monitor to look inside a method for " + seconds + " s.");

        try
        {
            DrillStartResponse started = await OnGameThreadAsync(() => DrillDown.Start(new DrillStartRequest { Id = methodId, Seconds = seconds }), cancellationToken).ConfigureAwait(false);

            if (!started.Started)
                return ToolBuilder.InsideRefused(started.Message ?? "It didn't start.");

            DrillResultResponse result = await ToolBuilder.PollAsync(
                () => Task.FromResult(JsonConvert.DeserializeObject<DrillResultResponse>(DrillDown.ResultJson()) ?? new DrillResultResponse()),
                drill => drill.State == RunState.Running,
                s_drillPoll,
                cancellationToken).ConfigureAwait(false);

            return ToolBuilder.ToInside(result, started.Number);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException))
        {
            MonitorLog.Error("Monitor could not look inside a method for a plugin.", exception);
            return ToolBuilder.InsideFailed("It failed. The details are in the server log.");
        }
    }

    public Task<IReadOnlyList<MonitorPatchedMethod>> PatchesAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<MonitorPatchedMethod>>(
            () =>
            {
                var methods = new List<PatchedMethodRow>();

                try
                {
                    while (true)
                    {
                        PatchListResponse page = PatchList.Page(methods.Count);
                        methods.AddRange(page.Methods);

                        if (page.Methods.Count == 0 || methods.Count >= page.Total)
                            return methods.Select(ToolBuilder.ToPatched).ToList();
                    }
                }
                catch (Exception exception)
                {
                    MonitorLog.Warning("Monitor could not list patches for a plugin: " + exception.Message);
                    return new List<MonitorPatchedMethod>();
                }
            },
            cancellationToken);

    public async Task<MonitorAudit> AuditAsync(IReadOnlyCollection<string> methods, int seconds, string requestedBy, CancellationToken cancellationToken)
    {
        if (ClaimInspect(requestedBy, seconds, ToolBuilder.MaxAuditSeconds) is { } problem)
            return ToolBuilder.AuditRefused(problem);

        MonitorLog.Info(requestedBy + " asked Monitor to audit " + methods.Count + " patched methods for " + seconds + " s.");

        try
        {
            var request = new AuditStartRequest { Methods = methods.ToList(), Seconds = seconds };
            AuditStartResponse started = await OnGameThreadAsync(() => PatchAudit.Start(request), cancellationToken).ConfigureAwait(false);

            if (!started.Started)
                return ToolBuilder.AuditRefused(started.Message ?? "The audit didn't start.");

            AuditResultResponse result = await ToolBuilder.PollAsync(
                () => Task.FromResult(JsonConvert.DeserializeObject<AuditResultResponse>(PatchAudit.ResultJson()) ?? new AuditResultResponse()),
                audit => audit.State == RunState.Running,
                s_auditPoll,
                cancellationToken).ConfigureAwait(false);

            return ToolBuilder.ToAudit(result, started.Number);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException))
        {
            MonitorLog.Error("Monitor could not audit methods for a plugin.", exception);
            return ToolBuilder.AuditFailed("It failed. The details are in the server log.");
        }
    }

    // These rebuild methods on a running server, so plugins only get one a minute.
    private string? ClaimInspect(string requestedBy, int seconds, int mostSeconds)
    {
        lock (_inspectGate)
        {
            DateTime now = DateTime.UtcNow;
            string? problem = ToolBuilder.InspectProblem(requestedBy, seconds, mostSeconds, _nextInspect, now);

            if (problem == null)
                _nextInspect = now + ToolBuilder.PluginGap;

            return problem;
        }
    }

    public Task<MonitorReply> RequestAsync(MonitorRequest request, CancellationToken cancellationToken)
    {
        switch (request.Source)
        {
            case MonitorSource.Run:
                return RunAsync(request, cancellationToken);
            case MonitorSource.SavedRun:
                return Task.FromResult(FromLastRun(request));
            default:
                return Task.Run(() => FromHistoryAsync(request, cancellationToken), cancellationToken);
        }
    }

    private MonitorReply FromLastRun(MonitorRequest request)
    {
        LastRun? last = Volatile.Read(ref _lastRun);

        if (last == null || (request.RunId != null && request.RunId != last.Id))
            return ReplyBuilder.Refused(request, "This server only keeps its latest run, and there isn't one like that.");

        return ReplyBuilder.FromRun(request, last.Result, last.Id, last.Time);
    }

    private async Task<MonitorReply> RunAsync(MonitorRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RequestedBy))
            return ReplyBuilder.Refused(request, "A run needs to say who asked for it.");

        if (request.Seconds < 1 || request.Seconds > MaxRunSeconds)
            return ReplyBuilder.Refused(request, "Seconds must be from 1 to " + MaxRunSeconds + ".");

        var wanted = new RunStartRequest
        {
            RequestedBy = request.RequestedBy,
            Seconds = request.Seconds,
            GridId = request.GridId,
            Player = request.Player,
            Faction = request.Faction,
            BlockType = request.BlockType,
            Clusters = ReplyBuilder.ListsFor(request).Contains(MonitorList.Clusters),
        };

        DateTime now = DateTime.UtcNow;
        ActiveRun? active = Volatile.Read(ref _active);

        // One long past its time never came back, and it mustn't stand in the way of this one.
        if (active != null && now >= active.GiveUpAt)
        {
            GiveUpOn(active);
            active = null;
        }

        if (active == null)
        {
            long nextRun = Volatile.Read(ref _nextRunTicks);

            if (now.Ticks < nextRun)
                return ReplyBuilder.Refused(request, "Runs from plugins are limited, because each one costs the server.", new TimeSpan(nextRun - now.Ticks));

            var mine = new ActiveRun(wanted, request.RequestedBy, now);
            active = Interlocked.CompareExchange(ref _active, mine, null);

            if (active == null)
            {
                active = mine;
                MonitorLog.Info("Monitor is measuring for " + request.Seconds + " s, for " + request.RequestedBy + ".");

                // Not waited for, because the game can drop a queued start when a world unloads. The limit below covers that.
                _ = StartAsync(mine);
            }
        }

        if (!ReferenceEquals(active.Request, wanted) && !CanShare(active.Request, wanted))
            return ReplyBuilder.Refused(request, "A run that can't be shared is going.", ToolBuilder.RetryAfter(active.Ends, now));

        TimeSpan patience = active.GiveUpAt > now ? active.GiveUpAt - now : TimeSpan.Zero;
        Task finished = await Task.WhenAny(active.Done.Task, Task.Delay(patience, cancellationToken)).ConfigureAwait(false);

        if (finished != active.Done.Task)
        {
            // Stops the wait and nothing else. The run finishes without this caller.
            cancellationToken.ThrowIfCancellationRequested();

            GiveUpOn(active);
        }

        RunResultResponse? result = await active.Done.Task.ConfigureAwait(false);

        if (result == null)
            return ReplyBuilder.Refused(request, active.Problem ?? "The run didn't start.", active.RetryAfter);

        return result.State == RunState.Finished
            ? ReplyBuilder.FromRun(request, result, Volatile.Read(ref active.RunId).ToString(CultureInfo.InvariantCulture), DateTime.UtcNow)
            : ReplyBuilder.Failed(request, result.Problem ?? "The run failed. The details are in the server log.");
    }

    private static bool CanShare(RunStartRequest going, RunStartRequest wanted) =>
        !going.Narrows && !wanted.Narrows && (going.Clusters || !wanted.Clusters);

    // Something that never came back mustn't leave the next caller waiting on it.
    private void GiveUpOn(ActiveRun active)
    {
        if (Interlocked.CompareExchange(ref _active, null, active) == active)
            MonitorLog.Warning("Monitor gave up on a run for " + active.RequestedBy + ", because it never finished.");

        active.Done.TrySetResult(new RunResultResponse { State = RunState.Failed, Problem = "The run never finished." });
    }

    /// <summary>Monitor is shutting down, so nothing will finish this run. Anyone waiting hears that now.</summary>
    public void Stop()
    {
        if (Volatile.Read(ref _active) is { } active)
            GiveUpOn(active);
    }

    /// <summary>A run started, so plugins and player tests wait their turn after it. No lock, because this can be in the frame hook.</summary>
    public void NoteTriggeredRun(int seconds) =>
        Volatile.Write(ref _nextRunTicks, (DateTime.UtcNow + TimeSpan.FromSeconds(seconds) + ToolBuilder.PluginGap).Ticks);

    /// <summary>How long until another run is allowed.</summary>
    public TimeSpan UntilNextRun()
    {
        long left = Volatile.Read(ref _nextRunTicks) - DateTime.UtcNow.Ticks;
        return left > 0L ? new TimeSpan(left) : TimeSpan.Zero;
    }

    private async Task StartAsync(ActiveRun active)
    {
        try
        {
            // The id is set on the game thread, so the run can't finish before it's known to be this one.
            RunStartResponse started = await RunService.StartAsync(active.Request, id => Volatile.Write(ref active.RunId, id)).ConfigureAwait(false);

            if (started.Started)
                return;

            Interlocked.CompareExchange(ref _active, null, active);
            active.Problem = started.Message;
            active.RetryAfter = started.RetrySeconds > 0 ? TimeSpan.FromSeconds(started.RetrySeconds) : null;
            active.Done.TrySetResult(null);
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not start a run for a plugin.", exception);

            Interlocked.CompareExchange(ref _active, null, active);
            active.Problem = "The run didn't start. The details are in the server log.";
            active.Done.TrySetResult(null);
        }
    }

    /// <summary>Told about every run that finishes, whoever started it. On the game thread, so it takes no lock.</summary>
    public void OnRunFinished(long runId, RunResultResponse result)
    {
        ActiveRun? active = Volatile.Read(ref _active);
        ActiveRun? finished = active != null && Volatile.Read(ref active.RunId) == runId ? active : null;

        if (finished != null)
        {
            Interlocked.CompareExchange(ref _active, null, finished);
            result.RequestedBy = finished.RequestedBy;
        }

        if (result.State == RunState.Finished)
            Volatile.Write(ref _lastRun, new LastRun(result, runId.ToString(CultureInfo.InvariantCulture), DateTime.UtcNow));

        finished?.Done.TrySetResult(result);

        if (result.State == RunState.Finished)
            Raise(RunFinished, new MonitorRunFinished(string.Empty, runId.ToString(CultureInfo.InvariantCulture)));
    }

    private async Task<MonitorReply> FromHistoryAsync(MonitorRequest request, CancellationToken cancellationToken)
    {
        long start = Math.Max(UnitsOf(request.From), 0L);
        long end = UnitsOf(request.To);

        if (end <= start)
            return ReplyBuilder.Refused(request, "A range has to end after it starts, and this server's history only goes back about an hour.");

        var timeline = new List<MonitorPoint>();
        var stretch = new Stretch();
        ReadRollups(start, end, timeline, stretch);

        IReadOnlyList<MonitorListReply> lists;

        try
        {
            lists = await ReadListsAsync(request, start, end, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException))
        {
            MonitorLog.Warning("Monitor could not read history for a plugin: " + exception.Message);
            return ReplyBuilder.Failed(request, "It couldn't be read: " + exception.Message);
        }

        return new MonitorReply
        {
            State = MonitorReplyState.Finished,
            Source = MonitorSource.History,
            Accuracy = MonitorAccuracy.Background,
            From = TimeOf(start),
            To = TimeOf(end),
            Frame = stretch.Frame,
            PartsMs = stretch.Parts,
            Lists = lists,
            Timeline = Thin(timeline),
            World = ReadWorld(start, end),
            Events = ReadAllEvents(start, end),
        };
    }

    private static unsafe void ReadRollups(long start, long end, List<MonitorPoint> timeline, Stretch stretch)
    {
        RecordRing ring = FrameEngine.Rollups;
        RollupRecord row = default;

        for (long sequence = ring.Oldest; sequence < ring.Head; sequence++)
        {
            if (!ring.TryCopy(sequence, (byte*)&row) || row.UnitsSinceStart < start || row.UnitsSinceStart > end)
                continue;

            stretch.Add(ref row);

            double ms = row.ScopeUnits[Scope.Frame] / (double)Math.Max(row.Frames, 1U) / 10000d;

            timeline.Add(new MonitorPoint(TimeOf(row.UnitsSinceStart), row.SimSpeed, row.FrameP50 / 10000d, row.FrameP95 / 10000d, row.FrameP99 / 10000d, row.FrameMax / 10000d, Math.Max(0d, FrameSplit.BudgetMs - ms))
            {
                SentKb = row.SentKb,
                ReceivedKb = row.ReceivedKb,
            });
        }
    }

    private static List<MonitorPoint> Thin(List<MonitorPoint> points)
    {
        if (points.Count <= MaxTimelinePoints)
            return points;

        int stride = (int)Math.Ceiling(points.Count / (double)MaxTimelinePoints);
        return points.Where((_, index) => index % stride == 0).ToList();
    }

    private static unsafe List<MonitorWorldPoint> ReadWorld(long start, long end)
    {
        var rows = new List<MonitorWorldPoint>();
        RecordRing ring = Census.Rows;
        CensusRecord row = default;

        for (long sequence = ring.Oldest; sequence < ring.Head; sequence++)
        {
            if (ring.TryCopy(sequence, (byte*)&row) && row.UnitsSinceStart >= start && row.UnitsSinceStart <= end)
                rows.Add(ToWorld(row));
        }

        return rows;
    }

    // Markers, and the thread jumps that ride in the background totals, newest first. Reads only, so it never waits on the game thread.
    internal static List<MonitorEvent> ReadAllEvents(long start, long end)
    {
        List<MonitorEvent> events = ReadEvents(start, end);
        List<GroupRecord> jumps = ReadJumps(start, end);

        if (jumps.Count == 0)
            return events;

        events.AddRange(jumps.Select(jump => ToEvent(jump)));
        return events.OrderByDescending(row => row.Time).ToList();
    }

    // Named only from names already looked up for other reasons. Jumps cover every object, so asking the game
    // thread for each would be new work there. Until a name is known, the id stands in.
    internal static MonitorEvent ToEvent(in GroupRecord jump)
    {
        if (jump.JumpsNotListed > 0U)
            return new MonitorEvent(string.Empty, TimeOf(jump.UnitsSinceStart), MonitorEventKind.ThreadJump, ThreadTags.NotListedText(jump.JumpsNotListed));

        var list = (HistoryDimension)jump.JumpedList;
        string name = s_names.TryGetValue((list, jump.Key), out string? known) ? known : "#" + jump.Key.ToString(CultureInfo.InvariantCulture);

        return new MonitorEvent(string.Empty, TimeOf(jump.UnitsSinceStart), MonitorEventKind.ThreadJump, ThreadTags.JumpText(list, name, (MonitorThread)jump.JumpFrom, (MonitorThread)jump.JumpTo));
    }

    internal static unsafe List<GroupRecord> ReadJumps(long start, long end)
    {
        var rows = new List<GroupRecord>();
        RecordRing ring = Sweep.Rows;
        long whole = Sweep.WholeRows;
        GroupRecord row = default;

        for (long sequence = ring.Oldest; sequence < whole; sequence++)
        {
            if (ring.TryCopy(sequence, (byte*)&row) && row.Dimension == GroupDimension.ThreadJumps && row.UnitsSinceStart >= start && row.UnitsSinceStart <= end)
                rows.Add(row);
        }

        return rows;
    }

    private static unsafe List<MonitorEvent> ReadEvents(long start, long end)
    {
        var rows = new List<MonitorEvent>();
        RecordRing ring = Markers.Rows;
        MarkerRecord row = default;

        for (long sequence = ring.Oldest; sequence < ring.Head; sequence++)
        {
            if (ring.TryCopy(sequence, (byte*)&row) && row.UnitsSinceStart >= start && row.UnitsSinceStart <= end && (MarkerKind)row.Kind != MarkerKind.RunFinished)
                rows.Add(ToEvent(row));
        }

        rows.Reverse();
        return rows;
    }

    private static MonitorEvent ToEvent(in MarkerRecord row) => new(string.Empty, TimeOf(row.UnitsSinceStart), (MarkerKind)row.Kind switch
    {
        MarkerKind.LagTripped => MonitorEventKind.Lag,
        MarkerKind.RunStarted or MarkerKind.DetailChanged => MonitorEventKind.Measuring,
        MarkerKind.JoinHitch => MonitorEventKind.JoinHitch,
        _ => MonitorEventKind.Save,
    }, (MarkerKind)row.Kind switch
    {
        MarkerKind.LagTripped => "Lag noticed",
        MarkerKind.SaveFailed => "World save failed",
        MarkerKind.RunStarted => "Run for " + row.Value + " s",
        MarkerKind.DetailChanged => "Detail set to " + ((DetailMode)row.Value).ToString().ToLowerInvariant(),
        MarkerKind.JoinHitch => "Join hitch (" + row.Value + " ms)",
        _ => "World saved",
    });

    private static unsafe void ReadGroups(long start, long end, Dictionary<uint, Dictionary<long, Totals>> totals, Dictionary<uint, HashSet<long>> sweeps)
    {
        RecordRing ring = Sweep.Rows;
        GroupRecord row = default;

        long whole = Sweep.WholeRows;

        for (long sequence = ring.Oldest; sequence < whole; sequence++)
        {
            if (!ring.TryCopy(sequence, (byte*)&row) || row.UnitsSinceStart < start || row.UnitsSinceStart > end)
                continue;

            uint list = (uint)row.Dimension;

            if (!totals.TryGetValue(list, out Dictionary<long, Totals>? rows))
            {
                rows = new Dictionary<long, Totals>();
                totals[list] = rows;
                sweeps[list] = new HashSet<long>();
            }

            sweeps[list].Add(row.UnitsSinceStart);

            if (!rows.TryGetValue(row.Key, out Totals? total))
            {
                total = new Totals { Key = row.Key, Representative = row.Representative };
                rows[row.Key] = total;
            }

            total.Main += row.MainUnits / 10000d;
            total.Worker += row.WorkerUnits / 10000d;
            total.Calls += row.CentiCallsPerFrame / 100d;
            total.Bytes += row.BytesPerSecond;
            total.Peak = Math.Max(total.Peak, row.MaxUnits / 10000d);
            total.Blocks = row.Blocks;
            total.Grids = row.Grids;
            total.Seen++;
        }
    }

    // Background totals keep each sweep's top rows, so a row is averaged over the sweeps it was in.
    private static async Task<IReadOnlyList<MonitorListReply>> ReadListsAsync(MonitorRequest request, long start, long end, CancellationToken cancellationToken)
    {
        var totals = new Dictionary<uint, Dictionary<long, Totals>>();
        var sweeps = new Dictionary<uint, HashSet<long>>();
        ReadGroups(start, end, totals, sweeps);

        var wanted = new List<(MonitorList List, HistoryDimension Dimension, List<Totals> Rows, int Sweeps)>();
        var replies = new List<MonitorListReply>();

        foreach (MonitorList list in ReplyBuilder.ListsFor(request))
        {
            if (!(ReplyBuilder.HistoryDimensionOf(list) is { } dimension))
            {
                replies.Add(new MonitorListReply { List = list, Available = false, Why = ReplyBuilder.NotInHistory });
                continue;
            }

            if (!totals.TryGetValue((uint)dimension, out Dictionary<long, Totals>? rows))
            {
                replies.Add(new MonitorListReply { List = list, Available = true });
                continue;
            }

            wanted.Add((list, dimension, rows.Values.OrderByDescending(item => (item.Main + item.Worker) / item.Seen).ThenByDescending(item => item.Bytes / item.Seen).Take(ReplyBuilder.MostRows(list)).ToList(), sweeps[(uint)dimension].Count));
        }

        Dictionary<(HistoryDimension, long), string> names = await NamesAsync(wanted.SelectMany(list => list.Rows.Select(item => (list.Dimension, item.Key, item.Representative))).ToList(), cancellationToken).ConfigureAwait(false);

        foreach ((MonitorList list, HistoryDimension dimension, List<Totals> rows, int sweepCount) in wanted)
        {
            replies.Add(new MonitorListReply
            {
                List = list,
                Available = true,
                Rows = rows.Select(item => new MonitorRow(
                    names[(dimension, item.Key)],
                    ReplyBuilder.StableId(list, item.Key),
                    item.Main / item.Seen,
                    item.Worker / item.Seen,
                    item.Peak,
                    item.Calls / item.Seen,
                    item.Seen / (double)sweepCount)
                {
                    Thread = MonitorThreads.Of(item.Main / item.Seen, item.Worker / item.Seen),
                    BytesPerSecond = item.Bytes / item.Seen,
                    Blocks = item.Blocks,
                    Grids = item.Grids,
                }).ToList(),
            });
        }

        return replies;
    }

    // Names live in the game. A batch is capped, so a long list can't cost one frame much, and if the game
    // doesn't answer, rows keep their numbers, which beats no answer at all.
    private static async Task<Dictionary<(HistoryDimension, long), string>> NamesAsync(List<(HistoryDimension Kind, long Key, long Representative)> rows, CancellationToken cancellationToken)
    {
        DateTime now = DateTime.UtcNow;

        if (now >= s_namesUntil)
        {
            s_names.Clear();
            s_namesUntil = now + s_namesFor;
        }

        var names = new Dictionary<(HistoryDimension, long), string>();
        var queue = new List<(HistoryDimension Kind, long Key, long Representative)>();

        foreach ((HistoryDimension Kind, long Key, long Representative) row in rows)
        {
            (HistoryDimension, long) key = (row.Kind, row.Key);

            if (names.ContainsKey(key))
                continue;

            if (s_names.TryGetValue(key, out string? known))
            {
                names[key] = known;
                continue;
            }

            names[key] = row.Key.ToString(CultureInfo.InvariantCulture);
            queue.Add(row);
        }

        for (int at = 0; at < queue.Count; at += MonitorProtocol.MaxIdentityQueries)
        {
            List<(HistoryDimension Kind, long Key, long Representative)> batch = queue.Skip(at).Take(MonitorProtocol.MaxIdentityQueries).ToList();

            try
            {
                await OnGameThreadAsync(
                    () =>
                    {
                        foreach ((HistoryDimension kind, long key, long representative) in batch)
                            s_names[(kind, key)] = LagReport.NameFor(kind, key, representative);

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                break;
            }

            foreach ((HistoryDimension kind, long key, long _) in batch)
            {
                if (s_names.TryGetValue((kind, key), out string? name))
                    names[(kind, key)] = name;
            }
        }

        return names;
    }

    /// <summary>Runs this on the game thread and waits, but not for ever. Work that's given up on can still run later.</summary>
    /// <summary>How many times anything asked the game thread for work, for tests that prove a read never does.</summary>
    internal static int GameThreadCalls => Volatile.Read(ref s_gameThreadCalls);

    internal static async Task<T> OnGameThreadAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref s_gameThreadCalls);

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
            "Monitor API");

        Task finished = await Task.WhenAny(answer.Task, Task.Delay(s_gameThreadLimit, cancellationToken)).ConfigureAwait(false);

        if (finished == answer.Task)
            return await answer.Task.ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("The game thread didn't answer.");
    }

    /// <summary>Passes on what the rings gained. Called every frame on the game thread, and does nothing if nobody listens.</summary>
    public void Step()
    {
        StepMarkers();
        StepSamples();
    }

    private unsafe void StepMarkers()
    {
        RecordRing ring = Markers.Rows;
        long head = ring.Head;

        if (head == _seenMarkers)
            return;

        long first = Math.Max(_seenMarkers, ring.Oldest);
        _seenMarkers = head;

        if (EventRecorded == null)
            return;

        MarkerRecord row = default;

        for (long sequence = first; sequence < head; sequence++)
        {
            if (ring.TryCopy(sequence, (byte*)&row) && (MarkerKind)row.Kind != MarkerKind.RunFinished)
                Raise(EventRecorded, ToEvent(row));
        }
    }

    // Once for each total, so it costs nothing in a normal frame, and it doesn't allocate unless someone's listening.
    private unsafe void StepSamples()
    {
        long whole = Sweep.WholeRows;

        if (whole == _seenSweeps)
            return;

        long seen = _seenSweeps;
        _seenSweeps = whole;

        // A total's thread jumps go to listeners the way markers do, worked out off the game thread.
        if (EventRecorded != null && seen > 0L)
            Task.Run(() => RaiseJumps(seen, whole));

        GroupRecord total = default;

        if (!Sweep.Rows.TryCopy(whole - 1L, (byte*)&total))
            return;

        RecordRing ring = FrameEngine.Rollups;
        RollupRecord row = default;
        long head = ring.Head;

        for (long sequence = Math.Max(_seenRollups, ring.Oldest); sequence < head; sequence++)
        {
            if (ring.TryCopy(sequence, (byte*)&row))
                _sampleRate.NoteSimSpeed(TimeOf(row.UnitsSinceStart), row.SimSpeed);
        }

        _seenRollups = head;

        if (!_sampleRate.Keep(TimeOf(total.UnitsSinceStart)))
            return;

        _keptUnits[_keptCount & (KeptSamples - 1)] = total.UnitsSinceStart;
        Volatile.Write(ref _keptCount, _keptCount + 1);

        if (SampleRecorded != null)
        {
            long units = total.UnitsSinceStart;
            Task.Run(() => OfferSampleAsync(units));
        }
    }

    private unsafe void RaiseJumps(long from, long upTo)
    {
        RecordRing ring = Sweep.Rows;
        GroupRecord row = default;

        for (long sequence = Math.Max(from, ring.Oldest); sequence < upTo; sequence++)
        {
            if (ring.TryCopy(sequence, (byte*)&row) && row.Dimension == GroupDimension.ThreadJumps)
                Raise(EventRecorded, ToEvent(row));
        }
    }

    private async Task OfferSampleAsync(long units)
    {
        try
        {
            List<MonitorSample> samples = await BuildSamplesAsync(ReadSweeps(units, units, KeptTimes()), ReplyBuilder.ListsInHistory, RowsPerList, CancellationToken.None).ConfigureAwait(false);

            if (samples.Count > 0)
                Raise(SampleRecorded, samples[0]);
        }
        catch (Exception exception)
        {
            MonitorLog.Warning("Monitor could not build a sample for a plugin: " + exception.Message);
        }
    }

    // Someone else's code, so it runs off the game thread, one at a time, and a slow one only holds up other handlers.
    private void Raise<T>(Action<T>? handlers, T value)
    {
        if (handlers == null)
            return;

        _events.Enqueue(() => Deliver(handlers, value));

        // Someone has fallen far behind, so the oldest go, as they do on Quasar.
        while (_events.Count > MostQueuedEvents && _events.TryDequeue(out _))
        {
        }

        if (Interlocked.Exchange(ref _pumping, 1) == 0)
            Task.Run(Pump);
    }

    private void Pump()
    {
        do
        {
            try
            {
                while (_events.TryDequeue(out Action? delivery))
                    delivery();
            }
            finally
            {
                // A full fence, because the check below mustn't be seen to happen before this.
                Interlocked.Exchange(ref _pumping, 0);
            }
        }
        while (!_events.IsEmpty && Interlocked.Exchange(ref _pumping, 1) == 0);
    }

    private static void Deliver<T>(Action<T> handlers, T value)
    {
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(value);
            }
            catch (Exception exception)
            {
                TellOfBrokenHandler(exception);
            }
        }
    }

    // Reading a plugin's exception is running that plugin's code too.
    private static void TellOfBrokenHandler(Exception exception)
    {
        try
        {
            MonitorLog.Warning("A plugin's Monitor event handler failed: " + exception.Message);
        }
        catch (Exception)
        {
            MonitorLog.Warning("A plugin's Monitor event handler failed.");
        }
    }
}
