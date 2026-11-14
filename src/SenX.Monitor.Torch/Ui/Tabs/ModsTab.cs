using System.Windows;
using System.Windows.Controls;
using SenX.Monitor.Api;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>Time by whose code it is. Opening a row shows where that mod or plugin spends it.</summary>
public sealed class ModsTab : MonitorTab
{
    private readonly ChartBoard _mods;
    private readonly ChartBoard _partsBoard;
    private readonly Notice _nothing = new();
    private readonly ReportGrid _table;
    private readonly ReportGrid _parts;
    private readonly FlowGrid _partsCharts = new();
    private readonly TextBlock _partsHeading = Look.Heading();
    private readonly TextBlock _partsEmpty = Look.Hint("Nothing from this one made the latest total's top rows.");
    private readonly StackPanel _partsSection;
    private readonly ChartBox _partsChart;
    private string? _open;

    public ModsTab(MonitorView page) : base(page)
    {
        _mods = NewBoard();
        _partsBoard = NewBoard();

        _table = GroupTable.Numbers(new ReportGrid()
            .Toggled<ModRow>("Mod or plugin", nameof(ModRow.Name), nameof(ModRow.Glyph), row => Toggle(row.Name)));

        // The numbers are ModRow's and PartRow's, which have the same names as a stored row's.
        _parts = GroupTable.Numbers(new ReportGrid().Text("Part", nameof(PartRow.Name)))
            .Button<PartRow>("Look inside", row => Page.LookInside(row.Name));

        var charts = new FlowGrid();
        charts.Children.Add(_mods.Chart("mods", "Mods and plugins, costliest 6", wide: true));

        _partsChart = _partsBoard.Chart("modParts", "Inside this one, costliest 6", wide: true);
        _partsCharts.Children.Add(_partsChart);
        _partsSection = Look.Stack(_partsHeading, _partsCharts, _partsEmpty, _parts);
        _partsSection.Visibility = Visibility.Collapsed;

        Add(
            Look.Hint("Time by whose code it is, from background totals, so use this for trends and a run to settle a cause. Open a row to see where that mod or plugin spends its time."),
            _nothing,
            charts,
            _table,
            _partsSection);
    }

    public override void Shown()
    {
        _mods.Show(new ChartRequest("mods", ServerId, RangeMinutes));

        if (_open != null)
            _partsBoard.Show(new ChartRequest("modparts", ServerId, RangeMinutes, Mod: _open));

        Refresh();
    }

    private void Toggle(string mod)
    {
        _open = mod == _open ? null : mod;
        _partsSection.Visibility = Look.VisibleIf(_open != null);
        _partsHeading.Text = "Where " + _open + " spends its time";

        if (_open != null)
            _partsBoard.Show(new ChartRequest("modparts", ServerId, RangeMinutes, Mod: _open));
        else
            _partsBoard.Hide();

        Refresh();
    }

    public override void Refresh()
    {
        List<StoredGroup> mods = Services.Store.LatestGroups(ServerId, HistoryDimension.Mods);

        _nothing.Text = mods.Count == 0 ? "No mod or plugin code in the latest total. Totals arrive every 30 seconds while detail is on." : null;
        _table.Visibility = Look.VisibleIf(mods.Count > 0);
        _table.Show(mods.Select(mod => new ModRow(mod.Name, mod.MainMs, mod.WorkerMs, mod.PeakMs, mod.CallsPerFrame, mod.Name == _open ? "-" : "+")).ToList());

        if (_open == null)
            return;

        string prefix = ModNames.PartsOf(_open);

        List<PartRow> parts = Services.Store.LatestGroups(ServerId, HistoryDimension.ModParts)
            .Where(part => part.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(part => new PartRow(ModNames.PartName(part.Name), part.MainMs, part.WorkerMs, part.PeakMs, part.CallsPerFrame))
            .ToList();

        _partsEmpty.Visibility = Look.VisibleIf(parts.Count == 0);
        _parts.Visibility = Look.VisibleIf(parts.Count > 0);
        _parts.Show(parts);
    }

    public sealed record ModRow(string Name, float MainMs, float WorkerMs, float PeakMs, float CallsPerFrame, string Glyph)
    {
        public string Thread => ThreadTags.Name(MonitorThreads.Of(MainMs, WorkerMs));

        public double MainPercent => ThreadTags.MainPercent(MainMs, WorkerMs);
    }

    public sealed record PartRow(string Name, float MainMs, float WorkerMs, float PeakMs, float CallsPerFrame)
    {
        public string Thread => ThreadTags.Name(MonitorThreads.Of(MainMs, WorkerMs));

        public double MainPercent => ThreadTags.MainPercent(MainMs, WorkerMs);
    }
}
