using System.Globalization;
using System.Windows.Controls;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>Two stretches of history side by side, such as before and after a restart.</summary>
public sealed class CompareTab : MonitorTab
{
    private const int RowsPerList = 10;
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    private static readonly (string Name, HistoryDimension List)[] s_lists =
    [
        ("Grids", HistoryDimension.Grids),
        ("Block types", HistoryDimension.BlockTypes),
        ("Players", HistoryDimension.Owners),
        ("Factions", HistoryDimension.Factions),
        ("Session components", HistoryDimension.Session),
        ("Game logic", HistoryDimension.Logic),
        ("Scripts", HistoryDimension.Scripts),
    ];

    private readonly ComboBox _events = new() { Width = 340d };
    private readonly NumberBox _minutes = new(30d);
    private readonly TextBox _beforeFrom = TimeBox(-2d);
    private readonly TextBox _beforeTo = TimeBox(-1d);
    private readonly TextBox _afterFrom = TimeBox(-1d);
    private readonly TextBox _afterTo = TimeBox(0d);
    private readonly Notice _message = new();
    private readonly StackPanel _result = new();
    private List<StoredMarker> _markers = [];
    private bool _filling;

    public CompareTab(MonitorView page) : base(page)
    {
        _events.SelectionChanged += (_, _) => EventChanged();

        Add(
            Look.Hint("Compares two stretches of history, such as before and after a restart or a mod change. These are background numbers, so use them for trends, and use a run to settle a cause."),
            Look.Row(Look.Field("Around", _events), Look.Field("Minutes each side", _minutes)),
            Look.Row(
                Look.Field("Before, from", _beforeFrom),
                Look.Field("to", _beforeTo),
                Look.Field("After, from", _afterFrom),
                Look.Field("to", _afterTo),
                Look.Action("Compare", (_, _) => Run())),
            _message,
            _result);
    }

    // Redrawing under someone typing in a box helps nobody.
    protected override int RefreshSeconds => 0;

    private static TextBox TimeBox(double hours) =>
        Look.Input(120d, DateTime.Now.AddHours(hours).ToString(TimeFormat, CultureInfo.CurrentCulture));

    public override void Shown()
    {
        // This also runs when the range changes, which has nothing to do with this tab's own choices.
        if (_events.SelectedIndex > 0)
            return;

        _markers = Services.Store.Markers(ServerId, DateTime.UtcNow.AddDays(-MonitorSettings.MaxRetentionDays), DateTime.UtcNow)
            .Where(marker => marker.Kind is not StoredMarkerKind.Save and not StoredMarkerKind.ThreadJump)
            .ToList();

        _filling = true;
        _events.Items.Clear();
        _events.Items.Add("Pick an event");

        foreach (StoredMarker marker in _markers)
            _events.Items.Add(marker.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + ": " + marker.Text);

        _events.SelectedIndex = 0;
        _filling = false;
    }

    private void EventChanged()
    {
        if (_filling || _events.SelectedIndex < 1 || _events.SelectedIndex > _markers.Count)
            return;

        DateTime time = _markers[_events.SelectedIndex - 1].Time.ToLocalTime();
        double minutes = _minutes.Number;

        _beforeFrom.Text = time.AddMinutes(-minutes).ToString(TimeFormat, CultureInfo.CurrentCulture);
        _beforeTo.Text = time.ToString(TimeFormat, CultureInfo.CurrentCulture);
        _afterFrom.Text = _beforeTo.Text;
        _afterTo.Text = time.AddMinutes(minutes).ToString(TimeFormat, CultureInfo.CurrentCulture);
        Run();
    }

    private static bool TryRead(TextBox box, out DateTime time) =>
        DateTime.TryParseExact(box.Text.Trim(), TimeFormat, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out time);

    private void Run()
    {
        _result.Children.Clear();

        if (!TryRead(_beforeFrom, out DateTime beforeFrom) || !TryRead(_beforeTo, out DateTime beforeTo)
            || !TryRead(_afterFrom, out DateTime afterFrom) || !TryRead(_afterTo, out DateTime afterTo))
        {
            _message.Text = "Times need to look like " + DateTime.Now.ToString(TimeFormat, CultureInfo.CurrentCulture) + ".";
            return;
        }

        if (beforeFrom >= beforeTo || afterFrom >= afterTo)
        {
            _message.Text = "Each stretch has to end after it starts.";
            return;
        }

        string server = ServerId;

        Guarded(
            async () =>
            {
                // These read a lot of files, and the page shouldn't stop while they do.
                Compared compared = await Task.Run(() => Compare(server, beforeFrom.ToUniversalTime(), beforeTo.ToUniversalTime(), afterFrom.ToUniversalTime(), afterTo.ToUniversalTime()));
                Show(compared);
            },
            message => _message.Text = message);
    }

    private Compared Compare(string server, DateTime beforeFrom, DateTime beforeTo, DateTime afterFrom, DateTime afterTo)
    {
        HistoryWindows windows = Services.Windows;
        WindowSummary before = windows.Summarize(server, beforeFrom, beforeTo);
        WindowSummary after = windows.Summarize(server, afterFrom, afterTo);

        if (!before.HasData || !after.HasData)
            return new Compared(before, after, [], []);

        List<ComparedRow> parts = RunComparison.Join(Parts(before), Parts(after));

        List<(string Title, List<ComparedRow> Rows)> lists = s_lists
            .Select(list => (list.Name, HistoryWindows.Compare(windows.List(server, list.List, beforeFrom, beforeTo), windows.List(server, list.List, afterFrom, afterTo)).Take(RowsPerList).ToList()))
            .Where(list => list.Item2.Count > 0)
            .ToList();

        return new Compared(before, after, parts, lists);
    }

    private void Show(Compared compared)
    {
        WindowSummary before = compared.Before;
        WindowSummary after = compared.After;

        if (!before.HasData || !after.HasData)
        {
            _message.Text = "There's no history for " + (before.HasData ? "the second" : "the first") + " stretch.";
            return;
        }

        _message.Text = null;

        _result.Children.Add(Look.Tiles(
            Pair("Sim speed", before.SimSpeed, after.SimSpeed, "0.00", string.Empty),
            Pair("Frame average", before.AverageFrameMs, after.AverageFrameMs, "0.0", " ms"),
            Pair("Frame worst", before.WorstFrameMs, after.WorstFrameMs, "0", " ms"),
            Pair("Available", before.AvailableMs, after.AvailableMs, "0.0", " ms")));

        _result.Children.Add(Look.Hint("A list holds each background total's top 20, so a dash means a row wasn't in that stretch's top, not that it cost nothing."));

        var columns = new FlowGrid(380d, 14d);
        columns.Children.Add(Look.Section("Frame", ComparedTable.Build(compared.Parts)));

        foreach ((string title, List<ComparedRow> rows) in compared.Lists)
            columns.Children.Add(Look.Section(title, ComparedTable.Build(rows)));

        _result.Children.Add(columns);
    }

    private static Tile Pair(string label, double before, double after, string format, string unit) =>
        new(label) { Value = Look.Num(before, format) + " to " + Look.Num(after, format) + unit };

    private static Dictionary<string, double> Parts(WindowSummary window)
    {
        var parts = new Dictionary<string, double>();

        for (int part = 0; part < window.Parts.Count; part++)
            parts[FrameParts.All[part].Name] = window.Parts[part];

        return parts;
    }

    private sealed record Compared(WindowSummary Before, WindowSummary After, List<ComparedRow> Parts, List<(string Title, List<ComparedRow> Rows)> Lists);
}
