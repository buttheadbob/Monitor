using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Services;
using SenX.Monitor.Torch.Ui.Tabs;

namespace SenX.Monitor.Torch.Ui;

/// <summary>
/// The whole page: controls along the top, then the tabs. Only the tab on screen does anything, and
/// when the plugin isn't showing at all the clock stops, so a closed page costs the game nothing.
/// </summary>
public sealed class MonitorView : UserControl
{
    private const int TilesRefreshSeconds = 2;

    // A page failing on every tick shouldn't fill Torch's log.
    private const int MostFaultsLogged = 5;

    private static readonly (string Name, int Minutes)[] s_ranges =
    [
        ("5 minutes", 5),
        ("1 hour", 60),
        ("6 hours", 360),
        ("24 hours", 1440),
        ("3 days", 4320),
        ("7 days", 10080),
    ];

    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _clock;
    private readonly TabControl _tabs = new();
    private readonly ComboBox _range = new() { Width = 110d };
    private readonly ComboBox _retention = new() { Width = 80d, ToolTip = "History older than this is deleted." };
    private readonly CheckBox _live = Look.Check("Live", on: true, tip: "Turn off to hold the charts still while you study them.");
    private readonly Notice _worker = new();
    private readonly Notice _lag = new();
    private readonly Notice _problem = new();
    private readonly Notice _fault = new();
    private readonly List<int> _shownRanges = [];
    private readonly TextBox _boardLink = new() { IsReadOnly = true, Width = 420d, ToolTip = "The full charts' address. It works on this computer, until the server restarts." };

    private readonly Func<string?>? _workerProblem;

    private InsideTab? _inside;
    private bool _loading = true;
    private int _second;
    private int _faults;

    public MonitorView(MonitorServices services, Func<string?>? problem = null)
    {
        Services = services;
        Server = services.Servers.Connected()[0];
        _workerProblem = problem;

        SetResourceReference(ForegroundProperty, Look.TextKey);
        SetResourceReference(BackgroundProperty, Look.BackgroundKey);

        Dispatcher.UnhandledException += OnUnhandled;

        Content = Build();

        FillRanges();

        for (int days = 1; days <= MonitorSettings.MaxRetentionDays; days++)
            _retention.Items.Add(days == 1 ? "1 day" : days.ToString(CultureInfo.InvariantCulture) + " days");

        _retention.SelectedIndex = services.Settings.RetentionDays - 1;
        _range.SelectionChanged += (_, _) => ShowCurrent();
        _retention.SelectionChanged += (_, _) => RetentionChanged();
        _live.Click += (_, _) => ShowCurrent();
        _tabs.SelectionChanged += OnTabChanged;

        _loading = false;
        _tabs.SelectedIndex = 0;

        _clock = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => OnSecond();

        IsVisibleChanged += (_, change) =>
        {
            if ((bool)change.NewValue)
            {
                _clock.Start();
                ShowCurrent();
            }
            else
            {
                _clock.Stop();
            }
        };
    }

    public MonitorServices Services { get; }

    /// <summary>The server on show. Torch only ever has the one.</summary>
    public MonitorServer Server { get; }

    public int RangeMinutes => _range.SelectedIndex < 0 ? s_ranges[0].Minutes : _shownRanges[_range.SelectedIndex];

    public bool Live => _live.IsChecked == true;

    /// <summary>Ends with the page, so a wait can stop.</summary>
    public CancellationToken Closing => _closing.Token;

    private MonitorTab? Current => (_tabs.SelectedItem as TabItem)?.Content as MonitorTab;

    /// <summary>Stops the clock for good, as the plugin closes.</summary>
    public void Stop()
    {
        _clock.Stop();
        _closing.Cancel();
        Dispatcher.UnhandledException -= OnUnhandled;
    }

    // Torch has no handler for its UI thread, so anything that escapes these pages would end the
    // server. This is the one place all of it passes through, async and layout included, and it
    // only takes what came from Monitor's own code.
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs failure)
    {
        if (failure.Exception.StackTrace?.Contains("SenX.Monitor") != true)
            return;

        failure.Handled = true;
        _fault.Text = "Part of this page failed. Torch's log has the details, and the server isn't affected.";

        if (++_faults <= MostFaultsLogged)
            MonitorLogSink.Write?.Invoke("A Monitor page failed.", failure.Exception);
    }

    /// <summary>Goes to the Inside tab, looking for this.</summary>
    public void LookInside(string part)
    {
        foreach (TabItem tab in _tabs.Items)
        {
            if (!Equals(tab.Header, InsideTab.Title))
                continue;

            if (tab is { Content: null, Tag: Func<MonitorTab> build })
                tab.Content = build();

            // First, so the tab doesn't start an empty search of its own as it comes on screen.
            _inside?.Find(part);
            _tabs.SelectedItem = tab;
            return;
        }
    }

    private Grid Build()
    {
        AddTab("Overview", () => new OverviewTab(this));
        AddTab("Tree", () => new TreeTab(this));
        AddTab("Costs", () => new CostsTab(this));
        AddTab("Mods", () => new ModsTab(this));
        AddTab("Network", () => new NetworkTab(this));
        AddTab("World", () => new WorldTab(this));
        AddTab("Incidents", () => new IncidentsTab(this));
        AddTab("Compare", () => new CompareTab(this));
        AddTab("Patches", () => new PatchesTab(this));
        AddTab(InsideTab.Title, () => _inside = new InsideTab(this));
        AddTab("Runs", () => new RunsTab(this));

        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1d, GridUnitType.Star) });

        _boardLink.Visibility = Visibility.Collapsed;

        WrapPanel header = Look.Row(
            Look.Field("Range", _range),
            _live,
            Look.Field("Keep history for", _retention),
            Look.Action("Open full charts", (_, _) => OpenBoard()),
            _boardLink);
        StackPanel top = Look.Stack(_worker, _fault, _problem, _lag);

        Grid.SetRow(top, 1);
        Grid.SetRow(_tabs, 2);
        root.Children.Add(header);
        root.Children.Add(top);
        root.Children.Add(_tabs);
        return root;
    }

    // The charts open in the admin's browser, from a small web server that only starts now. The address
    // stays on show, because a server may have no browser to open, and it can be copied into one that's elsewhere on this computer.
    private void OpenBoard()
    {
        try
        {
            string address = Services.BoardAddress();

            _boardLink.Text = address;
            _boardLink.Visibility = Visibility.Visible;

            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception)
        {
            _fault.Text = "Couldn't open a browser here. Copy the address next to the button into one on this computer.";
            MonitorLogSink.Write?.Invoke("Monitor couldn't open the full charts.", exception);
        }
    }

    // Building a tab waits until it's first opened, so tabs nobody looks at take no memory.
    private void AddTab(string name, Func<MonitorTab> build) => _tabs.Items.Add(new TabItem { Header = name, Tag = build });

    private void FillRanges()
    {
        int chosen = RangeMinutesOrDefault();
        int limit = Services.Settings.RetentionDays * 1440;

        _loading = true;
        _range.Items.Clear();
        _shownRanges.Clear();

        foreach ((string name, int minutes) in s_ranges)
        {
            if (minutes > limit)
                continue;

            _range.Items.Add(name);
            _shownRanges.Add(minutes);
        }

        // The largest range that's still offered, because the page can't show one that isn't.
        int index = _shownRanges.FindLastIndex(minutes => minutes <= chosen);
        _range.SelectedIndex = Math.Max(0, index);
        _loading = false;
    }

    private int RangeMinutesOrDefault() => _range.SelectedIndex >= 0 && _range.SelectedIndex < _shownRanges.Count ? _shownRanges[_range.SelectedIndex] : s_ranges[0].Minutes;

    private void RetentionChanged()
    {
        if (_loading)
            return;

        Services.Settings.RetentionDays = _retention.SelectedIndex + 1;
        Services.Store.PruneSoon();
        FillRanges();
        ShowCurrent();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs change)
    {
        // A control inside a tab raises this too, and it bubbles up here.
        if (!ReferenceEquals(change.OriginalSource, _tabs) || _loading)
            return;

        if (_tabs.SelectedItem is TabItem { Content: null, Tag: Func<MonitorTab> build } tab)
            tab.Content = build();

        ShowCurrent();
    }

    private void ShowCurrent()
    {
        if (_loading || !IsVisible)
            return;

        Services.Collector.Watch(Server.HistoryId);
        UpdateNotices();
        Current?.Shown();
    }

    private void OnSecond()
    {
        // Torch often sits minimized for days, and nothing drawn here would be seen.
        if (Window.GetWindow(this)?.WindowState == WindowState.Minimized)
            return;

        _second++;

        // A page being open is what says someone's watching, so this server stays fresh.
        Services.Collector.Watch(Server.HistoryId);

        MonitorTab? tab = Current;

        if (tab == null)
            return;

        if (Live)
            tab.Second(_second);

        if (_second % TilesRefreshSeconds != 0)
            return;

        UpdateNotices();

        if (Live && tab.Refreshes(_second))
            tab.Refresh();
    }

    private void UpdateNotices()
    {
        _worker.Text = _workerProblem?.Invoke();

        _problem.Text = Services.Collector.Problem(Server.HistoryId) is { } problem
            ? "This server isn't answering: " + problem
            : null;

        _lag.Text = Current is not IncidentsTab && Services.Incidents.OpenSince(Server.HistoryId) is { } since
            ? "Monitor noticed lag at " + since.ToLocalTime().ToString("T", CultureInfo.CurrentCulture) + " and it hasn't recovered yet. The Incidents tab has what's known so far."
            : null;
    }
}
