using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class ReportTextTests
{
    private static RunResultResponse Run() => new()
    {
        Seconds = 10d,
        SimSpeed = 0.75d,
        AverageFrameMs = 20d,
        WorstFrameMs = 90d,
        RequestedBy = "Sam",
        Rows = [new RunRow { List = RunLists.Grids, Name = "Big Ship", MainMsPerFrame = 3d, WorkerMsPerFrame = 1d, PeakMs = 9d }],
    };

    [Fact]
    public void A_run_reads_as_plain_text()
    {
        string text = ReportText.ForRun(Run());

        Assert.Contains("sim 0.75", text);
        Assert.Contains("Started by Sam", text);
        Assert.Contains("Big Ship: main 3.00", text);
    }

    [Fact]
    public void Rpcs_sent_read_as_bytes_and_not_as_time()
    {
        var run = new RunResultResponse
        {
            State = RunState.Finished,
            Rows = [new RunRow { List = RunLists.RpcsSent, Name = "MyCubeGrid.OnSomething", BytesPerSecond = 2048d, CallsPerFrame = 0.5d }],
        };

        string text = ReportText.ForRun(run);

        Assert.Contains("RPCs sent", text);
        Assert.Contains("MyCubeGrid.OnSomething: 2.0 KB/s, 0.5 events/f", text);
        Assert.DoesNotContain("main", text);
    }

    [Fact]
    public void Lists_with_nothing_in_them_are_left_out()
    {
        Assert.DoesNotContain("Scripts", ReportText.ForRun(Run()));
    }

    [Fact]
    public void An_incident_says_what_happened_and_how_it_ended()
    {
        var opened = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        string text = ReportText.ForIncident(new StoredIncident
        {
            Opened = opened,
            Closed = opened.AddSeconds(90),
            ClosedBy = "Recovered",
            LowestSim = 0.5d,
            WorstFrameMs = 250d,
            Unusual = ["Physics: 12.0 ms/f, normally 3.0"],
            Evidence = [new IncidentEvidence { Report = new IncidentResultResponse { State = RunState.Finished, Reason = "SimSpeed", Value = 0.5d, Run = Run() } }],
        });

        Assert.Contains("Sim speed fell to 0.50", text);
        Assert.Contains("Recovered after 90 s", text);
        Assert.Contains("normally 3.0", text);
        Assert.Contains("Big Ship", text);
    }

    [Fact]
    public void An_open_incident_says_so()
    {
        Assert.Contains("Still going", ReportText.ForIncident(new StoredIncident { Opened = DateTime.UtcNow }));
    }

    [Fact]
    public void A_spike_shows_its_slowest_calls()
    {
        string text = ReportText.ForIncident(new StoredIncident
        {
            Opened = DateTime.UtcNow,
            Evidence = [new IncidentEvidence { Report = new IncidentResultResponse { SlowCalls = [new SlowCallRow { Name = "Drill", Where = "Entity before", Ms = 40d }] } }],
        });

        Assert.Contains("Drill (Entity before): 40.0 ms", text);
    }
}

public sealed class NewCostTests : IDisposable
{
    private const string Server = "server-1";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = DateTime.UtcNow;
    private readonly HistoryStore _store;
    private readonly MonitorBaselines _baselines;

    public NewCostTests()
    {
        var settings = new MonitorSettings(_directory);
        _store = new HistoryStore(_directory, settings, NullLogger<HistoryStore>.Instance);
        _baselines = new MonitorBaselines(_store, settings, NullLogger<MonitorBaselines>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private void Past(params (long Key, uint Ms)[] grids)
    {
        foreach (int daysAgo in new[] { 2, 1 })
        {
            var batch = new HistoryBatch { StartedUtcTicks = _now.AddDays(-daysAgo).Ticks };

            foreach ((long key, uint ms) in grids)
                batch.Groups.Add(new HistoryGroup { UnitsSinceStart = 1L, Dimension = HistoryDimension.Grids, Key = key, MainUnits = ms * 10000U });

            _store.Add(Server, batch, (_, key) => "Grid " + key);
        }
    }

    private void Latest(params (long Key, uint Ms)[] grids)
    {
        var batch = new HistoryBatch { StartedUtcTicks = _now.AddMinutes(-1).Ticks };

        foreach ((long key, uint ms) in grids)
            batch.Groups.Add(new HistoryGroup { UnitsSinceStart = 1L, Dimension = HistoryDimension.Grids, Key = key, MainUnits = ms * 10000U });

        _store.Add(Server, batch, (_, key) => "Grid " + key);
    }

    [Fact]
    public void Something_never_seen_before_is_new()
    {
        Past((1L, 2U));
        _baselines.Rebuild(Server, _now);
        Latest((2L, 5U));

        NewCost cost = Assert.Single(_baselines.FindNewCosts(Server, _now));

        Assert.Equal("Grid 2", cost.Name);
        Assert.Null(cost.NormalMs);
    }

    [Fact]
    public void Something_that_doubled_has_jumped()
    {
        Past((1L, 2U));
        _baselines.Rebuild(Server, _now);
        Latest((1L, 5U));

        Assert.Equal(2d, Assert.Single(_baselines.FindNewCosts(Server, _now)).NormalMs!.Value, 3);
    }

    [Fact]
    public void Steady_costs_arent_flagged()
    {
        Past((1L, 2U));
        _baselines.Rebuild(Server, _now);
        Latest((1L, 3U));

        Assert.Empty(_baselines.FindNewCosts(Server, _now));
    }

    [Fact]
    public void Tiny_costs_arent_flagged_however_much_they_grew()
    {
        Past((1L, 0U));
        _baselines.Rebuild(Server, _now);

        var batch = new HistoryBatch { StartedUtcTicks = _now.AddMinutes(-1).Ticks };
        batch.Groups.Add(new HistoryGroup { UnitsSinceStart = 1L, Dimension = HistoryDimension.Grids, Key = 3L, MainUnits = 5000U });
        _store.Add(Server, batch, (_, key) => "Grid " + key);

        Assert.Empty(_baselines.FindNewCosts(Server, _now));
    }

    [Fact]
    public void Nothing_is_called_new_before_theres_a_history_to_compare()
    {
        Latest((2L, 5U));
        _baselines.Rebuild(Server, _now);

        Assert.Empty(_baselines.FindNewCosts(Server, _now));
    }
}

public sealed class CapacityTests : IDisposable
{
    private const string Server = "server-1";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _started = DateTime.UtcNow.AddHours(-2);
    private readonly HistoryStore _store;
    private readonly HistoryWindows _windows;

    public CapacityTests()
    {
        _store = new HistoryStore(_directory, new MonitorSettings(_directory), NullLogger<HistoryStore>.Instance);
        _windows = new HistoryWindows(_store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private void Moments(int count, Func<int, uint> players, Func<uint, double> frameMs)
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };

        for (int moment = 0; moment < count; moment++)
        {
            uint online = players(moment);
            var scopes = new uint[43];
            scopes[HistoryScopes.Frame] = (uint)(frameMs(online) * 10000d * 60d);
            long units = TimeSpan.FromSeconds(moment * 30).Ticks;

            batch.Rollups.Add(new HistoryRollup { UnitsSinceStart = units, Frames = 60U, SimulatedFrames = 60U, WindowUnits = 10_000_000U, ScopeUnits = scopes });
            batch.Census.Add(new HistoryCensus { UnitsSinceStart = units, OnlinePlayers = online });
        }

        _store.Add(Server, batch, (_, key) => key.ToString());
    }

    private CapacityReport Report() => _windows.Capacity(Server, _started.AddMinutes(-1), DateTime.UtcNow);

    [Fact]
    public void No_history_gives_no_rows()
    {
        Assert.Empty(Report().Rows);
    }

    [Fact]
    public void Frame_time_is_set_against_players()
    {
        Moments(100, moment => (uint)(moment % 40), online => 2d + (online * 0.25d));

        CapacityReport report = Report();

        Assert.True(report.Rows.Count > 1);
        Assert.True(report.Rows[^1].FrameMs > report.Rows[0].FrameMs);
    }

    [Fact]
    public void A_clean_line_says_where_the_budget_runs_out()
    {
        Moments(100, moment => (uint)(moment % 40), online => 2d + (online * 0.25d));

        Assert.InRange(Report().PlayersAtBudget!.Value, 55, 62);
    }

    [Fact]
    public void A_flat_server_says_nothing_about_a_limit()
    {
        Moments(100, moment => (uint)(moment % 40), _ => 5d);

        Assert.Null(Report().PlayersAtBudget);
    }

    [Fact]
    public void Too_little_history_says_nothing_about_a_limit()
    {
        Moments(10, moment => (uint)moment, online => 2d + online);

        Assert.Null(Report().PlayersAtBudget);
    }

    [Fact]
    public void One_level_of_players_says_nothing_about_a_limit()
    {
        Moments(100, _ => 20U, online => 2d + online);

        Assert.Null(Report().PlayersAtBudget);
    }

    [Fact]
    public void Copied_text_says_when_part_of_a_run_reads_low()
    {
        string text = ReportText.ForRun(new RunResultResponse { LostSites = "14 call sites can't be timed" });

        Assert.Contains("14 call sites can't be timed.", text, StringComparison.Ordinal);
    }
}
