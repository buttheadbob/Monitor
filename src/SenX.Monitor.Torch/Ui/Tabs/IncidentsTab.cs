using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>Lag Monitor noticed by itself, with the evidence it kept, and what counts as lag.</summary>
public sealed class IncidentsTab : MonitorTab
{
    private static readonly TimeSpan s_around = TimeSpan.FromMinutes(5);

    private readonly ChartBoard _board;
    private readonly Notice _nothing = new();
    private readonly ReportGrid _list;
    private readonly StackPanel _shownPanel = new();
    private readonly FlowGrid _chart = new();
    private readonly TextBlock _heading = Look.Heading();
    private readonly TextBlock _unusual = Look.Hint();
    private readonly Button _copy;
    private readonly StackPanel _evidence = new();
    private readonly Limits _limits;
    private readonly PasswordBox _webhook = new() { Width = 300d, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _webhookState = Look.Hint();
    private readonly TextBlock _webhookNote = Look.Hint();
    private StoredIncident? _shown;

    public IncidentsTab(MonitorView page) : base(page)
    {
        _board = NewBoard();
        _limits = new Limits(Services.Settings.Triggers);
        _copy = Look.Action("Copy as text", (_, _) => CopyShown());

        _list = new ReportGrid()
            .Text("When", nameof(IncidentLine.When), stretch: false)
            .Text("What", nameof(IncidentLine.What))
            .Text("Lasted", nameof(IncidentLine.Lasted), stretch: false)
            .Number("Lowest sim", nameof(IncidentLine.LowestSim))
            .Number("Worst frame ms", nameof(IncidentLine.WorstFrameMs), "0")
            .Button<IncidentLine>("View", row => Show(row.Id));

        _chart.Children.Add(_board.Chart("cause", "What was using the frame", wide: true));

        _shownPanel.Visibility = Visibility.Collapsed;
        _shownPanel.Children.Add(Look.Row(_heading, _copy));
        _shownPanel.Children.Add(_unusual);
        _shownPanel.Children.Add(_chart);
        _shownPanel.Children.Add(_evidence);

        Add(
            Look.Hint("Monitor watches for lag by itself, measures it, and keeps the evidence here, so a problem overnight can still be looked at in the morning."),
            Settings("What counts as lag", _limits.LagRow, Look.Action("Save", (_, _) => SaveLimits())),
            Settings("What players can run", _limits.PlayerRow, Look.Action("Save", (_, _) => SaveLimits())),
            DiscordSettings(),
            _nothing,
            _list,
            _shownPanel);
    }

    private Expander Settings(string title, WrapPanel row, Button save)
    {
        save.Margin = new Thickness(0, 0, 14, 6);
        row.Children.Add(save);
        return Folded(title, row);
    }

    private static Expander Folded(string title, UIElement content)
    {
        var expander = new Expander { Header = title, Content = content, Margin = new Thickness(0, 0, 0, 8) };
        expander.SetResourceReference(ForegroundProperty, Look.TextKey);
        return expander;
    }

    private Expander DiscordSettings()
    {
        WrapPanel row = Look.Row(
            Look.Field("Webhook URL", _webhook),
            Look.Action("Save", (_, _) => SaveWebhook()),
            Look.Action("Remove", (_, _) => RemoveWebhook()),
            Look.Action("Send a test", (_, _) => TestWebhook()));

        return Folded("Discord alerts", Look.Stack(
            Look.Hint("Posts to a Discord webhook when lag is noticed, when the evidence is in, and when it ends."),
            _webhookState,
            row,
            _webhookNote));
    }

    public override void Shown()
    {
        _webhookState.Text = Services.Settings.HasOwnWebhook(ServerId) ? "A webhook is saved." : "No webhook is saved, so nothing is posted.";
        Refresh();
    }

    public override void Refresh()
    {
        List<IncidentInfo> incidents = Services.Incidents.List(ServerId);

        _nothing.Text = incidents.Count == 0 ? "No lag noticed in the history kept." : null;
        _list.Visibility = Look.VisibleIf(incidents.Count > 0);
        _list.Show(incidents.Select(incident => new IncidentLine(
            incident.Id,
            incident.Opened.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            incident.Reason,
            Lasted(incident),
            incident.LowestSim,
            incident.WorstFrameMs)).ToList());

        // Evidence can arrive while it's on show.
        if (_shown != null && _shown.Evidence.Any(evidence => evidence.Report == null && evidence.Lost == null))
            ShowDetail(Services.Incidents.Load(ServerId, _shown.Id));
    }

    private void Show(string id)
    {
        StoredIncident? incident = Services.Incidents.Load(ServerId, id);

        // Worked out once, because a chart whose window kept moving would be rebuilt on every refresh.
        if (incident != null)
            _board.Show(new ChartRequest("overview", ServerId, 0, From: incident.Opened - s_around, To: (incident.Closed ?? DateTime.UtcNow) + s_around));

        ShowDetail(incident);
    }

    private void ShowDetail(StoredIncident? incident)
    {
        _shown = incident;
        _shownPanel.Visibility = Look.VisibleIf(incident != null);

        if (incident is not { } shown)
            return;

        _heading.Text = MonitorIncidents.ReasonOf(shown) + " at " + shown.Opened.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        _unusual.Text = shown.Unusual.Count > 0 ? "Unusual for this server when it began: " + string.Join("; ", shown.Unusual) + "." : string.Empty;
        _unusual.Visibility = Look.VisibleIf(shown.Unusual.Count > 0);

        _evidence.Children.Clear();

        foreach (IncidentEvidence evidence in shown.Evidence)
            AddEvidence(evidence);
    }

    private void AddEvidence(IncidentEvidence evidence)
    {
        if (evidence.Report is not { } report)
        {
            _evidence.Children.Add(evidence.Lost != null ? new Notice { Text = evidence.Lost } : Look.Hint("Still gathering the evidence."));
            return;
        }

        _evidence.Children.Add(Look.Heading("Noticed at " + evidence.Time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)));

        if (report.NoRun != null)
            _evidence.Children.Add(Look.Hint("No run for this one, because " + report.NoRun + "."));

        var columns = new FlowGrid(380d, 14d);

        if (report.Frames.Count > 0)
        {
            ReportGrid frames = new ReportGrid()
                .Text("Took most", nameof(FrameLine.Part))
                .Text("GC", nameof(FrameLine.Collection), stretch: false)
                .Number("ms", nameof(FrameLine.Ms), "0.0");

            frames.Show(report.Frames.Select(frame => new FrameLine(BiggestPart(frame), Collection(frame), frame.Ms)).ToList());
            columns.Children.Add(Look.Section("Worst frames just before", frames));
        }

        if (report.Run == null && report.SlowCalls.Count > 0)
            columns.Children.Add(Look.Section("Slowest single calls", RunReportView.SlowCalls(report.SlowCalls)));

        _evidence.Children.Add(columns);

        if (report.Run is { } run)
            _evidence.Children.Add(new RunReportView(run));
    }

    private void CopyShown()
    {
        if (_shown != null)
            Look.Copy(ReportText.ForIncident(_shown));
    }

    private void SaveLimits()
    {
        Services.Settings.Triggers = _limits.Read(Services.Settings.Triggers);
        _limits.Show(Services.Settings.Triggers);
    }

    private void SaveWebhook()
    {
        bool saved = Services.Settings.SetWebhook(ServerId, _webhook.Password);

        _webhookNote.Text = saved ? "Saved." : "That isn't a Discord webhook URL.";

        if (saved)
            _webhook.Clear();

        Shown();
    }

    private void RemoveWebhook()
    {
        Services.Settings.SetWebhook(ServerId, null);
        _webhookNote.Text = "Removed.";
        Shown();
    }

    private void TestWebhook() => Guarded(
        async () => _webhookNote.Text = await Services.Webhooks.SendTestAsync(ServerId, CancellationToken.None)
            ? "Sent. Check the channel."
            : "That didn't work. Check the webhook is saved and still exists in Discord.",
        message => _webhookNote.Text = message);

    private static string Lasted(IncidentInfo incident)
    {
        if (incident.Length is not { } length)
            return "Still going";

        string time = length.TotalMinutes >= 1d
            ? Look.Num(length.TotalMinutes, "0") + " min"
            : Look.Num(length.TotalSeconds, "0") + " s";

        return incident.ClosedBy == "Recovered" ? time : time + ", " + incident.ClosedBy.ToLowerInvariant();
    }

    private static string BiggestPart(IncidentFrame frame)
    {
        double[] parts = FrameParts.Split(new HistoryPoint { Scopes = frame.ScopeUnits.Select(units => units / 10000f).ToArray() });
        int biggest = 0;

        // Idle is last, and it's not something a frame spent time on.
        for (int part = 1; part < parts.Length - 1; part++)
        {
            if (parts[part] > parts[biggest])
                biggest = part;
        }

        return FrameParts.All[biggest].Name + " " + Look.Num(parts[biggest], "0.0") + " ms";
    }

    private static string Collection(IncidentFrame frame) => frame.Collection < 0 ? "-" : "Gen " + frame.Collection.ToString(CultureInfo.InvariantCulture);

    public sealed record IncidentLine(string Id, string When, string What, string Lasted, double LowestSim, double WorstFrameMs);

    public sealed record FrameLine(string Part, string Collection, double Ms);

    /// <summary>The settings as controls. Reading them gives a new copy, because the live one is shared.</summary>
    private sealed class Limits
    {
        private readonly CheckBox _enabled = Look.Check("Watch for lag");
        private readonly NumberBox _simBelow = new(0d);
        private readonly NumberBox _p95 = new(0d);
        private readonly NumberBox _seconds = new(0d);
        private readonly NumberBox _spike = new(0d);
        private readonly NumberBox _joinGrace = new(0d);
        private readonly NumberBox _runSeconds = new(0d);
        private readonly NumberBox _cooldown = new(0d);
        private readonly CheckBox _clusters = Look.Check("Time physics clusters", tip: "This changes how physics is scheduled, with nobody watching.");
        private readonly CheckBox _tellAdmins = Look.Check("Tell admins in the game");
        private readonly CheckBox _playerMine = Look.Check("!monitor mine", tip: "Reads the latest background total, so it costs the server nothing.");
        private readonly CheckBox _playerTest = Look.Check("!monitor test", tip: "A test times every call on the server for its length.");
        private readonly NumberBox _testSeconds = new(0d);
        private readonly NumberBox _testCooldown = new(0d);
        private readonly NumberBox _testLowestSim = new(0d);

        public Limits(TriggerSettings settings)
        {
            LagRow = Look.Row(
                _enabled,
                Look.Field("Sim speed under", _simBelow, "A second under this counts as slow."),
                Look.Field("Or frame p95 over (ms)", _p95, "Zero turns this off. Catches frames slowing down before sim speed falls."),
                Look.Field("For (seconds)", _seconds),
                Look.Field("Or one frame over (ms)", _spike, "Zero turns this off. Saves are left out, because they stop the game by design."),
                Look.Field("Not within (seconds of a spawn)", _joinGrace, "A character spawning makes the game load voxel files, which can hold a frame for seconds. Such a frame is recorded as a join hitch, with no alert or run. Zero turns this off."),
                Look.Field("Run for (seconds)", _runSeconds, "Timing every call adds cost during lag, so this is short. Zero gathers evidence without a run."),
                Look.Field("At most every (seconds)", _cooldown),
                _clusters,
                _tellAdmins);

            PlayerRow = Look.Row(
                _playerMine,
                _playerTest,
                Look.Field("For (seconds)", _testSeconds),
                Look.Field("At most every (seconds per player)", _testCooldown),
                Look.Field("Not under sim speed", _testLowestSim, "Refused below this, so players can't add to lag."));

            Show(settings);
        }

        public WrapPanel LagRow { get; }

        public WrapPanel PlayerRow { get; }

        public void Show(TriggerSettings settings)
        {
            _enabled.IsChecked = settings.Enabled;
            _simBelow.Number = settings.SimBelow;
            _p95.Number = settings.FrameP95AboveMs;
            _seconds.Number = settings.Seconds;
            _spike.Number = settings.SpikeMs;
            _joinGrace.Number = settings.JoinGraceSeconds;
            _runSeconds.Number = settings.RunSeconds;
            _cooldown.Number = settings.CooldownSeconds;
            _clusters.IsChecked = settings.TimeClusters;
            _tellAdmins.IsChecked = settings.TellAdmins;
            _playerMine.IsChecked = settings.PlayerMine;
            _playerTest.IsChecked = settings.PlayerTest;
            _testSeconds.Number = settings.PlayerTestSeconds;
            _testCooldown.Number = settings.PlayerTestCooldownSeconds;
            _testLowestSim.Number = settings.PlayerTestLowestSim;
        }

        public TriggerSettings Read(TriggerSettings current) => new()
        {
            Enabled = _enabled.IsChecked == true,
            SimBelow = _simBelow.Number,
            FrameP95AboveMs = _p95.Number,
            Seconds = _seconds.Whole,
            SpikeMs = _spike.Number,
            JoinGraceSeconds = _joinGrace.Whole,
            RunSeconds = _runSeconds.Whole,
            CooldownSeconds = _cooldown.Whole,
            TimeClusters = _clusters.IsChecked == true,
            TellAdmins = _tellAdmins.IsChecked == true,
            PlayerMine = _playerMine.IsChecked == true,
            PlayerTest = _playerTest.IsChecked == true,
            PlayerTestSeconds = _testSeconds.Whole,
            PlayerTestCooldownSeconds = _testCooldown.Whole,
            PlayerTestLowestSim = _testLowestSim.Number,
        };
    }
}
