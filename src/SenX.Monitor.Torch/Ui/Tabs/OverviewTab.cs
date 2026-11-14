using System.Globalization;
using System.Windows.Controls;
using SenX.Monitor.Components.Charts;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>A place on screen for each thing worth a glance, then what's using the frame.</summary>
public sealed class OverviewTab : MonitorTab
{
    private const int EventRows = 20;
    private const int NewCostsShown = 5;

    private readonly ChartBoard _board;
    private readonly Notice _nothing = new();
    private readonly Notice _unusual = new();
    private readonly Notice _newCosts = new();
    private readonly Gauge _sim = new();
    private readonly Tile _available = new("Available", "Time left in a frame before sim speed starts to fall, over the last 30 seconds.");
    private readonly Tile _availableAtWorst = new("Available, worst 1%", "The same, in the worst 1% of frames. This is the room there really is when things get busy.");
    private readonly Tile _frameP99 = new("Frame p99");
    private readonly Tile _players = new("Players online");
    private readonly Tile _ping = new("Server ping");
    private readonly Tile _sending = new("Sending");
    private readonly Tile _grids = new("Grids");
    private readonly Tile _blocks = new("Blocks");
    private readonly Tile _pcu = new("PCU");
    private readonly Tile _memory = new("Memory");
    private readonly Tile _gc = new("GC mode");
    private readonly Donut _now = new();
    private readonly ReportGrid _events = new ReportGrid().Text("When", nameof(EventRow.When), stretch: false).Text("What", nameof(EventRow.What));
    private readonly StackPanel _eventsSection;
    private readonly ReportGrid _jumps = new ReportGrid().Text("When", nameof(EventRow.When), stretch: false).Text("What", nameof(EventRow.What));
    private readonly StackPanel _jumpsSection;
    private readonly WrapPanel _tiles;

    public OverviewTab(MonitorView page) : base(page)
    {
        _board = NewBoard();

        var charts = new FlowGrid();
        charts.Children.Add(_board.Chart("cause", "What's using the frame, against sim speed", wide: true));
        charts.Children.Add(_board.Chart("sim", "Sim speed"));
        charts.Children.Add(_board.Chart("headroom", "Available frame time"));
        charts.Children.Add(_now.InBox("Main thread now"));

        _eventsSection = Look.Section("Events", Look.Hint("Dashed lines on the charts mark these. Saves aren't drawn on ranges over 6 hours."), _events);
        _jumpsSection = Look.Section(
            "Thread changes",
            Look.Hint("Things whose time moved between the game thread and worker threads, and stayed for two background totals. Only game thread time can set off a lag alert, so these never do."),
            _jumps);

        _tiles = Look.Tiles(_sim.InBox("Sim speed, last 30 s"), _available, _availableAtWorst, _frameP99, _players, _ping, _sending, _grids, _blocks, _pcu, _memory, _gc);

        Add(
            _nothing,
            _tiles,
            _unusual,
            _newCosts,
            charts,
            _jumpsSection,
            _eventsSection);
    }

    public override void Shown()
    {
        _board.Show(new ChartRequest("overview", ServerId, RangeMinutes));
        Refresh();
    }

    public override void Refresh()
    {
        HistoryStore store = Services.Store;
        HistoryPoint? latest = store.Latest(ServerId);
        HistoryCensus? census = store.LatestCensus(ServerId);
        List<HistoryPoint> last = store.Points(ServerId, DateTime.UtcNow.AddSeconds(-30), DateTime.UtcNow, 60);

        _nothing.Text = latest == null ? "Nothing collected from this server yet. It arrives within 30 seconds of the server starting." : null;
        _tiles.Visibility = Look.VisibleIf(latest != null);

        _sim.Value = last.Count == 0 ? 0d : last.Average(point => point.SimSpeed);
        double? available = last.Count == 0 ? null : last.Average(FrameParts.Available);
        double? availableAtWorst = last.Count == 0 ? null : last.Min(FrameParts.AvailableAtWorst);
        _available.Value = Look.Num(available, "0.0") + " ms";
        _availableAtWorst.Value = Look.Num(availableAtWorst, "0.0") + " ms";
        _frameP99.Value = Look.Num(latest?.FrameP99, "0.0") + " ms";
        _players.Value = census?.OnlinePlayers.ToString(CultureInfo.CurrentCulture) ?? "-";
        _ping.Value = Look.Num(census?.PingMean, "0.0") + " ms";
        _sending.Value = Look.Num(latest?.SentKb, "0") + " KB/s";
        _grids.Value = census?.Grids.ToString(CultureInfo.CurrentCulture) ?? "-";
        _blocks.Value = census?.Blocks.ToString("N0", CultureInfo.CurrentCulture) ?? "-";
        _pcu.Value = census?.Pcu.ToString("N0", CultureInfo.CurrentCulture) ?? "-";
        _memory.Value = (census?.WorkingSetMb.ToString("N0", CultureInfo.CurrentCulture) ?? "-") + " MB";
        _gc.Value = store.GcMode(ServerId);

        List<string> unusual = Services.Baselines.UnusualNow(ServerId);
        _unusual.Text = unusual.Count > 0 ? "Unusual for this hour: " + string.Join("; ", unusual) + "." : null;

        List<NewCost> newCosts = Services.Baselines.NewCosts(ServerId);
        _newCosts.Text = newCosts.Count > 0 ? "New or jumped: " + string.Join("; ", newCosts.Take(NewCostsShown).Select(Describe)) + "." : null;

        double[] now = latest == null ? [] : FrameParts.Split(latest);
        _now.Show(now.Select((value, index) => new ChartSlice(FrameParts.All[index].Name, FrameParts.All[index].Color, value)));

        List<StoredMarker> markers = store.Markers(ServerId, DateTime.UtcNow.AddMinutes(-RangeMinutes), DateTime.UtcNow);

        List<EventRow> events = markers
            .Where(marker => marker.Kind != StoredMarkerKind.ThreadJump)
            .Take(EventRows)
            .Select(marker => new EventRow(marker.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), marker.Text))
            .ToList();

        List<EventRow> jumps = markers
            .Where(marker => marker.Kind == StoredMarkerKind.ThreadJump)
            .Take(EventRows)
            .Select(marker => new EventRow(marker.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), marker.Text))
            .ToList();

        _eventsSection.Visibility = Look.VisibleIf(events.Count > 0);
        _events.Show(events);
        _jumpsSection.Visibility = Look.VisibleIf(jumps.Count > 0);
        _jumps.Show(jumps);
    }

    private static string Describe(NewCost cost) => cost.List + " " + cost.Name + " " + Look.Num(cost.NowMs, "0.0") + " ms/f"
        + (cost.NormalMs is { } normal ? ", usually " + Look.Num(normal, "0.0") : ", not seen before");

    public sealed record EventRow(string When, string What);
}
