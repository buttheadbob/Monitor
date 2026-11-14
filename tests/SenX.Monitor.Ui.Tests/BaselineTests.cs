using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class BaselineTests : IDisposable
{
    private const string Server = "server-1";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 30, 0, DateTimeKind.Utc);
    private readonly HistoryStore _store;
    private readonly MonitorBaselines _baselines;

    public BaselineTests()
    {
        var settings = new MonitorSettings(_directory);
        _store = new HistoryStore(_directory, settings, NullLogger<HistoryStore>.Instance);
        _baselines = new MonitorBaselines(_store, settings, NullLogger<MonitorBaselines>.Instance);
    }

    public void Dispose()
    {
        for (int attempt = 1; Directory.Exists(_directory); attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException) when (attempt < 100)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static HistoryRollup Row(DateTime started, DateTime time, uint physicsMs)
    {
        var scopes = new uint[43];
        scopes[HistoryScopes.Physics] = physicsMs * 10000U * 60U;
        scopes[HistoryScopes.Frame] = scopes[HistoryScopes.Physics];
        scopes[HistoryScopes.Components] = scopes[HistoryScopes.Physics];

        return new HistoryRollup { UnitsSinceStart = (time - started).Ticks, Frames = 60U, SimulatedFrames = 60U, WindowUnits = 10_000_000U, ScopeUnits = scopes };
    }

    // One row a minute across this hour of the day, on past days. Writing a row into a new minute is
    // what stores the one before, so each minute gets a second row to push it out.
    private void PastDays(int days, Func<int, uint> physicsMs)
    {
        DateTime started = _now.AddDays(-days - 1);
        var batch = new HistoryBatch { StartedUtcTicks = started.Ticks };

        for (int day = days; day >= 1; day--)
        {
            DateTime hour = _now.AddDays(-day).AddMinutes(-30);

            for (int minute = 0; minute < 60; minute++)
                batch.Rollups.Add(Row(started, hour.AddMinutes(minute), physicsMs(minute)));

            batch.Rollups.Add(Row(started, hour.AddMinutes(60), 1U));
        }

        _store.Add(Server, batch, (_, key) => key.ToString());
    }

    private void Recently(uint physicsMs)
    {
        DateTime started = _now.AddMinutes(-10);
        var batch = new HistoryBatch { StartedUtcTicks = started.Ticks };

        for (int second = 0; second < 60; second++)
            batch.Rollups.Add(Row(started, _now.AddSeconds(second - 60), physicsMs));

        _store.Add(Server, batch, (_, key) => key.ToString());
    }

    private BaselineRow Physics() => _baselines.Report(Server, _now).Rows.Single(row => row.Name == "Physics");

    [Fact]
    public void With_no_history_its_still_learning()
    {
        Assert.True(_baselines.Report(Server, _now).Learning);
    }

    [Fact]
    public void One_day_isnt_enough_to_call_normal()
    {
        PastDays(1, _ => 5U);
        Recently(5U);

        _baselines.Rebuild(Server, _now);

        Assert.True(_baselines.Report(Server, _now).Learning);
    }

    [Fact]
    public void Two_days_are()
    {
        PastDays(2, _ => 5U);
        Recently(5U);

        _baselines.Rebuild(Server, _now);

        Assert.False(_baselines.Report(Server, _now).Learning);
        Assert.Equal(5d, Physics().NormalMs, 3);
    }

    [Fact]
    public void Past_lag_doesnt_drag_normal_up()
    {
        PastDays(2, minute => minute < 10 ? 40U : 5U);
        Recently(5U);

        _baselines.Rebuild(Server, _now);

        Assert.Equal(5d, Physics().NormalMs, 3);
    }

    [Fact]
    public void A_part_well_over_normal_is_unusual()
    {
        PastDays(2, _ => 5U);
        Recently(12U);

        _baselines.Rebuild(Server, _now);

        Assert.True(Physics().Unusual);
        Assert.Contains(_baselines.UnusualNow(Server, _now), line => line.StartsWith("Physics", StringComparison.Ordinal));
    }

    [Fact]
    public void Normal_load_isnt()
    {
        PastDays(2, _ => 5U);
        Recently(5U);

        _baselines.Rebuild(Server, _now);

        Assert.False(Physics().Unusual);
    }

    [Theory]
    [InlineData(10d, 0.1d, 0.1d, true)]
    [InlineData(0.3d, 0.1d, 0.1d, false)]
    [InlineData(6d, 5d, 5.5d, false)]
    public void Unusual_needs_a_real_change(double now, double normal, double high, bool unusual)
    {
        Assert.Equal(unusual, new BaselineRow("Part", now, normal, high).Unusual);
    }

    [Fact]
    public void Idle_isnt_something_to_worry_about()
    {
        PastDays(2, _ => 5U);
        Recently(1U);

        _baselines.Rebuild(Server, _now);

        Assert.DoesNotContain(_baselines.Report(Server, _now).Rows, row => row.Name == FrameParts.IdleName);
    }

    [Fact]
    public void An_hour_it_hasnt_seen_is_still_learning()
    {
        PastDays(2, _ => 5U);

        _baselines.Rebuild(Server, _now);

        Assert.True(_baselines.Report(Server, _now.AddHours(6)).Learning);
    }
}

public sealed class HistoryWindowsTests : IDisposable
{
    private const string Server = "server-1";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _started = DateTime.UtcNow.AddMinutes(-20);
    private readonly HistoryStore _store;
    private readonly HistoryWindows _windows;

    public HistoryWindowsTests()
    {
        _store = new HistoryStore(_directory, new MonitorSettings(_directory), NullLogger<HistoryStore>.Instance);
        _windows = new HistoryWindows(_store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private void Add(int minute, uint frameMs, uint simulatedFrames = 60U)
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };

        for (int second = 0; second < 60; second++)
        {
            var scopes = new uint[43];
            scopes[HistoryScopes.Frame] = frameMs * 10000U * 60U;

            batch.Rollups.Add(new HistoryRollup
            {
                UnitsSinceStart = TimeSpan.FromMinutes(minute).Ticks + TimeSpan.FromSeconds(second).Ticks,
                Frames = 60U,
                SimulatedFrames = simulatedFrames,
                WindowUnits = 10_000_000U,
                FrameMax = (frameMs + 5U) * 10000U,
                ScopeUnits = scopes,
            });
        }

        _store.Add(Server, batch, (_, key) => key.ToString());
    }

    [Fact]
    public void A_stretch_is_averaged_and_its_worst_frame_kept()
    {
        Add(0, 8U);
        Add(1, 12U);

        WindowSummary window = _windows.Summarize(Server, _started, _started.AddMinutes(2));

        Assert.True(window.HasData);
        Assert.Equal(10d, window.AverageFrameMs, 3);
        Assert.Equal(17d, window.WorstFrameMs, 3);
        Assert.Equal(1d, window.SimSpeed, 3);
    }

    [Fact]
    public void A_stretch_with_nothing_in_it_says_so()
    {
        Assert.False(_windows.Summarize(Server, _started, _started.AddMinutes(1)).HasData);
    }

    [Fact]
    public void Parts_match_the_frame_parts()
    {
        Add(0, 8U);

        Assert.Equal(FrameParts.All.Length, _windows.Summarize(Server, _started, _started.AddMinutes(1)).Parts.Count);
    }

    [Fact]
    public void Two_stretches_line_up_by_name_biggest_change_first()
    {
        List<ComparedRow> rows = HistoryWindows.Compare(
            [new WindowRow("A", 0L, 1d, 0d, 0d, 0d, 1d), new WindowRow("B", 0L, 2d, 0d, 0d, 0d, 1d), new WindowRow("Gone", 0L, 5d, 0d, 0d, 0d, 1d)],
            [new WindowRow("A", 0L, 1.5d, 0d, 0d, 0d, 1d), new WindowRow("B", 0L, 5d, 0d, 0d, 0d, 1d)]);

        Assert.Equal(["B", "A", "Gone"], rows.Select(row => row.Name));
        Assert.Equal(3d, rows[0].Change!.Value, 6);
        Assert.Null(rows[2].Change);
    }

    [Fact]
    public void Old_stretches_read_from_minutes_however_short()
    {
        var old = new HistoryBatch { StartedUtcTicks = DateTime.UtcNow.AddHours(-10).Ticks };

        for (int minute = 0; minute < 3; minute++)
        {
            old.Rollups.Add(new HistoryRollup
            {
                UnitsSinceStart = TimeSpan.FromMinutes(minute).Ticks,
                Frames = 60U,
                SimulatedFrames = 60U,
                WindowUnits = 10_000_000U,
                ScopeUnits = new uint[43],
            });
        }

        _store.Add(Server, old, (_, key) => key.ToString());

        DateTime started = new(old.StartedUtcTicks, DateTimeKind.Utc);

        Assert.NotEmpty(_store.Points(Server, started.AddMinutes(-1), started.AddMinutes(5), 100));
    }

    [Fact]
    public void A_group_keeps_its_id_across_a_Quasar_restart()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Grids, Key = 77L, MainUnits = 10000U });
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Session, Key = 88L, MainUnits = 10000U });
        _store.Add(Server, batch, (_, key) => key.ToString());

        var reopened = new HistoryStore(_directory, new MonitorSettings(_directory), NullLogger<HistoryStore>.Instance);

        Assert.Equal(77L, Assert.Single(reopened.Groups(Server, HistoryDimension.Grids, _started.AddMinutes(-1), _started.AddMinutes(1))).Key);
        Assert.Equal(0L, Assert.Single(reopened.Groups(Server, HistoryDimension.Session, _started.AddMinutes(-1), _started.AddMinutes(1))).Key);
    }

    [Fact]
    public void An_entity_row_finds_its_grid_in_the_latest_total()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        batch.Groups.Add(new HistoryGroup { UnitsSinceStart = 1L, Dimension = HistoryDimension.Grids, Key = 77L, MainUnits = 30000U });
        _store.Add(Server, batch, (_, key) => key.ToString());

        Assert.Equal(3f, _store.LatestGridCost(Server, 77L)!.MainMs, 3);
        Assert.Null(_store.LatestGridCost(Server, 5L));
    }

    [Fact]
    public void A_newer_total_replaces_the_last()
    {
        var first = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        first.Groups.Add(new HistoryGroup { UnitsSinceStart = 1L, Dimension = HistoryDimension.Grids, Key = 77L });
        _store.Add(Server, first, (_, key) => key.ToString());

        var second = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        second.Groups.Add(new HistoryGroup { UnitsSinceStart = 2L, Dimension = HistoryDimension.Grids, Key = 5L });
        _store.Add(Server, second, (_, key) => key.ToString());

        Assert.Null(_store.LatestGridCost(Server, 77L));
    }
}
