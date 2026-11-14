using System.Windows.Controls;
using SenX.Monitor.Components.Charts;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>Any one list from the background totals, as a share, over time, and in full.</summary>
public sealed class CostsTab : MonitorTab
{
    private static readonly (string Name, HistoryDimension List)[] s_lists =
    [
        ("Grids", HistoryDimension.Grids),
        ("Block types", HistoryDimension.BlockTypes),
        ("Block definitions", HistoryDimension.Definitions),
        ("Players", HistoryDimension.Owners),
        ("Factions", HistoryDimension.Factions),
        ("Entity types", HistoryDimension.EntityTypes),
        ("Session components", HistoryDimension.Session),
        ("Game logic", HistoryDimension.Logic),
        ("Callbacks", HistoryDimension.Callbacks),
        ("RPCs", HistoryDimension.Rpcs),
        ("Scripts", HistoryDimension.Scripts),
        ("Mods and plugins", HistoryDimension.Mods),
    ];

    private readonly ChartBoard _board;
    private readonly ComboBox _list = new() { Width = 170d };
    private readonly Notice _nothing = new();
    private readonly Donut _share = new();
    private readonly FigureBox _shareBox;
    private readonly ChartBox _chart;
    private readonly ReportGrid _rows = GroupTable.Costs();

    public CostsTab(MonitorView page) : base(page)
    {
        _board = NewBoard();
        _shareBox = _share.InBox(s_lists[0].Name + " now");

        foreach ((string name, _) in s_lists)
            _list.Items.Add(name);

        _list.SelectedIndex = 0;
        _list.SelectionChanged += (_, _) => Shown();

        var charts = new FlowGrid();
        charts.Children.Add(_shareBox);
        _chart = _board.Chart("costs", s_lists[0].Name);
        charts.Children.Add(_chart);

        Add(
            Look.Row(Look.Field("List", _list), Look.Hint("Background numbers. For a lag cause, use a run: it times every call.")),
            _nothing,
            charts,
            _rows);
    }

    private (string Name, HistoryDimension List) Chosen => s_lists[Math.Max(0, _list.SelectedIndex)];

    public override void Shown()
    {
        _shareBox.Title = Chosen.Name + " now";
        _chart.Title = Chosen.Name;
        _board.Show(new ChartRequest("costs", ServerId, RangeMinutes, Chosen.List));
        Refresh();
    }

    public override void Refresh()
    {
        List<StoredGroup> rows = Services.Store.LatestGroups(ServerId, Chosen.List);

        _nothing.Text = rows.Count == 0 ? "No totals yet. They arrive every 30 seconds while detail is on." : null;
        _rows.Visibility = Look.VisibleIf(rows.Count > 0);
        _rows.Show(rows);
        _share.Show(rows.Select(row => new ChartSlice(row.Name, ChartColors.For(row.Name), row.MainMs + row.WorkerMs)));
    }
}

/// <summary>The table every list of stored costs uses.</summary>
internal static class GroupTable
{
    public static ReportGrid Costs() => Numbers(new ReportGrid().Text("Name", nameof(StoredGroup.Name)));

    // Rows need Thread and MainPercent too, for the thread column.
    public static ReportGrid Numbers(ReportGrid table) => ThreadColumn.Add(table
        .Number("Main ms/f", nameof(StoredGroup.MainMs))
        .Number("Workers ms/f", nameof(StoredGroup.WorkerMs))
        .Number("Peak ms", nameof(StoredGroup.PeakMs))
        .Number("Calls/f", nameof(StoredGroup.CallsPerFrame), "0.#"));
}
