using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>Starts runs and shows them, which is the accurate way to find a lag cause.</summary>
public sealed class RunsTab : MonitorTab
{
    private readonly NumberBox _seconds = new(10d);
    private readonly CheckBox _clusters = Look.Check("Time physics clusters", on: true);
    private readonly TextBox _player = Look.Input(110d);
    private readonly TextBox _faction = Look.Input(110d);
    private readonly TextBox _blockType = Look.Input(110d);
    private readonly Button _start;
    private readonly Notice _message = new();
    private readonly Notice _problem = new();
    private readonly ReportGrid _saved;
    private readonly StackPanel _shownPanel = new();
    private List<SavedRunInfo> _runs = [];
    private string? _shownId;
    private RunResultResponse? _shown;

    public RunsTab(MonitorView page) : base(page)
    {
        _start = Look.Action("Start run", (_, _) => Start());

        _saved = new ReportGrid()
            .Text("Saved runs", nameof(SavedLine.When))
            .Number("Length (s)", nameof(SavedLine.Seconds), "0")
            .Number("Sim speed", nameof(SavedLine.SimSpeed))
            .Number("Frame average ms", nameof(SavedLine.AverageFrameMs), "0.0")
            .Button<SavedLine>("View", row => Show(row.Id))
            .Button<SavedLine>("Compare", row => CompareWith(row.Id, row.When))
            .Button<SavedLine>("Delete", row => Delete(row.Id));

        Add(
            Look.Hint("A run times every call for its length, then puts detail back as it was. This is the accurate way to find a lag cause. Results stay here for as long as history is kept."),
            Look.Row(
                Look.Field("Seconds", _seconds),
                _clusters,
                Look.Field("Player", _player, "Leave these empty to measure everything. A name, an identity id, or sid/STEAMID."),
                Look.Field("Faction", _faction, "A tag, a name or a faction id."),
                Look.Field("Block type", _blockType, "Part of a block class name, such as Drill."),
                _start),
            _message,
            _problem,
            _saved,
            _shownPanel);

        // A run that finishes shows itself, wherever the admin was on this page.
        Services.Runs.Changed += () => Dispatcher.BeginInvoke(new Action(OnRunsChanged));
    }

    private bool Running => Services.Runs.Running(ServerId);

    // Runs have their own rhythm, and redrawing under someone typing in a box helps nobody.
    protected override int RefreshSeconds => 0;

    public override void Shown()
    {
        LoadList();
        _problem.Text = Services.Runs.Problem(ServerId);
        _start.IsEnabled = !Running;
        _start.Content = Running ? "Measuring..." : "Start run";

        if (_shownId == null && _runs.Count > 0)
            Show(_runs[0].Id);
    }

    private void LoadList()
    {
        _runs = Services.Runs.List(ServerId);
        _saved.Visibility = Look.VisibleIf(_runs.Count > 0);
        _saved.Show(_runs.Select(run => new SavedLine(run.Id, run.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), run.Seconds, run.SimSpeed, run.AverageFrameMs)).ToList());
    }

    private void OnRunsChanged()
    {
        LoadList();
        _problem.Text = Services.Runs.Problem(ServerId);
        _start.IsEnabled = !Running;
        _start.Content = Running ? "Measuring..." : "Start run";

        if (!Running && _runs.Count > 0 && _runs[0].Id != _shownId && !_runs[0].ByLagTrip)
            Show(_runs[0].Id);
    }

    private void Start() => Guarded(
        async () =>
        {
            if (Running)
                return;

            _message.Text = null;

            var request = new RunStartRequest
            {
                Seconds = Math.Clamp(_seconds.Whole, 1, 600),
                Clusters = _clusters.IsChecked == true,
                Player = _player.Text,
                Faction = _faction.Text,
                BlockType = _blockType.Text,
            };

            _message.Text = await Services.Runs.StartAsync(Page.Server, request, CancellationToken.None);
            OnRunsChanged();
        },
        message => _message.Text = message);

    private void Show(string id)
    {
        _shownId = id;
        _shown = Services.Runs.Load(ServerId, id);
        _shownPanel.Children.Clear();

        if (_shown != null)
            _shownPanel.Children.Add(new RunReportView(_shown));
    }

    private void CompareWith(string id, string when)
    {
        if (id == _shownId || _shown is not { } result || Services.Runs.Load(ServerId, id) is not { } baseline)
            return;

        _shownPanel.Children.Clear();

        _shownPanel.Children.Add(Look.Heading("Comparing with the run from " + when));
        _shownPanel.Children.Add(Look.Tiles(
            new Tile("Sim speed") { Value = Look.Num(baseline.SimSpeed, "0.00") + " to " + Look.Num(result.SimSpeed, "0.00") },
            new Tile("Frame average") { Value = Look.Num(baseline.AverageFrameMs, "0.0") + " to " + Look.Num(result.AverageFrameMs, "0.0") + " ms" },
            new Tile("Frame worst") { Value = Look.Num(baseline.WorstFrameMs, "0.0") + " to " + Look.Num(result.WorstFrameMs, "0.0") + " ms" }));
        _shownPanel.Children.Add(Look.Hint("Lists hold each run's top rows, so a dash means a row wasn't in that run's top, not that it cost nothing."));

        var lists = new FlowGrid(380d, 14d);

        foreach ((string title, string list) in RunReportView.Lists)
        {
            if (list == RunLists.Clusters)
                continue;

            List<ComparedRow> rows = RunComparison.Compare(baseline, result, list);

            if (rows.Count > 0)
                lists.Children.Add(Look.Section(title, ComparedTable.Build(rows)));
        }

        _shownPanel.Children.Add(lists);
    }

    private void Delete(string id)
    {
        Services.Runs.Delete(ServerId, id);
        LoadList();

        if (id == _shownId)
        {
            _shownId = null;
            _shown = null;
            _shownPanel.Children.Clear();
        }
    }

    public sealed record SavedLine(string Id, string When, double Seconds, double SimSpeed, double AverageFrameMs);
}
