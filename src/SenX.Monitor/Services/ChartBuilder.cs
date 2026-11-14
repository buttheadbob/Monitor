using SenX.Monitor.Components.Charts;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>
/// One line or band. Its colour is a CSS class, so it comes from the host's palette. An overlay is drawn
/// as a plain line over a stacked chart, and must come after the bands. <see cref="Unit"/> replaces the
/// chart's unit, and <see cref="SecondScale"/> reads against the 0 to 1 scale on the right.
/// </summary>
public sealed record ChartLine(string Name, string Color, double?[] Values)
{
    public bool Overlay { get; init; }

    public bool Dashed { get; init; }

    /// <summary>A dotted line, for a line that would otherwise look the same as another.</summary>
    public bool Dotted { get; init; }

    public bool SecondScale { get; init; }

    public string? Unit { get; init; }
}

/// <summary>One chart's data. All lines share the same times, and a missing value breaks a line.</summary>
public sealed record ChartData(string Id, string Unit, bool Stacked, double? Maximum, double MinimumToShow, double[] Times, IReadOnlyList<ChartLine> Lines)
{
    /// <summary>Makes this a timeline: one row per name, drawn wherever that row has a value.</summary>
    public IReadOnlyList<string>? Rows { get; init; }
}

/// <summary>A dashed line across a server's charts. Its words are in the events table.</summary>
public sealed record ChartMarker(double Time, string Color, string Text);

public sealed record ChartsResponse(IReadOnlyList<ChartData> Charts)
{
    public IReadOnlyList<ChartMarker> Markers { get; init; } = [];
}

/// <summary>The parts of a frame, each counted once, for the main thread charts.</summary>
public static class FrameParts
{
    /// <summary>A frame's budget. Past this, the server can't keep 60 frames a second, so sim speed falls.</summary>
    public const double BudgetMs = FrameSplit.BudgetMs;

    public const string IdleName = FrameSplit.IdleName;

    public static readonly (string Name, string Color)[] All =
    [
        ("Physics", ChartColors.Physics),
        ("Entities", "monitor-c-0"),
        ("Session components", ChartColors.Session),
        ("Network", ChartColors.Network),
        ("Replication", "monitor-c-1"),
        ("Callbacks", "monitor-c-2"),
        ("GPS", "monitor-c-3"),
        ("Plugins", "monitor-c-4"),
        ("Monitor", "monitor-c-5"),
        ("Other", ChartColors.Other),
        (IdleName, ChartColors.Idle),
    ];

    /// <summary>The time the game spent updating in an average frame.</summary>
    public static double Update(HistoryPoint point) =>
        HistoryScopes.Update < point.Scopes.Length ? point.Scopes[HistoryScopes.Update] : 0d;

    /// <summary>The game thread's busy time in an average frame.</summary>
    public static double Busy(HistoryPoint point) => HistoryScopes.Frame < point.Scopes.Length ? point.Scopes[HistoryScopes.Frame] : 0d;

    /// <summary>The time left in an average frame.</summary>
    public static double Available(HistoryPoint point) =>
        Math.Max(0d, BudgetMs - (HistoryScopes.Frame < point.Scopes.Length ? point.Scopes[HistoryScopes.Frame] : 0d));

    /// <summary>The time left in the worst 1% of frames, which is the room there really is when things get busy.</summary>
    public static double AvailableAtWorst(HistoryPoint point) => Math.Max(0d, BudgetMs - point.FrameP99);

    /// <summary>A frame's time in the order of <see cref="All"/>. Children come out of their parents, so nothing counts twice.</summary>
    public static double[] Split(HistoryPoint point) => FrameSplit.Of(id => id < point.Scopes.Length ? point.Scopes[id] : 0d);
}

/// <summary>
/// Turns stored history into data for the browser to draw. The charts come over plain HTTP, not the
/// page's live connection, the same way Quasar's own charts do, so a busy chart can't slow the page.
/// </summary>
public sealed class ChartBuilder(IHistoryReads store, IMonitorServers servers)
{
    private const int MaxPoints = 1200;
    private const int LinesPerList = 6;
    private const int TimelineRows = 30;
    private const int PlayerLines = 8;
    private const double GapBefore = 0.5d;

    // Half of MaxPoints, so a step always holds a row. The shortest steps fit one-second rows and census passes.
    private const double CompareSteps = 600d;
    private const double ShortestStep = 5d;
    private const double ShortestCensusStep = 60d;

    // Past this, saves are so close together that lines for them would hide the chart.
    private const int SaveMarkerMinutes = 360;

    public ChartsResponse Build(string view, string serverId, int minutes, HistoryDimension list, string? mod = null)
    {
        DateTime to = DateTime.UtcNow;
        return Build(view, serverId, to.AddMinutes(-minutes), to, list, mod);
    }

    /// <summary>The same charts over a fixed stretch of the past, such as around an incident.</summary>
    public ChartsResponse Build(string view, string serverId, DateTime from, DateTime to, HistoryDimension list, string? mod = null)
    {
        return view switch
        {
            "overview" => WithMarkers(Overview(serverId, from, to), serverId, from, to),
            "costs" => WithMarkers(Costs(serverId, list, from, to), serverId, from, to),
            "mods" => WithMarkers(Costs(serverId, HistoryDimension.Mods, from, to, "mods"), serverId, from, to),
            "modparts" => WithMarkers(ModParts(serverId, mod ?? string.Empty, from, to), serverId, from, to),
            "world" => WithMarkers(World(serverId, from, to), serverId, from, to),
            "network" => WithMarkers(Network(serverId, from, to), serverId, from, to),
            "servers" => new ChartsResponse(Compare(from, to)),
            "all" => new ChartsResponse(AllServers(from, to)),
            _ => new ChartsResponse([]),
        };
    }

    private ChartsResponse WithMarkers(List<ChartData> charts, string serverId, DateTime from, DateTime to)
    {
        bool saves = (to - from).TotalMinutes <= SaveMarkerMinutes;

        return new ChartsResponse(charts)
        {
            Markers = store.Markers(serverId, from, to)
                .Where(marker => saves || marker.Kind is not (StoredMarkerKind.Save or StoredMarkerKind.Measuring))
                .Select(marker => new ChartMarker(Seconds(marker.Time), ColorOf(marker.Kind), marker.Text))
                .ToList(),
        };
    }

    private static string ColorOf(StoredMarkerKind kind) => kind switch
    {
        StoredMarkerKind.Lag => ChartColors.Bad,
        StoredMarkerKind.Restart => ChartColors.Session,
        StoredMarkerKind.Change => ChartColors.Network,
        StoredMarkerKind.Measuring => "monitor-c-5",
        StoredMarkerKind.JoinHitch => ChartColors.Join,
        StoredMarkerKind.ThreadJump => ChartColors.ThreadJump,
        _ => ChartColors.Other,
    };

    private List<ChartData> Overview(string serverId, DateTime from, DateTime to)
    {
        List<HistoryPoint> points = store.Points(serverId, from, to, MaxPoints);
        double[] times = PointTimes(points);

        // Worked out once per point, because a page asks for this every second.
        double[][] parts = points.Select(FrameParts.Split).ToArray();

        var charts = new List<ChartData>
        {
            new("sim", string.Empty, false, 1.05d, 0d, times, [new ChartLine("Sim speed", ChartColors.Good, Values(points, point => point.SimSpeed))]),
            new("headroom", " ms", false, FrameParts.BudgetMs, 0d, times,
            [
                new ChartLine("Average frame", ChartColors.Good, Values(points, FrameParts.Available)),
                new ChartLine("Worst 1% of frames", ChartColors.Network, Values(points, FrameParts.AvailableAtWorst)),
            ]),
            new("gameLoop", " ms/f", false, null, 0d, times,
            [
                new ChartLine("Update", ChartColors.Session, Values(points, FrameParts.Update)),
                new ChartLine("Idle", ChartColors.Idle, Values(points, FrameParts.Available)),
            ]),
            new("frames", " ms", false, null, 0d, times,
            [
                new ChartLine("p99", ChartColors.Physics, Values(points, point => point.FrameP99)),
                new ChartLine("p95", ChartColors.Network, Values(points, point => point.FrameP95)),
                new ChartLine("p50", "monitor-c-1", Values(points, point => point.FrameP50)),
            ]),
        };

        var bands = new List<ChartLine>(FrameParts.All.Length);

        for (int part = 0; part < FrameParts.All.Length; part++)
        {
            // Idle is what's left, not something using the frame, and it would hide the budget line.
            if (FrameParts.All[part].Name == FrameParts.IdleName)
                continue;

            int index = part;
            int row = 0;
            bands.Add(new ChartLine(FrameParts.All[part].Name, FrameParts.All[part].Color, Values(points, point => parts[row++][index])));
        }

        // When the bands reach the budget line, sim speed falls, and the band that grew is the cause.
        List<ChartLine> lines = Biggest(bands);
        lines.Add(new ChartLine("Frame budget", ChartColors.Bad, Values(points, _ => FrameParts.BudgetMs)) { Overlay = true, Dashed = true });
        lines.Add(new ChartLine("Sim speed", ChartColors.Good, Values(points, point => point.SimSpeed)) { Overlay = true, SecondScale = true, Unit = string.Empty });

        charts.Add(new ChartData("cause", " ms/f", true, null, 0.1d, times, lines));
        charts.Add(new ChartData("gc", string.Empty, false, null, 0d, times,
        [
            new ChartLine("Gen 0", "monitor-c-1", Values(points, point => point.Gen0)),
            new ChartLine("Gen 1", ChartColors.Network, Values(points, point => point.Gen1)),
            new ChartLine("Gen 2", ChartColors.Bad, Values(points, point => point.Gen2)),
        ]));
        charts.Add(new ChartData("allocated", " MB", false, null, 0d, times,
            [new ChartLine("Main thread allocated", ChartColors.Other, Values(points, point => point.AllocatedKb / 1024d))]));

        List<StoredCensus> census = store.Census(serverId, from, to);
        double[] censusTimes = census.Select(row => Seconds(row.Time)).ToArray();
        charts.Add(new ChartData("memory", " MB", false, null, 0d, censusTimes,
        [
            new ChartLine("Working set", ChartColors.Session, census.Select(row => (double?)row.Row.WorkingSetMb).ToArray()),
            new ChartLine("Managed", ChartColors.Good, census.Select(row => (double?)row.Row.ManagedMb).ToArray()),
        ]));

        return charts;
    }

    private List<ChartData> Network(string serverId, DateTime from, DateTime to)
    {
        List<HistoryPoint> points = store.Points(serverId, from, to, MaxPoints);
        double[] times = PointTimes(points);
        List<StoredPlayer> players = store.Players(serverId, from, to);

        // Other is what's left, so the bands always add up to what was sent.
        List<ChartLine> kinds = Biggest(
        [
            new ChartLine("State sync", ChartColors.Network, Values(points, point => point.StateSyncKb)),
            new ChartLine("Events", ChartColors.Session, Values(points, point => point.EventsKb)),
            new ChartLine("Replication", "monitor-c-1", Values(points, point => point.ReplicationKb)),
            new ChartLine("Other", ChartColors.Other, Values(points, point => Math.Max(0f, point.SentKb - point.StateSyncKb - point.EventsKb - point.ReplicationKb))),
        ]);

        return
        [
            new ChartData("traffic", " KB/s", false, null, 0d, times,
            [
                new ChartLine("Sent", ChartColors.Network, Values(points, point => point.SentKb)),
                new ChartLine("Received", ChartColors.Good, Values(points, point => point.ReceivedKb)),
            ]),
            new ChartData("trafficKinds", " KB/s", true, null, 0d, times, kinds),
            new ChartData("rpcRate", " /s", false, null, 0d, times, [new ChartLine("RPCs from clients", ChartColors.Session, Values(points, point => point.RpcsIn))]),
            PlayerChart("playerTraffic", " KB/s", players, row => row.SentBytesPerSecond / 1024d),
            PlayerChart("playerQueue", " KB", players, row => row.QueuedBytes / 1024d),
            .. Costs(serverId, HistoryDimension.Rpcs, from, to, "rpcs"),
            SentChart(serverId, from, to),
        ];
    }

    private ChartData SentChart(string serverId, DateTime from, DateTime to)
    {
        List<StoredGroup> rows = store.Groups(serverId, HistoryDimension.RpcsSent, from, to);
        HashSet<string> top = Top(serverId, HistoryDimension.RpcsSent, rows, row => row.BytesPerSecond);

        return CostChart("rpcsSent", rows.Where(row => top.Contains(row.Name)).ToList(), " KB/s", row => row.BytesPerSecond / 1024d);
    }

    private List<ChartData> Costs(string serverId, HistoryDimension list, DateTime from, DateTime to, string id = "costs")
    {
        List<StoredGroup> rows = store.Groups(serverId, list, from, to);
        HashSet<string> top = Top(serverId, list, rows, row => row.MainMs + row.WorkerMs);

        return [CostChart(id, rows.Where(row => top.Contains(row.Name)).ToList(), " ms/f", row => row.MainMs + row.WorkerMs)];
    }

    // What's costliest now. A quiet list can have nothing in the latest total, and the range may still have plenty to show.
    private HashSet<string> Top(string serverId, HistoryDimension list, List<StoredGroup> rows, Func<StoredGroup, double> value)
    {
        List<StoredGroup> latest = store.LatestGroups(serverId, list);

        IEnumerable<string> names = latest.Count > 0
            ? latest.Select(row => row.Name)
            : rows.GroupBy(row => row.Name).OrderByDescending(group => group.Max(value)).Select(group => group.Key);

        return names.Take(LinesPerList).ToHashSet();
    }

    // The costliest parts of one mod, with their lines named without the mod in front.
    private List<ChartData> ModParts(string serverId, string mod, DateTime from, DateTime to)
    {
        string prefix = ModNames.PartsOf(mod);

        HashSet<string> top = store.LatestGroups(serverId, HistoryDimension.ModParts)
            .Where(row => row.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Take(LinesPerList)
            .Select(row => row.Name)
            .ToHashSet();

        List<StoredGroup> rows = store.Groups(serverId, HistoryDimension.ModParts, from, to)
            .Where(row => top.Contains(row.Name))
            .Select(row => row with { Name = ModNames.PartName(row.Name) })
            .ToList();

        return [CostChart("modParts", rows, " ms/f", row => row.MainMs + row.WorkerMs)];
    }

    private static ChartData CostChart(string id, List<StoredGroup> rows, string unit, Func<StoredGroup, double> value)
    {
        double[] times = rows.Select(row => Seconds(row.Time)).Distinct().Order().ToArray();
        Dictionary<double, int> indexOf = IndexOf(times);

        // Two grids can share a name, so anything with an id gets its own line, and the id tells those lines apart.
        List<IGrouping<string, StoredGroup>> series = rows.GroupBy(row => row.Identity).ToList();
        HashSet<string> shared = series.GroupBy(group => group.First().Name).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();

        List<ChartLine> lines = series
            .Select(group =>
            {
                var values = new double?[times.Length];

                foreach (StoredGroup row in group)
                {
                    int at = indexOf[Seconds(row.Time)];
                    values[at] = (values[at] ?? 0d) + value(row);
                }

                string name = group.First().Name;
                string label = shared.Contains(name) ? name + " " + group.Key : name;

                return new ChartLine(label, ChartColors.For(label), values);
            })
            .ToList();

        return new ChartData(id, unit, false, null, 0.1d, times, Biggest(lines));
    }

    private List<ChartData> World(string serverId, DateTime from, DateTime to)
    {
        List<StoredCensus> rows = store.Census(serverId, from, to);
        double[] times = rows.Select(row => Seconds(row.Time)).ToArray();

        double?[] Column(Func<HistoryCensus, double> value) => rows.Select(row => (double?)value(row.Row)).ToArray();

        List<StoredPlayer> players = store.Players(serverId, from, to);

        return
        [
            Timeline(players),
            FactionsOnline(players),
            FactionBlocks(store.Factions(serverId, from, to)),
            PlayerChart("playerPing", " ms", players, row => row.PingMs),
            new ChartData("players", string.Empty, false, null, 0d, times, [new ChartLine("Online", ChartColors.Session, Column(row => row.OnlinePlayers))]),
            new ChartData("blocks", string.Empty, false, null, 0d, times, [new ChartLine("Blocks", ChartColors.Other, Column(row => row.Blocks))]),
            new ChartData("blocksConceal", string.Empty, false, null, 0d, times,
            [
                new ChartLine("Active", ChartColors.Good, Column(row => (double)row.Blocks - row.ConcealedBlocks)),
                new ChartLine("Concealed", ChartColors.Idle, Column(row => row.ConcealedBlocks)),
            ]),
            new ChartData("pcu", string.Empty, false, null, 0d, times, [new ChartLine("PCU", "monitor-c-1", Column(row => row.Pcu))]),
            new ChartData("ping", " ms", false, null, 0d, times,
            [
                new ChartLine("Worst", ChartColors.Network, Column(row => row.PingWorst)),
                new ChartLine("Mean", ChartColors.Good, Column(row => row.PingMean)),
            ]),
            new ChartData("objects", string.Empty, false, null, 0d, times,
            [
                new ChartLine("Grids", "monitor-c-0", Column(row => row.Grids)),
                new ChartLine("Characters", "monitor-c-2", Column(row => row.Characters)),
                new ChartLine("Floating objects", "monitor-c-3", Column(row => row.FloatingObjects)),
            ]),
        ];
    }

    // Servers don't tick together, so their rows go in shared steps of time, and a line breaks only where
    // a server really has no data. Colours go by position, because two servers sharing one defeats this view.
    private List<ChartData> Compare(DateTime from, DateTime to)
    {
        List<MonitorServer> connected = servers.Connected().OrderBy(server => server.Name, StringComparer.Ordinal).ToList();
        double span = (to - from).TotalSeconds;

        List<List<HistoryPoint>> points = connected.Select(server => store.Points(server.HistoryId, from, to, MaxPoints)).ToList();
        double step = Math.Max(ShortestStep, span / CompareSteps);
        double[] times = StepTimes(points.SelectMany(rows => rows).Select(point => point.Time), step);

        List<ChartLine> Lines(Func<HistoryPoint, double> value, Func<double, double, double> keep) =>
            Aligned(connected, points, times, step, point => point.Time, value, keep);

        List<List<StoredCensus>> census = connected.Select(server => store.Census(server.HistoryId, from, to)).ToList();
        double censusStep = Math.Max(ShortestCensusStep, span / CompareSteps);
        double[] censusTimes = StepTimes(census.SelectMany(rows => rows).Select(row => row.Time), censusStep);

        return
        [
            new ChartData("serversSim", string.Empty, false, 1.05d, 0d, times, Lines(point => point.SimSpeed, Math.Min)),
            new ChartData("serversAvailable", " ms", false, FrameParts.BudgetMs, 0d, times, Lines(FrameParts.Available, Math.Min)),
            new ChartData("serversFrames", " ms", false, null, 0d, times, Lines(point => point.FrameP99, Math.Max)),
            new ChartData("serversPlayers", string.Empty, false, null, 0d, censusTimes,
                Aligned(connected, census, censusTimes, censusStep, row => row.Time, row => row.Row.OnlinePlayers, (_, latest) => latest)),
        ];
    }

    /// <summary>The charts the All servers view shows, in its order.</summary>
    public static readonly string[] AllServersCharts =
        ["allSim", "allAvailable", "allFrames", "allBusy", "allWorker", "allPlayers", "allPing", "allMemory", "allGc", "allSent", "allRpcs", "allPcu"];

    // Every connected server on each chart, one line each. It reads only Quasar's own history, which the collector
    // fills anyway, so it asks no server for anything. Colours repeat after eight, so lines after that are dashed, then dotted.
    private List<ChartData> AllServers(DateTime from, DateTime to)
    {
        List<MonitorServer> connected = servers.Connected().OrderBy(server => server.Name, StringComparer.Ordinal).ToList();
        double span = (to - from).TotalSeconds;

        List<List<HistoryPoint>> points = connected.Select(server => store.Points(server.HistoryId, from, to, MaxPoints)).ToList();
        double step = Math.Max(ShortestStep, span / CompareSteps);
        double[] times = StepTimes(points.SelectMany(rows => rows).Select(point => point.Time), step);

        List<ChartLine> Lines(Func<HistoryPoint, double> value, Func<double, double, double> keep) =>
            Styled(Aligned(connected, points, times, step, point => point.Time, value, keep));

        List<List<StoredCensus>> census = connected.Select(server => store.Census(server.HistoryId, from, to)).ToList();
        double censusStep = Math.Max(ShortestCensusStep, span / CompareSteps);
        double[] censusTimes = StepTimes(census.SelectMany(rows => rows).Select(row => row.Time), censusStep);

        List<ChartLine> CensusLines(Func<HistoryCensus, double> value) =>
            Styled(Aligned(connected, census, censusTimes, censusStep, row => row.Time, row => value(row.Row), (_, latest) => latest));

        // Background totals keep each total's top grids, so their worker time added up is an estimate of the server's.
        List<List<(DateTime Time, double WorkerMs)>> totals = connected
            .Select(server => store.Groups(server.HistoryId, HistoryDimension.Grids, from, to)
                .GroupBy(row => row.Time)
                .Select(total => (total.Key, (double)total.Sum(row => row.WorkerMs)))
                .ToList())
            .ToList();
        double[] totalTimes = StepTimes(totals.SelectMany(rows => rows).Select(row => row.Time), censusStep);

        return
        [
            new ChartData("allSim", string.Empty, false, 1.05d, 0d, times, Lines(point => point.SimSpeed, Math.Min)),
            new ChartData("allAvailable", " ms", false, FrameParts.BudgetMs, 0d, times, Lines(FrameParts.AvailableAtWorst, Math.Min)),
            new ChartData("allFrames", " ms", false, null, 0d, times, Lines(point => point.FrameP99, Math.Max)),
            new ChartData("allBusy", " ms", false, null, 0d, times, Lines(FrameParts.Busy, Math.Max)),
            new ChartData("allWorker", " ms", false, null, 0d, totalTimes,
                Styled(Aligned(connected, totals, totalTimes, censusStep, row => row.Time, row => row.WorkerMs, (_, latest) => latest))),
            new ChartData("allPlayers", string.Empty, false, null, 0d, censusTimes, CensusLines(row => row.OnlinePlayers)),
            new ChartData("allPing", " ms", false, null, 0d, censusTimes, CensusLines(row => row.PingMean)),
            new ChartData("allMemory", " MB", false, null, 0d, censusTimes, CensusLines(row => row.WorkingSetMb)),
            new ChartData("allGc", string.Empty, false, null, 0d, times, Lines(point => point.Gen2, (held, current) => held + current)),
            new ChartData("allSent", " KB/s", false, null, 0d, times, Lines(point => point.SentKb, Math.Max)),
            new ChartData("allRpcs", " /s", false, null, 0d, times, Lines(point => point.RpcsIn, Math.Max)),
            new ChartData("allPcu", string.Empty, false, null, 0d, censusTimes, CensusLines(row => row.Pcu)),
        ];
    }

    // The palette has eight colours. Lines 9 to 16 reuse them dashed, and 17 to 24 dotted, so no two lines look the same.
    // Lines aren't filled, because many filled areas on one chart hide each other.
    private static List<ChartLine> Styled(List<ChartLine> lines) =>
        lines.Select((line, index) => (index / ChartColors.Count % 3) switch
        {
            1 => line with { Dashed = true, Overlay = true },
            2 => line with { Dotted = true, Overlay = true },
            _ => line with { Overlay = true },
        }).ToList();

    private static double StepOf(DateTime time, double step) => Math.Floor(Seconds(time) / step) * step;

    private static double[] StepTimes(IEnumerable<DateTime> times, double step) =>
        times.Select(time => StepOf(time, step)).Distinct().Order().ToArray();

    private static List<ChartLine> Aligned<T>(
        List<MonitorServer> connected,
        List<List<T>> rows,
        double[] times,
        double step,
        Func<T, DateTime> timeOf,
        Func<T, double> value,
        Func<double, double, double> keep)
    {
        Dictionary<double, int> indexOf = IndexOf(times);
        var lines = new List<ChartLine>(connected.Count);

        for (int server = 0; server < connected.Count; server++)
        {
            var values = new double?[times.Length];

            foreach (T row in rows[server])
            {
                int index = indexOf[StepOf(timeOf(row), step)];
                double current = value(row);
                values[index] = values[index] is { } held ? keep(held, current) : current;
            }

            lines.Add(new ChartLine(connected[server].Name, ChartColors.At(server), values));
        }

        return lines;
    }

    // Who was on, a row per player. Most time online goes at the top, and colour follows the faction.
    private static ChartData Timeline(List<StoredPlayer> players)
    {
        double[] times = players.Select(row => Seconds(row.Time)).Distinct().Order().ToArray();
        Dictionary<double, int> indexOf = IndexOf(times);

        List<IGrouping<string, StoredPlayer>> named = players
            .Where(row => row.Name.Length > 0)
            .GroupBy(row => row.Name)
            .OrderByDescending(group => group.Count())
            .Take(TimelineRows)
            .ToList();

        var lines = new List<ChartLine>(named.Count);

        foreach (IGrouping<string, StoredPlayer> player in named)
        {
            var values = new double?[times.Length];

            foreach (StoredPlayer row in player)
                values[indexOf[Seconds(row.Time)]] = lines.Count;

            lines.Add(new ChartLine(player.Key, ChartColors.For(player.Last().Faction), values));
        }

        return new ChartData("online", string.Empty, false, null, 0d, times, lines) { Rows = named.Select(group => group.Key).ToList() };
    }

    private static ChartData FactionsOnline(List<StoredPlayer> players)
    {
        double[] times = players.Select(row => Seconds(row.Time)).Distinct().Order().ToArray();
        Dictionary<double, int> indexOf = IndexOf(times);

        List<ChartLine> lines = players
            .Where(row => row.Name.Length > 0)
            .GroupBy(row => row.Faction)
            .Select(faction =>
            {
                var values = new double?[times.Length];
                Array.Fill(values, 0d);

                foreach (StoredPlayer row in faction)
                    values[indexOf[Seconds(row.Time)]] += 1d;

                return new ChartLine(faction.Key, ChartColors.For(faction.Key), values);
            })
            .ToList();

        return new ChartData("factionsOnline", string.Empty, true, null, 0d, times, Biggest(lines));
    }

    // The biggest factions now, as a chart of everyone would be a tangle.
    private static ChartData FactionBlocks(List<StoredFaction> factions)
    {
        double[] times = factions.Select(row => Seconds(row.Time)).Distinct().Order().ToArray();
        Dictionary<double, int> indexOf = IndexOf(times);

        List<ChartLine> lines = factions
            .GroupBy(row => row.Name)
            .OrderByDescending(faction => faction.Last().Blocks)
            .Take(LinesPerList)
            .Select(faction =>
            {
                var values = new double?[times.Length];

                foreach (StoredFaction row in faction)
                    values[indexOf[Seconds(row.Time)]] = row.Blocks;

                return new ChartLine(faction.Key, ChartColors.For(faction.Key), values);
            })
            .ToList();

        return new ChartData("blocksByFaction", string.Empty, false, null, 0d, times, Biggest(lines));
    }

    // The worst players are the ones worth a line, and this keeps a full server's chart readable.
    private static ChartData PlayerChart(string id, string unit, List<StoredPlayer> players, Func<StoredPlayer, double> value)
    {
        double[] times = players.Select(row => Seconds(row.Time)).Distinct().Order().ToArray();
        Dictionary<double, int> indexOf = IndexOf(times);

        List<ChartLine> lines = players
            .Where(row => row.Name.Length > 0)
            .GroupBy(row => row.Name)
            .OrderByDescending(player => player.Average(value))
            .Take(PlayerLines)
            .Select(player =>
            {
                var values = new double?[times.Length];

                foreach (StoredPlayer row in player)
                    values[indexOf[Seconds(row.Time)]] = value(row);

                return new ChartLine(player.Key, ChartColors.For(player.Key), values);
            })
            .ToList();

        return new ChartData(id, unit, false, null, 0d, times, Biggest(lines));
    }

    private static Dictionary<double, int> IndexOf(double[] times)
    {
        var index = new Dictionary<double, int>(times.Length);

        for (int i = 0; i < times.Length; i++)
            index[times[i]] = i;

        return index;
    }

    private static double Seconds(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds() / 1000d;

    // A paused server has no data. An extra empty moment before the row after it breaks the line there.
    private static double[] PointTimes(List<HistoryPoint> points)
    {
        var times = new List<double>(points.Count + 4);

        foreach (HistoryPoint point in points)
        {
            if (point.AfterGap)
                times.Add(Seconds(point.Time) - GapBefore);

            times.Add(Seconds(point.Time));
        }

        return [.. times];
    }

    private static double?[] Values(List<HistoryPoint> points, Func<HistoryPoint, double> value)
    {
        var values = new List<double?>(points.Count + 4);

        foreach (HistoryPoint point in points)
        {
            if (point.AfterGap)
                values.Add(null);

            values.Add(value(point));
        }

        return [.. values];
    }

    // Biggest first, because legends sort by current value.
    private static List<ChartLine> Biggest(List<ChartLine> lines) =>
        lines.OrderByDescending(line => Array.FindLast(line.Values, value => value.HasValue) ?? 0d).ToList();
}
