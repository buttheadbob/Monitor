using System.Windows;
using System.Windows.Controls;
using SenX.Monitor.Api;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>Looks inside a plugin, a mod or the game by timing what a method calls.</summary>
public sealed class InsideTab : MonitorTab
{
    public const string Title = "Inside";

    private static readonly TimeSpan s_pollEvery = TimeSpan.FromSeconds(1);

    private readonly List<(string Id, string Name)> _trail = [];
    private readonly TextBox _search = Look.Input();
    private readonly NumberBox _seconds = new(10d);
    private readonly Notice _message = new();
    private readonly ReportGrid _roots;
    private readonly WrapPanel _trailButtons = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _timing = Look.Hint();
    private readonly StackPanel _result = new();
    private int _rootCount;
    private bool _hasResult;
    private bool _searched;
    private bool _running;

    public InsideTab(MonitorView page) : base(page)
    {
        _roots = new ReportGrid()
            .Text("Method", nameof(DrillRoot.Name))
            .Text("Whose", nameof(DrillRoot.Owner))
            .Button<DrillRoot>("Look inside", root => Drill(root.Id, root.Name));

        _roots.Visibility = Visibility.Collapsed;
        _timing.Visibility = Visibility.Collapsed;

        Add(
            Look.Hint("Looks inside a plugin, a mod or the game by timing what a method calls, with no help from whoever wrote it. This rebuilds the method while the server runs, so it takes a moment on the game thread, and it comes out by itself. It stops early if the timing costs too much."),
            Look.Row(
                Look.Field("Find", _search),
                Look.Action("Search", (_, _) => Search()),
                Look.Field("Time for (seconds)", _seconds)),
            _message,
            _roots,
            _trailButtons,
            _timing,
            _result);
    }

    protected override int RefreshSeconds => 0;

    public override void Shown()
    {
        if (!_searched)
            Search();
    }

    /// <summary>Starts from what another tab sent here.</summary>
    public void Find(string text)
    {
        _search.Text = text;
        Search();
    }

    private void Search()
    {
        // The trail belongs to what's being timed, and it needs to stay as it is until that's done.
        if (_running)
            return;

        Guarded(
            async () =>
            {
                _searched = true;
                _hasResult = false;
                _result.Children.Clear();
                _trail.Clear();
                ShowTrail();

                List<DrillRoot> roots = (await Services.Servers.DrillRootsAsync(Page.Server.Id, new DrillRootsRequest { Text = _search.Text }, Page.Closing)).Roots;

                _message.Text = roots.Count == 0 ? "Nothing matches that." : null;
                _rootCount = roots.Count;
                _roots.Show(roots);
                ShowRoots();
            },
            message => _message.Text = message);
    }

    private void ShowRoots() => _roots.Visibility = Look.VisibleIf(_rootCount > 0 && !_hasResult && !_running);

    private void BackTo(string id, string name)
    {
        if (_running)
            return;

        int index = _trail.FindIndex(step => step.Id == id);

        if (index >= 0)
            _trail.RemoveRange(index, _trail.Count - index);

        Drill(id, name);
    }

    private void Drill(string id, string name)
    {
        if (_running)
            return;

        _running = true;
        _hasResult = false;
        _message.Text = null;
        _result.Children.Clear();
        ShowRoots();
        _trail.Add((id, name));
        ShowTrail();

        _timing.Text = "Timing what " + name + " calls.";
        _timing.Visibility = Visibility.Visible;

        Guarded(
            async () =>
            {
                try
                {
                    await RunDrill(id);
                }
                finally
                {
                    _running = false;
                    _timing.Visibility = Visibility.Collapsed;
                    ShowRoots();
                    ShowTrail();
                }
            },
            message => _message.Text = message);
    }

    private async Task RunDrill(string id)
    {
        var request = new DrillStartRequest { Id = id, Seconds = Math.Clamp(_seconds.Whole, 1, 120) };
        DrillStartResponse started = await Services.Servers.StartDrillAsync(Page.Server.Id, request, Page.Closing);

        if (!started.Started)
        {
            _message.Text = started.Message ?? "It didn't start.";
            _trail.RemoveAt(_trail.Count - 1);
            ShowTrail();
            return;
        }

        while (true)
        {
            await Task.Delay(s_pollEvery, Page.Closing);

            DrillResultResponse result = await Services.Servers.DrillResultAsync(Page.Server.Id, new DrillResultRequest(), Page.Closing);

            if (result.State == RunState.Running)
                continue;

            ShowResult(result);
            return;
        }
    }

    private void ShowTrail()
    {
        _trailButtons.Children.Clear();

        foreach ((string id, string name) in _trail)
        {
            Button step = Look.Action(name, (_, _) => BackTo(id, name));

            // As plain text, a button takes an underscore in a method's name as a keyboard shortcut.
            step.Content = new TextBlock { Text = name };
            step.Margin = new Thickness(0, 0, 4, 4);
            step.IsEnabled = !_running;
            _trailButtons.Children.Add(step);
        }
    }

    private void ShowResult(DrillResultResponse result)
    {
        _hasResult = true;

        _result.Children.Add(Look.Tiles(
            new Tile("Calls") { Value = Look.Num(result.CallsPerFrame, "0.##") + "/f" },
            new Tile("Main") { Value = Look.Num(result.MainMs, "0.00") + " ms/f" },
            new Tile("Workers") { Value = Look.Num(result.WorkerMs, "0.00") + " ms/f" },
            new Tile("Its own work", "Main thread time not in anything it called.") { Value = Look.Num(result.SelfMs, "0.00") + " ms/f" },
            new Tile("Peak") { Value = Look.Num(result.PeakMs, "0.00") + " ms" },
            new Tile("Timing added") { Value = Look.Num(result.TimingMsPerFrame, "0.00") + " ms/f" }));

        if (result.CallsPerFrame == 0d)
            _result.Children.Add(new Notice { Text = "It never ran in that time, or the compiler built it into whatever calls it. Try looking inside the caller." });

        if (result.EndedEarly)
            _result.Children.Add(new Notice { Text = "It stopped early, because timing this was costing the server too much." });
        else if (result.Inflated)
            _result.Children.Add(new Notice { Text = "Timing added enough here that these figures read high." });

        if (result.SkippedSites > 0)
            _result.Children.Add(Look.Hint(result.SkippedSites + " calls were left out, because this makes too many different ones to track."));

        ReportGrid table = new ReportGrid()
            .Text("Calls", nameof(DrillLine.Name))
            .Text("Whose", nameof(DrillLine.Owner))
            .Number("Calls/f", nameof(DrillLine.CallsPerFrame), "0.##")
            .Number("Main ms/f", nameof(DrillLine.MainMs))
            .Number("Workers ms/f", nameof(DrillLine.WorkerMs))
            .Number("Peak ms", nameof(DrillLine.PeakMs));

        ThreadColumn.Add(table)
            .Button<DrillLine>("Look inside", row => Drill(row.Id, row.Name), nameof(DrillLine.CanOpen));

        table.Show(result.Rows.Select(row => new DrillLine(row.Id, row.Name, row.Owner, row.CallsPerFrame, row.MainMs, row.WorkerMs, row.PeakMs, Look.VisibleIf(row.Id.Length > 0))).ToList());

        _result.Children.Add(table);
        _result.Children.Add(Look.Hint("Time spent on other threads through tasks or timers shows as the call that started them, not the work itself."));
    }

    public sealed record DrillLine(string Id, string Name, string Owner, double CallsPerFrame, double MainMs, double WorkerMs, double PeakMs, Visibility CanOpen)
    {
        public string Thread => ThreadTags.Name(MonitorThreads.Of(MainMs, WorkerMs));

        public double MainPercent => ThreadTags.MainPercent(MainMs, WorkerMs);
    }
}
