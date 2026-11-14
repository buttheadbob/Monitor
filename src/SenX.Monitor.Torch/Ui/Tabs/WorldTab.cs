using System.Globalization;
using System.Windows.Controls;
using SenX.Monitor.Components.Charts;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>Who's on and what's built: facts that aren't timings, from the census.</summary>
public sealed class WorldTab : MonitorTab
{
    private const int SlicesShown = 12;

    private readonly ChartBoard _board;
    private readonly Donut _blocksByFaction = new(unit: string.Empty, smallest: 1d);
    private readonly Donut _membersByFaction = new(unit: string.Empty, smallest: 1d);
    private readonly ReportGrid _offline;
    private readonly ReportGrid _capacity;
    private readonly ReportGrid _players;
    private readonly ReportGrid _factions;
    private readonly StackPanel _offlineSection;
    private readonly TextBlock _capacityNote = Look.Hint();

    public WorldTab(MonitorView page) : base(page)
    {
        _board = NewBoard();

        var charts = new FlowGrid();
        charts.Children.Add(_board.Chart("online", "Who was online", wide: true));
        charts.Children.Add(_board.Chart("players", "Players online"));
        charts.Children.Add(_board.Chart("blocks", "Total blocks"));
        charts.Children.Add(_board.Chart("pcu", "PCU"));
        charts.Children.Add(_board.Chart("ping", "Server ping"));
        charts.Children.Add(_blocksByFaction.InBox("Blocks by faction"));
        charts.Children.Add(_membersByFaction.InBox("Online members by faction"));

        _offline = ThreadColumn.Add(new ReportGrid()
            .Text("Owner", nameof(StoredGroup.Name))
            .Number("Main ms/f", nameof(StoredGroup.MainMs))
            .Number("Workers ms/f", nameof(StoredGroup.WorkerMs)));

        _capacity = new ReportGrid()
            .Text("Players", nameof(CapacityLine.Players), stretch: false)
            .Number("Frame ms", nameof(CapacityLine.FrameMs), "0.0")
            .Number("Worst 1% ms", nameof(CapacityLine.WorstMs), "0.0")
            .Number("Census passes", nameof(CapacityLine.Moments), "0");

        _players = new ReportGrid()
            .Text("Player", nameof(WorldPlayer.Name))
            .Text("Faction", nameof(WorldPlayer.Faction))
            .Number("Ping ms", nameof(WorldPlayer.PingMs), "0");

        _factions = new ReportGrid()
            .Text("Faction", nameof(WorldFaction.Name))
            .Number("Online", nameof(WorldFaction.Online), "0")
            .Number("Grids", nameof(WorldFaction.Grids), "0")
            .Number("Blocks", nameof(WorldFaction.Blocks), "N0")
            .Number("PCU", nameof(WorldFaction.Pcu), "N0");

        _offlineSection = Look.Section(
            "Costing time while offline",
            Look.Hint("From the latest background total, so it only shows owners in that total's top 20."),
            _offline);

        _capacity.Visibility = System.Windows.Visibility.Collapsed;

        var tables = new FlowGrid(380d, 14d);
        tables.Children.Add(_offlineSection);
        tables.Children.Add(Look.Section(
            "Capacity",
            Look.Hint("How frame time has moved with players, over all the history kept. This reads a lot of files, so it only runs when asked."),
            Look.Row(Look.Action("Work it out", (_, _) => WorkOutCapacity())),
            _capacity,
            _capacityNote));
        tables.Children.Add(Look.Section("Players online", _players));
        tables.Children.Add(Look.Section("Factions", _factions));

        Add(charts, tables);
    }

    public override void Shown()
    {
        _board.Show(new ChartRequest("world", ServerId, RangeMinutes));
        Refresh();
    }

    public override void Refresh()
    {
        WorldNow world = Services.Store.World(ServerId);

        _blocksByFaction.Show(Slices(world, faction => faction.Blocks));
        _membersByFaction.Show(Slices(world, faction => faction.OnlineMembers));

        // Both lists get their names from the same lookup, so a name is enough to match them.
        HashSet<string> online = world.Players.Select(player => NameFor(HistoryDimension.Owners, player.IdentityId)).ToHashSet();
        List<StoredGroup> offline = Services.Store.LatestGroups(ServerId, HistoryDimension.Owners).Where(owner => !online.Contains(owner.Name)).ToList();

        _offlineSection.Visibility = Look.VisibleIf(offline.Count > 0);
        _offline.Show(offline);

        _players.Show(world.Players
            .OrderByDescending(player => player.PingMs)
            .Select(player => new WorldPlayer(NameFor(HistoryDimension.Owners, player.IdentityId), NameFor(HistoryDimension.Factions, player.FactionId), player.PingMs))
            .ToList());

        _factions.Show(world.Factions
            .OrderByDescending(faction => faction.Blocks)
            .Select(faction => new WorldFaction(NameFor(HistoryDimension.Factions, faction.FactionId), faction.OnlineMembers, faction.Grids, faction.Blocks, faction.Pcu))
            .ToList());
    }

    private void WorkOutCapacity()
    {
        string server = ServerId;
        DateTime from = DateTime.UtcNow - Services.Settings.Retention;

        Guarded(
            async () =>
            {
                CapacityReport report = await Task.Run(() => Services.Windows.Capacity(server, from, DateTime.UtcNow));

                _capacity.Visibility = Look.VisibleIf(report.Rows.Count > 0);
                _capacity.Show(report.Rows.Select(row => new CapacityLine(row.PlayersFrom + " to " + row.PlayersTo, row.FrameMs, row.WorstMs, row.Moments)).ToList());

                _capacityNote.Text = report.Rows.Count == 0
                    ? "There isn't enough history yet."
                    : report.PlayersAtBudget is { } players
                        ? "A straight line through this reaches the frame budget at about " + players + " players. Treat it as a guide, because load doesn't always grow in a straight line."
                        : "The history doesn't fit a line well enough to say where the limit is.";
            },
            message => _capacityNote.Text = message);
    }

    private List<ChartSlice> Slices(WorldNow world, Func<HistoryFaction, double> value) => world.Factions
        .OrderByDescending(value)
        .Take(SlicesShown)
        .Select(faction =>
        {
            string name = NameFor(HistoryDimension.Factions, faction.FactionId);
            return new ChartSlice(name, ChartColors.For(name), value(faction));
        })
        .ToList();

    private string NameFor(HistoryDimension kind, long key) =>
        Services.Collector.NameOf(ServerId, kind, key) ?? key.ToString(CultureInfo.InvariantCulture);

    public sealed record CapacityLine(string Players, double FrameMs, double WorstMs, int Moments);

    public sealed record WorldPlayer(string Name, string Faction, uint PingMs);

    public sealed record WorldFaction(string Name, uint Online, uint Grids, uint Blocks, uint Pcu);
}
