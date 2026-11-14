using System.Globalization;
using SenX.Monitor.Components.Charts;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>A range a value can fall in, in rising order. A value takes the colour of the first limit it's at or under.</summary>
public sealed record BoardBand(double Limit, string Color);

/// <summary>A level drawn across a chart, such as the frame budget.</summary>
public sealed record BoardLevel(double Value, string Color);

/// <summary>
/// One value per row and step of time, in rows then columns. An empty cell means nothing was recorded,
/// which isn't the same as zero: history only holds the costliest rows of each total.
/// </summary>
public sealed record BoardHeat(IReadOnlyList<string> Names, double Start, double Step, int Columns, double Maximum, double?[] Values);

/// <summary>A box in a treemap. A parent's own value is the sum of its children, so nothing counts twice.</summary>
public sealed record BoardNode(string Name, double Value, string Color, IReadOnlyList<BoardNode> Children);

/// <summary>A table cell. A time is shown in the reader's own time zone, and a server makes the cell a link to its board.</summary>
public sealed record BoardCell(string Text)
{
    public string? Color { get; init; }

    public double? Time { get; init; }

    public string? Server { get; init; }
}

public sealed record BoardTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<BoardCell>> Rows);

/// <summary>One panel. Its <see cref="Kind"/> says which of the optional parts it fills in.</summary>
public sealed record BoardPanel(string Id, string Kind, string Title, int Span, int Height)
{
    public string? Unit { get; init; }

    public int? Decimals { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public string? Hint { get; init; }

    public double? Value { get; init; }

    public IReadOnlyList<BoardBand>? Bands { get; init; }

    public IReadOnlyList<BoardLevel>? Levels { get; init; }

    public ChartData? Chart { get; init; }

    public IReadOnlyList<ChartSlice>? Slices { get; init; }

    public BoardHeat? Heat { get; init; }

    public IReadOnlyList<BoardNode>? Tree { get; init; }

    public BoardTable? Table { get; init; }
}

public sealed record BoardSection(string Title, IReadOnlyList<BoardPanel> Panels);

public sealed record BoardServer(string Id, string Name, string Cluster);

/// <summary>A whole board. Which panels it holds is decided here, and the page only draws what it's sent.</summary>
public sealed record BoardResponse(string Title, string Server, IReadOnlyList<BoardServer> Servers, IReadOnlyList<BoardSection> Sections, IReadOnlyList<ChartMarker> Markers)
{
    /// <summary>How far back history goes.</summary>
    public int RetentionMinutes { get; init; }
}

/// <summary>
/// Builds the full boards that open in their own window: one server's, or every server's together. It
/// reads the same history as the charts on the plugin page, and only ever runs while one is open.
/// </summary>
public sealed class BoardBuilder(HistoryStore store, IMonitorServers servers, MonitorSettings settings)
{
    private const int StatPoints = 120;
    private const int HeatRows = 20;
    private const int HeatColumns = 720;
    private const int TreeRows = 30;
    private const int TreeRowsPerServer = 8;
    private const int EventRows = 50;
    private const int NowSeconds = 30;
    private const int CensusMinutes = 2;
    private const int Rounding = 3;
    private const double ShortestLagStep = 5d;

    private const int StatHeight = 110;
    private const int GaugeHeight = 200;
    private const int ChartHeight = 250;
    private const int TallHeight = 340;
    private const int HeatHeight = 320;
    private const int TreeHeight = 380;
    private const int TableHeight = 300;

    // Tiles and gauges read as a traffic light.
    private const string Green = "monitor-c-idle";
    private const string Amber = "monitor-c-network";
    private const string Red = "monitor-c-physics";

    private const double SimBad = 0.7d;
    private const double SimLow = 0.9d;

    private static readonly BoardBand[] s_simBands = [new(SimBad, Red), new(SimLow, Amber), new(double.MaxValue, Green)];

    private static readonly BoardBand[] s_availableBands = [new(1d, Red), new(4d, Amber), new(double.MaxValue, Green)];

    private static readonly BoardBand[] s_frameBands = [new(12d, Green), new(FrameParts.BudgetMs, Amber), new(double.MaxValue, Red)];

    private static readonly BoardBand[] s_shareBands = [new(70d, Green), new(90d, Amber), new(double.MaxValue, Red)];

    private static readonly BoardBand[] s_pingBands = [new(100d, Green), new(200d, Amber), new(double.MaxValue, Red)];

    private static readonly BoardLevel[] s_budget = [new(FrameParts.BudgetMs, Red)];

    private static readonly BoardLevel[] s_simLevels = [new(1d, Green), new(SimBad, Red)];

    // Every list a board shows, read together.
    private static readonly HistoryDimension[] s_lists =
    [
        HistoryDimension.Grids,
        HistoryDimension.Owners,
        HistoryDimension.BlockTypes,
        HistoryDimension.Mods,
        HistoryDimension.ModParts,
        HistoryDimension.Scripts,
        HistoryDimension.Logic,
        HistoryDimension.Session,
        HistoryDimension.Callbacks,
        HistoryDimension.Rpcs,
        HistoryDimension.RpcsSent,
    ];

    private static readonly (HistoryDimension List, string Title, string Hint)[] s_costs =
    [
        (HistoryDimension.Grids, "Grids", "Time each grid took. Scripts and callbacks are left out here, because they run inside a grid's update."),
        (HistoryDimension.Owners, "Owners", "Grid time by whoever owns the grid."),
        (HistoryDimension.BlockTypes, "Block types", "Time by type of block, across every grid."),
        (HistoryDimension.Mods, "Mods and plugins", "Time spent in each mod's or plugin's own code."),
        (HistoryDimension.Scripts, "Scripts", "Programmable blocks."),
        (HistoryDimension.Logic, "Game logic", "Game logic components."),
        (HistoryDimension.Session, "Session components", "Session components, each one timed on its own."),
        (HistoryDimension.Callbacks, "Grid callbacks", "Work grids queue for themselves, by what it runs."),
    ];

    /// <summary>The servers there are boards for.</summary>
    public IReadOnlyList<MonitorServer> Connected() => servers.Connected();

    /// <summary>Whether this names a server there's history for.</summary>
    public bool Knows(HistoryStore history, string server) => BoardRequest.Known(history, servers, server);

    /// <summary>The board for a stretch of time. With no server named, it's the one for every server.</summary>
    public BoardResponse Build(string serverId, DateTime from, DateTime to)
    {
        List<MonitorServer> connected = servers.Connected().ToList();
        IReadOnlyList<BoardServer> list = connected.Select(server => new BoardServer(server.HistoryId, server.Name, server.Cluster)).ToList();
        int retention = settings.RetentionDays * 24 * 60;
        var reads = new BoardReads(store, s_lists);
        var charts = new ChartBuilder(reads, servers);

        if (serverId.Length == 0)
            return new BoardResponse("All servers", string.Empty, list, EveryServer(reads, charts, connected, from, to), []) { RetentionMinutes = retention };

        string title = connected.Find(server => server.HistoryId == serverId)?.Name ?? serverId;
        return Single(reads, charts, title, serverId, list, retention, from, to);
    }

    private BoardResponse Single(BoardReads reads, ChartBuilder charts, string title, string serverId, IReadOnlyList<BoardServer> list, int retention, DateTime from, DateTime to)
    {
        ChartsResponse overview = charts.Build("overview", serverId, from, to, HistoryDimension.Grids);
        ChartsResponse world = charts.Build("world", serverId, from, to, HistoryDimension.Grids);
        ChartsResponse network = charts.Build("network", serverId, from, to, HistoryDimension.Grids);

        var byId = new Dictionary<string, ChartData>();

        foreach (ChartData chart in overview.Charts.Concat(world.Charts).Concat(network.Charts))
            byId[chart.Id] = chart;

        List<HistoryPoint> recent = store.Points(serverId, to.AddSeconds(-NowSeconds), to, NowSeconds * 2);
        HistoryCensus? census = store.Census(serverId, to.AddMinutes(-CensusMinutes), to).LastOrDefault()?.Row;

        var sections = new List<BoardSection>
        {
            new("Overview", Tiles(byId, recent, census)),
            new("The frame", Frame(byId, recent)),
            new("Costs", Costs(charts, serverId, from, to)),
            new("Where it goes", WhereItGoes(reads, serverId, from, to)),
            new("Network", NetworkPanels(byId)),
            new("World", WorldPanels(reads, byId, serverId, from, to)),
        };

        if (Events(serverId, from, to) is { } events)
            sections.Add(new BoardSection("Events", [events]));

        return new BoardResponse(title, serverId, list, sections, overview.Markers) { RetentionMinutes = retention };
    }

    private static List<BoardPanel> Tiles(Dictionary<string, ChartData> byId, List<HistoryPoint> recent, HistoryCensus? census)
    {
        double? sim = recent.Count == 0 ? null : recent.Average(point => point.SimSpeed);
        double? available = recent.Count == 0 ? null : recent.Min(FrameParts.AvailableAtWorst);
        double? p99 = recent.Count == 0 ? null : recent.Max(point => point.FrameP99);
        double? busy = recent.Count == 0 ? null : (1d - (recent.Average(FrameParts.Available) / FrameParts.BudgetMs)) * 100d;

        return
        [
            Stat("statSim", "Sim speed", sim, string.Empty, 2, Spark(byId, "sim", 0), s_simBands),
            Stat("statAvailable", "Available, worst 1%", available, " ms", 1, Spark(byId, "headroom", 1), s_availableBands),
            Stat("statP99", "Frame p99", p99, " ms", 1, Spark(byId, "frames", 0), s_frameBands),
            Stat("statPlayers", "Players online", census?.OnlinePlayers, string.Empty, 0, Spark(byId, "players", 0), null),
            Stat("statGrids", "Grids", census?.Grids, string.Empty, 0, Spark(byId, "objects", 0), null),
            Stat("statPcu", "PCU", census?.Pcu, string.Empty, 0, Spark(byId, "pcu", 0), null),

            Gauge("gaugeSim", "Sim speed", sim, string.Empty, 2, 0d, 1.2d, s_simBands),
            Gauge("gaugeMain", "Main thread busy", busy, " %", 0, 0d, 100d, s_shareBands),
            Gauge("gaugeCpu", "Server CPU", census?.CpuPercent, " %", 0, 0d, 100d, s_shareBands),
            // With nobody on there is no ping to read, which is not a ping of zero.
            Gauge("gaugePing", "Server ping", census is { OnlinePlayers: > 0U } ? census.PingMean : null, " ms", 0, 0d, 300d, s_pingBands),
        ];
    }

    private static List<BoardPanel> Frame(Dictionary<string, ChartData> byId, List<HistoryPoint> recent)
    {
        return
        [
            Time("frameCause", "What's using the frame, against sim speed", byId, "cause", 8, TallHeight, "When the bands reach the budget line, sim speed falls, and the band that grew is the cause.", s_budget),
            new BoardPanel("frameDonut", "donut", "Main thread, last 30 s", 4, TallHeight) { Unit = " ms", Slices = Slices(recent) },
            Time("frameSim", "Sim speed", byId, "sim", 4, ChartHeight, levels: s_simLevels),
            Time("frameHeadroom", "Available frame time", byId, "headroom", 4, ChartHeight, "Time left in a frame before sim speed starts to fall."),
            Time("frameTimes", "Frame time", byId, "frames", 4, ChartHeight, levels: s_budget),
            Time("frameLoop", "Game loop time", byId, "gameLoop", 3, ChartHeight),
            Time("frameGc", "Garbage collections each second", byId, "gc", 3, ChartHeight),
            Time("frameAllocated", "Main thread allocation", byId, "allocated", 3, ChartHeight),
            Time("frameMemory", "Memory", byId, "memory", 3, ChartHeight),
        ];
    }

    // The frame's parts over the last moments, shared the same way as the chart above them.
    private static List<ChartSlice> Slices(List<HistoryPoint> recent)
    {
        if (recent.Count == 0)
            return [];

        var totals = new double[FrameParts.All.Length];

        foreach (HistoryPoint point in recent)
        {
            double[] parts = FrameParts.Split(point);

            for (int part = 0; part < totals.Length; part++)
                totals[part] += parts[part];
        }

        return FrameParts.All
            .Select((part, index) => new ChartSlice(part.Name, part.Color, Math.Round(totals[index] / recent.Count, Rounding)))
            .Where(slice => slice.Value > 0d)
            .ToList();
    }

    private static List<BoardPanel> Costs(ChartBuilder charts, string serverId, DateTime from, DateTime to)
    {
        var panels = new List<BoardPanel>(s_costs.Length);

        for (int cost = 0; cost < s_costs.Length; cost++)
        {
            (HistoryDimension list, string title, string hint) = s_costs[cost];
            ChartData chart = charts.Build("costs", serverId, from, to, list).Charts[0];

            panels.Add(new BoardPanel("cost" + list, "time", title, cost < 6 ? 4 : 6, ChartHeight) { Chart = chart, Unit = chart.Unit, Hint = hint });
        }

        return panels;
    }

    private static List<BoardPanel> WhereItGoes(BoardReads reads, string serverId, DateTime from, DateTime to)
    {
        const string HeatHint = "Darker is cheaper. An empty cell means it wasn't among the costliest then.";
        const string TreeHint = "Each box is its average over the range on show, so a box's size is its share of the time.";

        List<WindowRow> Rows(HistoryDimension list) => HistoryWindows.ToRows(reads.Groups(serverId, list, from, to));

        return
        [
            HeatPanel("heatGrids", "Grids over time", Heat(reads, serverId, HistoryDimension.Grids, from, to), " ms/f", 12, HeatHint),
            HeatPanel("heatMods", "Mods and plugins over time", Heat(reads, serverId, HistoryDimension.Mods, from, to), " ms/f", 6, HeatHint),
            HeatPanel("heatOwners", "Owners over time", Heat(reads, serverId, HistoryDimension.Owners, from, to), " ms/f", 6, HeatHint),
            TreePanel("treeMods", "Mods and their parts", ModTree(Rows(HistoryDimension.Mods), Rows(HistoryDimension.ModParts)), 4, TreeHint),
            TreePanel("treeGrids", "Grids", FlatTree(Rows(HistoryDimension.Grids)), 4, TreeHint),
            TreePanel("treeBlocks", "Block types", FlatTree(Rows(HistoryDimension.BlockTypes)), 4, TreeHint),
        ];
    }

    private static List<BoardPanel> NetworkPanels(Dictionary<string, ChartData> byId) =>
    [
        Time("netTraffic", "Sent and received", byId, "traffic", 4, ChartHeight),
        Time("netKinds", "What's being sent", byId, "trafficKinds", 4, ChartHeight),
        Time("netRate", "RPCs from clients", byId, "rpcRate", 4, ChartHeight),
        Time("netPlayers", "Sent to each player", byId, "playerTraffic", 6, ChartHeight),
        Time("netQueue", "Waiting to send to each player", byId, "playerQueue", 6, ChartHeight),
        Time("netRpcs", "Time in RPCs from clients", byId, "rpcs", 6, ChartHeight),
        Time("netSent", "RPCs the server sent", byId, "rpcsSent", 6, ChartHeight),
    ];

    private static List<BoardPanel> WorldPanels(BoardReads reads, Dictionary<string, ChartData> byId, string serverId, DateTime from, DateTime to)
    {
        List<BoardNode> factions = reads.Factions(serverId, from, to)
            .GroupBy(row => row.Name)
            .Select(faction => Leaf(faction.Key, faction.Last().Blocks))
            .Where(node => node.Value > 0d)
            .OrderByDescending(node => node.Value)
            .Take(TreeRows)
            .ToList();

        ChartData? timeline = byId.GetValueOrDefault("online");
        int rows = timeline?.Rows?.Count ?? 0;

        return
        [
            new BoardPanel("worldOnline", "timeline", "Who was on", 12, Math.Max(ChartHeight / 2, 44 + (rows * 20))) { Chart = timeline },
            Time("worldPlayers", "Players online", byId, "players", 3, ChartHeight),
            Time("worldBlocks", "Blocks", byId, "blocks", 3, ChartHeight),
            Time("worldPcu", "PCU", byId, "pcu", 3, ChartHeight),
            Time("worldConceal", "Blocks and concealment", byId, "blocksConceal", 3, ChartHeight, "Concealed grids are stopped by a concealment plugin, so they cost almost nothing."),
            Time("worldObjects", "Grids and objects", byId, "objects", 4, ChartHeight),
            Time("worldPing", "Server ping", byId, "ping", 4, ChartHeight),
            Time("worldPlayerPing", "Ping by player", byId, "playerPing", 4, ChartHeight),
            Time("worldFactionBlocks", "Blocks by faction", byId, "blocksByFaction", 4, ChartHeight),
            Time("worldFactionsOnline", "Online by faction", byId, "factionsOnline", 4, ChartHeight),
            new BoardPanel("worldFactionTree", "treemap", "Blocks by faction, latest", 4, ChartHeight) { Tree = factions, Decimals = 0 },
        ];
    }

    private BoardPanel? Events(string serverId, DateTime from, DateTime to)
    {
        // Thread jumps have their own list, and could push lag and restarts out of this one.
        List<IReadOnlyList<BoardCell>> rows = store.Markers(serverId, from, to)
            .Where(marker => marker.Kind != StoredMarkerKind.ThreadJump)
            .Take(EventRows)
            .Select(marker => (IReadOnlyList<BoardCell>)[new BoardCell(string.Empty) { Time = Seconds(marker.Time) }, new BoardCell(marker.Text)])
            .ToList();

        return rows.Count == 0
            ? null
            : new BoardPanel("events", "table", "Events", 12, TableHeight) { Table = new BoardTable(["When", "What"], rows) };
    }

    private List<BoardSection> EveryServer(BoardReads reads, ChartBuilder charts, List<MonitorServer> connected, DateTime from, DateTime to)
    {
        ChartsResponse compare = charts.Build("servers", string.Empty, from, to, HistoryDimension.Grids);
        Dictionary<string, ChartData> byId = compare.Charts.ToDictionary(chart => chart.Id);

        return
        [
            new BoardSection("Servers", [ServersTable(connected)]),
            new BoardSection("Side by side",
            [
                Time("allSim", "Sim speed", byId, "serversSim", 6, ChartHeight, levels: s_simLevels),
                Time("allAvailable", "Available frame time", byId, "serversAvailable", 6, ChartHeight),
                Time("allFrames", "Frame p99", byId, "serversFrames", 6, ChartHeight, levels: s_budget),
                Time("allPlayers", "Players online", byId, "serversPlayers", 6, ChartHeight),
            ]),
            new BoardSection("Where it goes",
            [
                HeatPanel("allLag", "Lost sim speed by server", LagHeat(reads, connected, from, to), " %", 12, "How far below full speed each server fell. Darker is better."),
                TreePanel("allMods", "Mods by server", ServerTree(reads, connected, HistoryDimension.Mods, from, to), 6, "A mod's box is its average over the range on show, inside its server."),
                TreePanel("allGrids", "Grids by server", ServerTree(reads, connected, HistoryDimension.Grids, from, to), 6, "A grid's box is its average over the range on show, inside its server."),
            ]),
        ];
    }

    private BoardPanel ServersTable(List<MonitorServer> connected)
    {
        List<IReadOnlyList<BoardCell>> rows = connected.Select(server =>
        {
            HistoryPoint? point = store.Latest(server.HistoryId);
            HistoryCensus? census = store.LatestCensus(server.HistoryId);

            return (IReadOnlyList<BoardCell>)
            [
                new BoardCell(server.Name) { Server = server.HistoryId },
                Coloured(point?.SimSpeed, "0.00", s_simBands),
                Coloured(point == null ? null : FrameParts.AvailableAtWorst(point), "0.0", s_availableBands),
                Coloured(point?.FrameP99, "0.0", s_frameBands),
                Plain(census?.OnlinePlayers),
                Plain(census?.Grids),
                Plain(census?.Pcu),
                Coloured(census?.CpuPercent, "0", s_shareBands),
            ];
        }).ToList();

        int height = 40 + (Math.Max(rows.Count, 1) * 30);

        return new BoardPanel("allTable", "table", "Now", 12, height)
        {
            Table = new BoardTable(["Server", "Sim speed", "Available, worst 1% (ms)", "Frame p99 (ms)", "Players", "Grids", "PCU", "CPU %"], rows),
        };
    }

    private static BoardCell Plain(uint? value) =>
        new(value?.ToString("N0", CultureInfo.InvariantCulture) ?? "-");

    private static BoardCell Coloured(double? value, string format, BoardBand[] bands) =>
        value is { } known
            ? new BoardCell(known.ToString(format, CultureInfo.InvariantCulture)) { Color = ColorOf(known, bands) }
            : new BoardCell("-");

    private static string ColorOf(double value, BoardBand[] bands) => Array.Find(bands, band => value <= band.Limit)!.Color;

    private static BoardHeat? Heat(BoardReads reads, string serverId, HistoryDimension list, DateTime from, DateTime to)
    {
        List<StoredGroup> rows = reads.Groups(serverId, list, from, to);

        if (rows.Count == 0)
            return null;

        double[] sweeps = rows.Select(row => Seconds(row.Time)).Distinct().Order().ToArray();
        double step = Math.Max(TypicalGap(sweeps), (to - from).TotalSeconds / HeatColumns);
        double start = sweeps[0];
        int columns = (int)((sweeps[^1] - start) / step) + 1;

        List<IGrouping<string, StoredGroup>> top = rows
            .GroupBy(row => row.Identity)
            .OrderByDescending(group => group.Sum(row => row.MainMs + row.WorkerMs))
            .Take(HeatRows)
            .ToList();

        // Two grids can share a name, so those two get their ids.
        HashSet<string> shared = top.GroupBy(group => group.First().Name).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();

        var names = new List<string>(top.Count);
        var cells = new double?[top.Count * columns];

        for (int row = 0; row < top.Count; row++)
        {
            string name = top[row].First().Name;
            names.Add(shared.Contains(name) ? name + " " + top[row].Key : name);

            foreach (StoredGroup group in top[row])
                Keep(cells, (row * columns) + (int)((Seconds(group.Time) - start) / step), group.MainMs + group.WorkerMs);
        }

        return new BoardHeat(names, start, step, columns, Highest(cells), cells);
    }

    // A step holds the worst of what fell in it, so a spike can't be averaged away.
    private static void Keep(double?[] cells, int at, double value)
    {
        double rounded = Math.Round(value, Rounding);

        if (cells[at] is not { } held || rounded > held)
            cells[at] = rounded;
    }

    private static double Highest(double?[] cells)
    {
        double highest = 0d;

        foreach (double? cell in cells)
        {
            if (cell > highest)
                highest = cell.Value;
        }

        return highest;
    }

    private static double TypicalGap(double[] times)
    {
        if (times.Length < 2)
            return 1d;

        var gaps = new double[times.Length - 1];

        for (int i = 0; i < gaps.Length; i++)
            gaps[i] = times[i + 1] - times[i];

        Array.Sort(gaps);
        return Math.Max(1d, gaps[gaps.Length / 2]);
    }

    private static BoardHeat? LagHeat(BoardReads reads, List<MonitorServer> connected, DateTime from, DateTime to)
    {
        List<List<HistoryPoint>> points = connected.Select(server => reads.Points(server.HistoryId, from, to, HeatColumns)).ToList();

        if (points.TrueForAll(rows => rows.Count == 0))
            return null;

        double start = points.SelectMany(rows => rows).Min(point => Seconds(point.Time));
        double end = points.SelectMany(rows => rows).Max(point => Seconds(point.Time));
        double step = Math.Max(ShortestLagStep, (end - start) / HeatColumns);
        int columns = (int)((end - start) / step) + 1;
        var cells = new double?[connected.Count * columns];

        for (int row = 0; row < connected.Count; row++)
        {
            foreach (HistoryPoint point in points[row])
                Keep(cells, (row * columns) + (int)((Seconds(point.Time) - start) / step), Math.Max(0d, (1d - point.SimSpeed) * 100d));
        }

        return new BoardHeat(connected.Select(server => server.Name).ToList(), start, step, columns, Highest(cells), cells);
    }

    // A row in only some totals counts for only those, so one brief spike doesn't read as a steady cost.
    private static double Share(WindowRow row) => row.TotalMs * row.SeenShare;

    private static BoardNode Leaf(string name, double value) =>
        new(name, Math.Round(value, Rounding), ChartColors.For(name), []);

    private static List<BoardNode> FlatTree(List<WindowRow> rows) =>
        rows.Where(row => Share(row) > 0d).Take(TreeRows).Select(row => Leaf(row.Name, Share(row))).ToList();

    // Parts name their mod, and a mod's time beyond its parts stays in the box, so its size matches the mod list.
    private static List<BoardNode> ModTree(List<WindowRow> mods, List<WindowRow> parts)
    {
        ILookup<string, WindowRow> partsOf = parts.ToLookup(ModOf);

        return mods
            .Where(mod => Share(mod) > 0d)
            .Take(TreeRows)
            .Select(mod =>
            {
                List<BoardNode> children = partsOf[mod.Name]
                    .Where(part => Share(part) > 0d)
                    .Select(part => Leaf(ModNames.PartName(part.Name), Share(part)))
                    .ToList();

                double rest = Share(mod) - children.Sum(child => child.Value);

                if (children.Count > 0 && rest > 0.001d)
                    children.Add(Leaf("Everything else", rest));

                return new BoardNode(mod.Name, Math.Round(Math.Max(Share(mod), children.Sum(child => child.Value)), Rounding), ChartColors.For(mod.Name), children);
            })
            .ToList();
    }

    private static string ModOf(WindowRow part)
    {
        int at = part.Name.LastIndexOf(ModNames.Separator, StringComparison.Ordinal);
        return at < 0 ? part.Name : part.Name.Substring(0, at);
    }

    private static List<BoardNode> ServerTree(BoardReads reads, List<MonitorServer> connected, HistoryDimension list, DateTime from, DateTime to)
    {
        var nodes = new List<BoardNode>(connected.Count);

        for (int server = 0; server < connected.Count; server++)
        {
            List<BoardNode> children = FlatTree(HistoryWindows.ToRows(reads.Groups(connected[server].HistoryId, list, from, to))).Take(TreeRowsPerServer).ToList();

            if (children.Count > 0)
                nodes.Add(new BoardNode(connected[server].Name, Math.Round(children.Sum(child => child.Value), Rounding), ChartColors.At(server), children));
        }

        return nodes;
    }

    private static BoardPanel Time(
        string id,
        string title,
        Dictionary<string, ChartData> byId,
        string chart,
        int span,
        int height,
        string? hint = null,
        IReadOnlyList<BoardLevel>? levels = null)
    {
        ChartData? data = byId.GetValueOrDefault(chart);
        return new BoardPanel(id, "time", title, span, height) { Chart = data, Unit = data?.Unit, Hint = hint, Levels = levels };
    }

    private static BoardPanel Stat(string id, string title, double? value, string unit, int decimals, ChartData? spark, BoardBand[]? bands) =>
        new(id, "stat", title, 2, StatHeight) { Value = value, Unit = unit, Decimals = decimals, Chart = spark, Bands = bands };

    private static BoardPanel Gauge(string id, string title, double? value, string unit, int decimals, double minimum, double maximum, BoardBand[] bands) =>
        new(id, "gauge", title, 3, GaugeHeight) { Value = value, Unit = unit, Decimals = decimals, Minimum = minimum, Maximum = maximum, Bands = bands };

    private static BoardPanel HeatPanel(string id, string title, BoardHeat? heat, string unit, int span, string hint) =>
        new(id, "heatmap", title, span, HeatHeight) { Heat = heat, Unit = unit, Hint = hint };

    private static BoardPanel TreePanel(string id, string title, List<BoardNode> tree, int span, string hint) =>
        new(id, "treemap", title, span, TreeHeight) { Tree = tree, Unit = " ms/f", Hint = hint };

    // A tile's small line has room for far fewer points than its chart, so it's averaged down to what it can show.
    private static ChartData? Spark(Dictionary<string, ChartData> byId, string chart, int line)
    {
        if (!byId.TryGetValue(chart, out ChartData? data) || line >= data.Lines.Count || data.Times.Length == 0)
            return null;

        double?[] values = data.Lines[line].Values;
        int count = Math.Min(StatPoints, data.Times.Length);
        var times = new double[count];
        var thinned = new double?[count];

        for (int bucket = 0; bucket < count; bucket++)
        {
            int first = bucket * values.Length / count;
            int end = (bucket + 1) * values.Length / count;
            double sum = 0d;
            int held = 0;

            for (int i = first; i < end; i++)
            {
                if (values[i] is { } value)
                {
                    sum += value;
                    held++;
                }
            }

            times[bucket] = data.Times[first];
            thinned[bucket] = held == 0 ? null : Math.Round(sum / held, Rounding);
        }

        return new ChartData(data.Id, data.Unit, false, data.Maximum, 0d, times, [data.Lines[line] with { Values = thinned }]);
    }

    private static double Seconds(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds() / 1000d;
}
