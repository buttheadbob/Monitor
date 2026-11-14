using System.Globalization;
using System.Windows.Controls;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>What the server is sending, to whom, and what clients are asking of it.</summary>
public sealed class NetworkTab : MonitorTab
{
    private readonly ChartBoard _board;
    private readonly Tile _sending = new("Sending");
    private readonly Tile _receiving = new("Receiving");
    private readonly Tile _rpcsIn = new("RPCs from clients");
    private readonly WrapPanel _tiles;
    private readonly ReportGrid _players;
    private readonly ReportGrid _sent;
    private readonly ReportGrid _rpcs;
    private readonly Notice _noSent = new();
    private readonly Notice _noRpcs = new();

    public NetworkTab(MonitorView page) : base(page)
    {
        _board = NewBoard();
        _tiles = Look.Tiles(_sending, _receiving, _rpcsIn);

        var charts = new FlowGrid();
        charts.Children.Add(_board.Chart("traffic", "Sent and received"));
        charts.Children.Add(_board.Chart("trafficKinds", "What's being sent"));
        charts.Children.Add(_board.Chart("rpcRate", "RPCs from clients"));
        charts.Children.Add(_board.Chart("rpcs", "RPCs received, costliest 6"));

        _players = new ReportGrid()
            .Text("Player", nameof(PlayerRow.Name))
            .Number("Down KB/s", nameof(PlayerRow.DownKb), "0.0")
            .Number("Up KB/s", nameof(PlayerRow.UpKb), "0.0")
            .Number("RPCs/s", nameof(PlayerRow.Rpcs), "0")
            .Number("Queued KB", nameof(PlayerRow.QueuedKb), "0.0")
            .Number("Refused", nameof(PlayerRow.Refused), "0")
            .Number("Ping ms", nameof(PlayerRow.PingMs), "0");

        _sent = new ReportGrid()
            .Text("RPC", nameof(SentRow.Name))
            .Number("KB/s", nameof(SentRow.Kb), "0.0")
            .Number("Events/f", nameof(SentRow.CallsPerFrame), "0.#");

        _rpcs = new ReportGrid()
            .Text("RPC", nameof(StoredGroup.Name))
            .Number("Main ms/f", nameof(StoredGroup.MainMs))
            .Number("Peak ms", nameof(StoredGroup.PeakMs))
            .Number("Calls/f", nameof(StoredGroup.CallsPerFrame), "0.#")
            .Button<StoredGroup>("Look inside", row => Page.LookInside(row.Name));

        var tables = new FlowGrid(380d, 14d);
        tables.Children.Add(Look.Section(
            "Players now",
            Look.Hint("Averages since the last census, about 30 seconds. Steam reports what's queued, and EOS doesn't, so EOS players always show 0 there."),
            _players));
        tables.Children.Add(Look.Section(
            "RPCs sent",
            Look.Hint("Bytes across everyone who got each one, from the latest total. This is what to look at when events are most of what's being sent. State sync and replication aren't RPCs, so they aren't here."),
            _noSent,
            _sent));
        tables.Children.Add(Look.Section(
            "RPCs from clients",
            Look.Hint("From background totals, so use this for trends and !monitor rpcs or a run to settle a cause. Each one's time covers checking it and running it."),
            _noRpcs,
            _rpcs));

        Add(
            Look.Hint("Bytes are counted all the time, as they're handed to Steam or EOS and as they arrive. Sim speed can read 1.00 while players rubberband, and that usually shows here: a queue that keeps growing means a player can't take what the server's sending."),
            _tiles,
            charts,
            tables);
    }

    public override void Shown()
    {
        _board.Show(new ChartRequest("network", ServerId, RangeMinutes));
        Refresh();
    }

    public override void Refresh()
    {
        HistoryPoint? latest = Services.Store.Latest(ServerId);

        _tiles.Visibility = Look.VisibleIf(latest != null);
        _sending.Value = Look.Num(latest?.SentKb, "0.0") + " KB/s";
        _receiving.Value = Look.Num(latest?.ReceivedKb, "0.0") + " KB/s";
        _rpcsIn.Value = Look.Num(latest?.RpcsIn, "0") + "/s";

        _players.Show(Services.Store.World(ServerId).Players
            .OrderByDescending(player => player.SentBytesPerSecond)
            .Select(player => new PlayerRow(
                Services.Collector.NameOf(ServerId, HistoryDimension.Owners, player.IdentityId) ?? player.IdentityId.ToString(CultureInfo.InvariantCulture),
                player.SentBytesPerSecond / 1024d,
                player.ReceivedBytesPerSecond / 1024d,
                player.RpcsPerSecond,
                player.QueuedBytes / 1024d,
                player.FailedSends,
                player.PingMs))
            .ToList());

        const string NoRows = "No RPCs in the latest total. Totals arrive every 30 seconds while detail is on.";

        List<StoredGroup> sent = Services.Store.LatestGroups(ServerId, HistoryDimension.RpcsSent);
        _noSent.Text = sent.Count == 0 ? NoRows : null;
        _sent.Show(sent.Select(rpc => new SentRow(rpc.Name, rpc.BytesPerSecond / 1024f, rpc.CallsPerFrame)).ToList());

        List<StoredGroup> rpcs = Services.Store.LatestGroups(ServerId, HistoryDimension.Rpcs);
        _noRpcs.Text = rpcs.Count == 0 ? NoRows : null;
        _rpcs.Show(rpcs);
    }

    public sealed record PlayerRow(string Name, double DownKb, double UpKb, uint Rpcs, double QueuedKb, uint Refused, uint PingMs);

    public sealed record SentRow(string Name, float Kb, float CallsPerFrame);
}
