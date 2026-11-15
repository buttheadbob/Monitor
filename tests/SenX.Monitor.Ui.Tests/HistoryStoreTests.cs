using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Components.Charts;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private const string Server = "server-1";

    // An hour ago, on the minute. Anything past the retention setting is deleted as soon as a row goes in.
    private static readonly DateTime s_started = new(DateTime.UtcNow.AddHours(-1).Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private HistoryStore NewStore() => new(_directory, new MonitorSettings(_directory), NullLogger<HistoryStore>.Instance);

    private static HistoryBatch Batch(int firstSecond, int seconds, uint scopeUnits = 10000U)
    {
        var batch = new HistoryBatch { StartedUtcTicks = s_started.Ticks, RollupFirst = firstSecond, RollupHead = firstSecond + seconds };

        for (int second = firstSecond; second < firstSecond + seconds; second++)
        {
            var scopes = new uint[43];
            scopes[HistoryScopes.Physics] = scopeUnits * 60U;

            batch.Rollups.Add(new HistoryRollup
            {
                Sequence = second,
                UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks,
                Frames = 60U,
                SimulatedFrames = 60U,
                WindowUnits = 10_000_000U,
                FrameP99 = 150_000U,
                ScopeUnits = scopes,
            });
        }

        return batch;
    }

    private static string Name(HistoryDimension kind, long key) => kind == HistoryDimension.Factions ? "F" + key : "Grid " + key;

    // Rows of background totals, each second one total. A head past the rows says more are coming.
    private static HistoryBatch Totals(long groupHead = 0L, params (int Second, HistoryDimension List, long Key)[] rows)
    {
        var batch = new HistoryBatch { StartedUtcTicks = s_started.Ticks, GroupHead = groupHead };

        foreach ((int second, HistoryDimension list, long key) in rows)
            batch.Groups.Add(new HistoryGroup { UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks, Dimension = list, Key = key, MainUnits = 10000U });

        return batch;
    }

    [Fact]
    public void A_short_read_gets_every_total_memory_has_and_older_ones_from_files()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Totals(rows: [(0, HistoryDimension.Grids, 1L), (30, HistoryDimension.Grids, 1L)]), Name);

        Assert.Equal(2, store.Groups(Server, HistoryDimension.Grids, s_started.AddMinutes(-1), s_started.AddMinutes(5)).Count);

        // Quasar restarted, so the skipped total has gone, and only what came since is in memory.
        store = NewStore();
        store.Add(Server, Totals(rows: [(60, HistoryDimension.Grids, 1L)]), Name);

        List<StoredGroup> rows = store.Groups(Server, HistoryDimension.Grids, s_started.AddMinutes(-1), s_started.AddMinutes(5));

        Assert.Equal([s_started, s_started.AddSeconds(60)], rows.Select(row => row.Time));
    }

    [Fact]
    public void An_old_short_range_stops_where_it_ends()
    {
        NewStore().Add(Server, Totals(rows: [(0, HistoryDimension.Grids, 1L), (240, HistoryDimension.Grids, 2L)]), Name);

        HistoryStore store = NewStore();
        store.Add(Server, Totals(rows: [(600, HistoryDimension.Grids, 3L)]), Name);

        StoredGroup row = Assert.Single(store.Groups(Server, HistoryDimension.Grids, s_started.AddSeconds(-30), s_started.AddSeconds(60)));

        Assert.Equal(1L, row.Key);
    }

    [Fact]
    public void A_short_range_reaching_memory_gets_files_and_memory_with_nothing_twice()
    {
        NewStore().Add(Server, Totals(rows: [(0, HistoryDimension.Grids, 1L)]), Name);

        HistoryStore store = NewStore();
        store.Add(Server, Totals(rows: [(240, HistoryDimension.Grids, 1L)]), Name);

        Assert.Equal(2, store.Groups(Server, HistoryDimension.Grids, s_started.AddSeconds(-30), s_started.AddMinutes(10)).Count);
    }

    [Fact]
    public void A_range_from_the_start_of_time_doesnt_let_a_skipped_row_through()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Totals(rows: [(0, HistoryDimension.Grids, 1L), (0, HistoryDimension.Mods, 2L)]), Name);

        StoredGroup row = Assert.Single(store.GroupsOf(Server, [HistoryDimension.Grids], DateTime.MinValue, DateTime.MaxValue));

        Assert.Equal(HistoryDimension.Grids, row.Dimension);
    }

    [Fact]
    public void A_point_says_how_long_it_covers_and_a_minute_adds_them_up()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Batch(0, 125), Name);

        HistoryPoint second = store.Points(Server, s_started, s_started.AddMinutes(5), int.MaxValue)[0];
        Assert.Equal(1f, second.Seconds, 3);
        Assert.Equal(60U, second.SimulatedFrames);

        HistoryPoint minute = store.Points(Server, s_started, s_started.AddDays(2), 100)[0];
        Assert.Equal(60f, minute.Seconds, 3);
        Assert.Equal(3600U, minute.SimulatedFrames);
    }

    [Fact]
    public void Rows_nobody_asked_for_are_skipped_and_the_rest_still_line_up()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Totals(rows: [(0, HistoryDimension.Grids, 1L), (0, HistoryDimension.Mods, 2L), (0, HistoryDimension.Owners, 3L)]), Name);

        List<StoredGroup> rows = NewStore().GroupsOf(Server, [HistoryDimension.Grids, HistoryDimension.Owners], s_started.AddHours(-2), s_started.AddHours(2));

        Assert.Equal([HistoryDimension.Grids, HistoryDimension.Owners], rows.Select(row => row.Dimension));
        Assert.Equal("Grid 3", rows[1].Name);
    }

    [Fact]
    public void A_sample_shows_up_even_with_nothing_in_the_lists_asked_for()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Totals(rows: [(0, HistoryDimension.Mods, 2L), (0, HistoryDimension.Rpcs, 3L)]), Name);

        StoredGroup row = Assert.Single(store.SampleGroups(Server, [HistoryDimension.Grids], s_started.AddHours(-2), s_started.AddHours(2)));

        Assert.Equal(HistoryDimension.None, row.Dimension);
        Assert.Equal(s_started, row.Time);
    }

    [Fact]
    public void A_total_split_across_two_batches_is_told_of_once_whole()
    {
        HistoryStore store = NewStore();
        var told = new List<DateTime>();
        store.SampleKept += (_, time, _) => told.Add(time);

        store.Add(Server, Totals(2L, (0, HistoryDimension.Grids, 1L)), Name);

        Assert.Empty(told);
        Assert.DoesNotContain(store.SampleGroups(Server, [HistoryDimension.Grids], s_started.AddHours(-2), s_started.AddHours(2)), row => row.Time == s_started);

        store.Add(Server, Totals(rows: [(0, HistoryDimension.Grids, 2L)]), Name);

        Assert.Equal([s_started], told);
        Assert.Equal(2, store.SampleGroups(Server, [HistoryDimension.Grids], s_started.AddHours(-2), s_started.AddHours(2)).Count);
    }

    [Fact]
    public void A_total_arriving_after_a_whole_one_leaves_the_latest_whole()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Totals(rows: [(0, HistoryDimension.Grids, 1L)]), Name);
        store.Add(Server, Totals(2L, (30, HistoryDimension.Grids, 2L)), Name);

        Assert.Equal(1L, Assert.Single(store.LatestGroups(Server, HistoryDimension.Grids)).Key);
    }

    [Fact]
    public void A_sample_says_which_came_before_it()
    {
        HistoryStore store = NewStore();
        var told = new List<(DateTime Time, DateTime Since)>();
        store.SampleKept += (_, time, since) => told.Add((time, since));

        store.Add(Server, Totals(rows: [(0, HistoryDimension.Grids, 1L)]), Name);
        store.Add(Server, Totals(rows: [(120, HistoryDimension.Grids, 1L)]), Name);

        Assert.Equal(default, told[0].Since);
        Assert.Equal(s_started, told[1].Since);
    }

    [Fact]
    public void The_sample_rate_carries_on_after_a_Quasar_restart()
    {
        NewStore().Add(Server, Totals(rows: [(0, HistoryDimension.Grids, 1L)]), Name);

        HistoryStore store = NewStore();
        var told = new List<DateTime>();
        store.SampleKept += (_, time, _) => told.Add(time);

        store.Add(Server, Totals(rows: [(30, HistoryDimension.Grids, 1L)]), Name);

        Assert.Empty(told);
        Assert.Single(store.SampleGroups(Server, [HistoryDimension.Grids], s_started.AddHours(-2), s_started.AddHours(2)));
    }

    [Fact]
    public void A_sample_nobody_was_told_of_is_told_of_after_a_Quasar_restart()
    {
        NewStore().Add(Server, Totals(2L, (0, HistoryDimension.Grids, 1L)), Name);

        HistoryStore store = NewStore();
        var told = new List<DateTime>();
        store.SampleKept += (_, time, _) => told.Add(time);

        store.Add(Server, Totals(rows: [(0, HistoryDimension.Grids, 2L)]), Name);

        Assert.Equal([s_started], told);
    }

    [Fact]
    public void Latest_figures_come_with_when_the_server_started()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Batch(0, 3), Name);

        Assert.Equal(s_started.AddSeconds(2), store.Latest(Server)!.Time);
        Assert.Equal(s_started.Ticks, store.StartedUtcTicks(Server));
    }

    [Fact]
    public void Recent_points_come_back_with_their_real_times()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Batch(0, 10), Name);

        List<HistoryPoint> points = store.Points(Server, s_started, s_started.AddSeconds(30), 100);

        Assert.Equal(10, points.Count);
        Assert.Equal(s_started.AddSeconds(3), points[3].Time);
        Assert.Equal(1f, points[3].SimSpeed, 3);
        Assert.Equal(15f, points[3].FrameP99, 3);
    }

    [Fact]
    public void Scope_times_are_per_frame()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Batch(0, 1, scopeUnits: 20000U), Name);

        Assert.Equal(2f, store.Latest(Server)!.Scopes[HistoryScopes.Physics], 3);
    }

    [Fact]
    public void Position_moves_on_with_each_batch()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Batch(0, 10), Name);

        Assert.Equal(10L, store.Position(Server).NextRollup);
        Assert.Equal(s_started.Ticks, store.Position(Server).StartedUtcTicks);
    }

    [Fact]
    public void Where_it_got_to_survives_a_restart()
    {
        NewStore().Add(Server, Batch(0, 10), Name);

        Assert.Equal(10L, NewStore().Position(Server).NextRollup);
    }

    [Fact]
    public void History_survives_a_restart_through_its_files()
    {
        NewStore().Add(Server, Batch(0, 10), Name);

        // A new store has nothing in memory, so this can only come from disk.
        List<HistoryPoint> points = NewStore().Points(Server, s_started, s_started.AddMinutes(5), 100);

        Assert.Equal(10, points.Count);
    }

    [Fact]
    public void Network_figures_survive_a_restart_through_their_files()
    {
        HistoryBatch batch = Batch(0, 1);
        batch.Rollups[0].SentKb = 120U;
        batch.Rollups[0].ReceivedKb = 30U;
        batch.Rollups[0].StateSyncKb = 70U;
        batch.Rollups[0].EventsKb = 20U;
        batch.Rollups[0].ReplicationKb = 10U;
        batch.Rollups[0].RpcsIn = 9U;
        NewStore().Add(Server, batch, Name);

        HistoryPoint point = Assert.Single(NewStore().Points(Server, s_started, s_started.AddMinutes(5), 100));

        Assert.Equal(120f, point.SentKb);
        Assert.Equal(30f, point.ReceivedKb);
        Assert.Equal(70f, point.StateSyncKb);
        Assert.Equal(20f, point.EventsKb);
        Assert.Equal(10f, point.ReplicationKb);
        Assert.Equal(9f, point.RpcsIn);
    }

    [Fact]
    public void A_minute_holds_the_average_rate_and_not_the_sum()
    {
        // These are per second, so adding up 60 of them would read 60 times too high on a long range.
        HistoryBatch batch = Batch(0, 125);

        foreach (HistoryRollup row in batch.Rollups)
            row.SentKb = 100U;

        HistoryStore store = NewStore();
        store.Add(Server, batch, Name);

        List<HistoryPoint> points = store.Points(Server, s_started, s_started.AddDays(2), 100);

        Assert.Equal(100f, points[0].SentKb, 3);
    }

    [Fact]
    public void A_finished_minute_goes_to_the_minute_tier()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Batch(0, 125), Name);

        // A range past 6 hours reads minutes, and the third minute isn't finished yet.
        List<HistoryPoint> points = store.Points(Server, s_started, s_started.AddDays(2), 100);

        Assert.Equal(2, points.Count);
        Assert.Equal(s_started.AddMinutes(1), points[1].Time);
    }

    [Fact]
    public void A_minute_keeps_the_worst_frame_of_its_seconds()
    {
        HistoryStore store = NewStore();
        HistoryBatch batch = Batch(0, 61);
        batch.Rollups[30].FrameP99 = 900_000U;
        store.Add(Server, batch, Name);

        Assert.Equal(90f, store.Points(Server, s_started, s_started.AddDays(2), 100)[0].FrameP99, 3);
    }

    [Fact]
    public void Thinning_keeps_the_worst_dip()
    {
        HistoryStore store = NewStore();
        HistoryBatch batch = Batch(0, 1000);
        batch.Rollups[437].SimulatedFrames = 6U;
        store.Add(Server, batch, Name);

        List<HistoryPoint> points = store.Points(Server, s_started, s_started.AddMinutes(30), 50);

        Assert.True(points.Count <= 51);
        Assert.Contains(points, point => point.SimSpeed < 0.2f);
    }

    [Fact]
    public void A_pause_stays_marked_as_a_gap()
    {
        HistoryStore store = NewStore();
        HistoryBatch batch = Batch(0, 3);
        batch.Rollups[1].AfterGap = true;
        store.Add(Server, batch, Name);

        Assert.True(store.Points(Server, s_started, s_started.AddMinutes(1), 100)[1].AfterGap);
    }

    [Fact]
    public void A_restart_starts_positions_again()
    {
        HistoryStore store = NewStore();
        store.Add(Server, Batch(0, 10), Name);

        store.Restart(Server, s_started.AddDays(1).Ticks);

        Assert.Equal(0L, store.Position(Server).NextRollup);
        Assert.Equal(s_started.AddDays(1).Ticks, store.Position(Server).StartedUtcTicks);
    }

    [Fact]
    public void Group_rows_keep_their_names()
    {
        HistoryStore store = NewStore();
        HistoryBatch batch = Batch(0, 1);
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Grids, Key = 7L, MainUnits = 20000U, WorkerUnits = 10000U });
        store.Add(Server, batch, Name);

        StoredGroup row = Assert.Single(store.LatestGroups(Server, HistoryDimension.Grids));

        Assert.Equal("Grid 7", row.Name);
        Assert.Equal(2f, row.MainMs, 3);
        Assert.Equal(1f, row.WorkerMs, 3);
    }

    [Fact]
    public void Group_rows_survive_a_restart_through_their_files()
    {
        HistoryBatch batch = Batch(0, 1);
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Grids, Key = 7L, MainUnits = 20000U });
        NewStore().Add(Server, batch, Name);

        List<StoredGroup> rows = NewStore().Groups(Server, HistoryDimension.Grids, s_started.AddHours(-1), s_started.AddHours(1));

        Assert.Equal("Grid 7", Assert.Single(rows).Name);
    }

    [Fact]
    public void Census_survives_a_restart_through_its_files()
    {
        HistoryBatch batch = Batch(0, 1);
        batch.Census.Add(new HistoryCensus { Blocks = 1234U, PingMean = 21.3d });
        NewStore().Add(Server, batch, Name);

        StoredCensus row = Assert.Single(NewStore().Census(Server, s_started.AddHours(-1), s_started.AddHours(1)));

        Assert.Equal(1234U, row.Row.Blocks);
        Assert.Equal(21.3d, row.Row.PingMean, 6);
    }

    [Fact]
    public void Bytes_on_a_group_row_survive_a_restart_through_their_files()
    {
        HistoryBatch batch = Batch(0, 1);
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.RpcsSent, Key = 1L, BytesPerSecond = 4096U, CallsPerFrame = 0.25f });
        NewStore().Add(Server, batch, Name);

        StoredGroup row = Assert.Single(NewStore().Groups(Server, HistoryDimension.RpcsSent, s_started.AddHours(-1), s_started.AddHours(1)));

        Assert.Equal(4096f, row.BytesPerSecond);
        Assert.Equal(0.25f, row.CallsPerFrame);
    }

    [Fact]
    public void The_latest_rpcs_sent_come_biggest_first()
    {
        HistoryBatch batch = Batch(0, 1);
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.RpcsSent, Key = 1L, BytesPerSecond = 100U });
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.RpcsSent, Key = 2L, BytesPerSecond = 900U });

        HistoryStore store = NewStore();
        store.Add(Server, batch, Name);

        Assert.Equal([900f, 100f], store.LatestGroups(Server, HistoryDimension.RpcsSent).Select(row => row.BytesPerSecond));
    }

    [Fact]
    public void Concealed_counts_survive_a_restart_through_their_files()
    {
        HistoryBatch batch = Batch(0, 1);
        batch.Census.Add(new HistoryCensus { Blocks = 1234U, ConcealedGrids = 4U, ConcealedBlocks = 400U });
        NewStore().Add(Server, batch, Name);

        StoredCensus row = Assert.Single(NewStore().Census(Server, s_started.AddHours(-1), s_started.AddHours(1)));

        Assert.Equal(4U, row.Row.ConcealedGrids);
        Assert.Equal(400U, row.Row.ConcealedBlocks);
    }

    [Fact]
    public void A_file_cut_off_by_a_crash_keeps_what_came_before()
    {
        NewStore().Add(Server, Batch(0, 10), Name);

        string file = Directory.GetFiles(Path.Combine(_directory, StateFiles.FolderFor(Server), "seconds"), "*.bin").Single();
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write))
            stream.SetLength(stream.Length - 5L);

        Assert.Equal(9, NewStore().Points(Server, s_started, s_started.AddMinutes(5), 100).Count);
    }

    private static HistoryBatch WithPlayers(int second, params long[] identities)
    {
        var batch = new HistoryBatch { StartedUtcTicks = s_started.Ticks, HasDetail = true };
        batch.Census.Add(new HistoryCensus { UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks });

        foreach (long identity in identities)
            batch.Players.Add(new HistoryPlayer { IdentityId = identity, FactionId = 5L, PingMs = 40U });

        return batch;
    }

    [Fact]
    public void Players_are_kept_with_their_names_and_the_time_of_their_pass()
    {
        HistoryStore store = NewStore();
        store.Add(Server, WithPlayers(90, 1L, 2L), Name);

        List<StoredPlayer> rows = store.Players(Server, s_started, s_started.AddMinutes(5));

        Assert.Equal(2, rows.Count);
        Assert.Equal(s_started.AddSeconds(90), rows[0].Time);
        Assert.Equal("Grid 1", rows[0].Name);
        Assert.Equal("F5", rows[0].Faction);
        Assert.Equal(40U, rows[0].PingMs);
    }

    [Fact]
    public void Players_are_stored_once_a_minute_not_every_pass()
    {
        HistoryStore store = NewStore();
        store.Add(Server, WithPlayers(0, 1L), Name);
        store.Add(Server, WithPlayers(30, 1L), Name);
        store.Add(Server, WithPlayers(60, 1L), Name);

        Assert.Equal(2, store.Players(Server, s_started, s_started.AddMinutes(5)).Count);
    }

    [Fact]
    public void An_empty_server_is_marked_and_not_left_as_a_hole()
    {
        HistoryStore store = NewStore();
        store.Add(Server, WithPlayers(0), Name);

        StoredPlayer row = Assert.Single(store.Players(Server, s_started, s_started.AddMinutes(5)));

        Assert.Empty(row.Name);
    }

    [Fact]
    public void Players_survive_a_restart_through_their_files()
    {
        NewStore().Add(Server, WithPlayers(0, 1L), Name);

        Assert.Single(NewStore().Players(Server, s_started.AddMinutes(-90), s_started.AddMinutes(30)));
    }

    [Fact]
    public void A_players_traffic_survives_a_restart_through_their_files()
    {
        HistoryBatch batch = WithPlayers(0, 1L);
        batch.Players[0].SentBytesPerSecond = 5000U;
        batch.Players[0].ReceivedBytesPerSecond = 600U;
        batch.Players[0].RpcsPerSecond = 7U;
        batch.Players[0].QueuedBytes = 80000U;
        NewStore().Add(Server, batch, Name);

        StoredPlayer player = Assert.Single(NewStore().Players(Server, s_started.AddMinutes(-90), s_started.AddMinutes(30)));

        Assert.Equal(5000U, player.SentBytesPerSecond);
        Assert.Equal(600U, player.ReceivedBytesPerSecond);
        Assert.Equal(7U, player.RpcsPerSecond);
        Assert.Equal(80000U, player.QueuedBytes);
    }

    [Fact]
    public void Servers_are_kept_apart()
    {
        HistoryStore store = NewStore();
        store.Add("one", Batch(0, 10), Name);

        Assert.Empty(store.Points("two", s_started, s_started.AddMinutes(1), 100));
    }

    [Fact]
    public void A_change_of_culture_keeps_history_and_names_files_the_same()
    {
        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            NewStore().Add(Server, Batch(0, 10), Name);

            // The Thai culture's calendar counts years from 543 BC. A new store prunes on its first batch.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            NewStore().Add(Server, Batch(10, 10), Name);

            string year = s_started.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string[] files = Directory.GetFiles(Path.Combine(_directory, StateFiles.FolderFor(Server), "seconds"), "*.bin");

            Assert.NotEmpty(files);
            Assert.All(files, file => Assert.StartsWith(year, Path.GetFileName(file), StringComparison.Ordinal));
            Assert.Equal(20, NewStore().Points(Server, s_started, s_started.AddMinutes(5), 100).Count);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void History_is_kept_for_3_days_unless_changed()
    {
        Assert.Equal(3, new MonitorSettings(_directory).RetentionDays);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(7, 7)]
    [InlineData(30, 7)]
    [InlineData(0, 1)]
    public void Retention_stays_between_1_and_7_days(int asked, int kept)
    {
        var settings = new MonitorSettings(_directory) { RetentionDays = asked };

        Assert.Equal(kept, settings.RetentionDays);
    }

    [Fact]
    public void Retention_survives_a_restart()
    {
        _ = new MonitorSettings(_directory) { RetentionDays = 5 };

        Assert.Equal(5, new MonitorSettings(_directory).RetentionDays);
    }

    [Fact]
    public void History_past_retention_is_deleted()
    {
        var settings = new MonitorSettings(_directory) { RetentionDays = 1 };
        var store = new HistoryStore(_directory, settings, NullLogger<HistoryStore>.Instance);

        // A start this long ago makes its minute file out of date at once.
        HistoryBatch batch = Batch(0, 125);
        batch.StartedUtcTicks = DateTime.UtcNow.AddDays(-30).Ticks;

        store.Add(Server, batch, Name);

        string[] left = Directory.GetFiles(Path.Combine(_directory, StateFiles.FolderFor(Server), "minutes"), "*.bin");
        Assert.Empty(left);
    }

    [Fact]
    public void A_series_keeps_its_colour_across_restarts()
    {
        // A fixed answer, because comparing two calls in one process would pass with a hash that changes every run.
        Assert.Equal("monitor-c-5", ChartColors.For("Big Ship"));
    }
}
