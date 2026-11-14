using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Api;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class PublicApiTests : IDisposable
{
    private const string History = "cluster/slot-1";

    private static readonly MonitorServer s_server = new("node-1", "One", History);
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(10);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _started = DateTime.UtcNow.AddMinutes(-10);
    private readonly ScriptedServers _servers = new();
    private readonly HistoryStore _store;
    private readonly MonitorRuns _runs;
    private readonly MonitorApi _api;

    public PublicApiTests()
    {
        var settings = new MonitorSettings(_directory);
        _store = new HistoryStore(_directory, settings, NullLogger<HistoryStore>.Instance);
        _runs = new MonitorRuns(_servers, _directory, settings, new NoLifetime(), NullLogger<MonitorRuns>.Instance) { PollEvery = TimeSpan.FromMilliseconds(1) };

        var baselines = new MonitorBaselines(_store, settings, NullLogger<MonitorBaselines>.Instance);
        var incidents = new MonitorIncidents(_servers, _directory, settings, baselines, NullLogger<MonitorIncidents>.Instance);
        var collector = new MonitorCollector(_servers, _store, incidents, _runs, settings, NullLogger<MonitorCollector>.Instance);

        _api = new MonitorApi(_servers, _store, new HistoryWindows(_store), _runs, collector, incidents, settings, new NoLifetime(), NullLogger<MonitorApi>.Instance);
        _servers.Servers.Add(s_server);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static MonitorRequest Run(params MonitorList[] lists) =>
        new() { Server = History, Source = MonitorSource.Run, Lists = lists, Seconds = 1, RequestedBy = "a test" };

    private MonitorRequest FromHistory(params MonitorList[] lists) =>
        new() { Server = History, Source = MonitorSource.History, Lists = lists, From = _started, To = DateTime.UtcNow };

    private static MonitorRequest Saved(params MonitorList[] lists) =>
        new() { Server = History, Source = MonitorSource.SavedRun, Lists = lists };

    private Task<MonitorReply> AskAsync(MonitorRequest request) => _api.RequestAsync(request, CancellationToken.None).WaitAsync(s_wait);

    private async Task UntilStartedAsync(int starts)
    {
        for (int i = 0; i < 2000 && _servers.Starts < starts; i++)
            await Task.Delay(5);

        Assert.Equal(starts, _servers.Starts);
    }

    private void History2Sweeps()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, GroupFirst = 0L, GroupHead = 3L };

        void Add(int second, long key, uint mainMs) => batch.Groups.Add(new HistoryGroup
        {
            UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks,
            Dimension = HistoryDimension.Grids,
            Key = key,
            MainUnits = mainMs * 10000U,
            CallsPerFrame = 2U,
        });

        Add(10, 1L, 4U);
        Add(40, 1L, 2U);
        Add(40, 2L, 6U);

        var rollups = new uint[43];
        rollups[HistoryScopes.Frame] = 600000U;
        batch.Rollups.Add(new HistoryRollup { UnitsSinceStart = TimeSpan.FromSeconds(10).Ticks, Frames = 60U, SimulatedFrames = 60U, WindowUnits = 10_000_000U, ScopeUnits = rollups });
        batch.RollupHead = 1L;

        _store.Add(History, batch, (_, key) => "Grid " + key);
    }

    // Two totals far enough apart that both are kept, with a census just before each.
    private void TwoSamples(int firstSecond = 10, int secondSecond = 140, int extraSecond = 0, uint extraFrameMs = 0U)
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, GroupFirst = 0L, GroupHead = 3L, CensusFirst = 0L, CensusHead = 2L, RollupFirst = 0L, RollupHead = 2L };

        void Grid(int second, long key, uint mainMs) => batch.Groups.Add(new HistoryGroup
        {
            UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks,
            Dimension = HistoryDimension.Grids,
            Key = key,
            MainUnits = mainMs * 10000U,
            Blocks = 500U,
        });

        void World(int second, uint players) => batch.Census.Add(new HistoryCensus
        {
            UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks,
            OnlinePlayers = players,
            Grids = 10U,
            StaticGrids = 4U,
            Blocks = 9000U,
            Asteroids = 7U,
            CpuPercent = 12.5d,
            WorkingSetMb = 900U,
        });

        void Second(int second, uint frameMs)
        {
            var scopes = new uint[43];
            scopes[HistoryScopes.Frame] = 60U * frameMs * 10000U;
            scopes[HistoryScopes.Physics] = 60U * frameMs * 5000U;
            batch.Rollups.Add(new HistoryRollup { UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks, Frames = 60U, SimulatedFrames = 60U, WindowUnits = 10_000_000U, ScopeUnits = scopes });
        }

        World(firstSecond - 1, 3U);
        World(secondSecond - 1, 5U);
        Second(firstSecond - 1, 4U);

        if (extraSecond != 0)
            Second(extraSecond, extraFrameMs);

        Second(secondSecond - 1, 8U);
        Grid(firstSecond, 1L, 4U);
        Grid(secondSecond, 1L, 2U);
        Grid(secondSecond, 2L, 6U);

        _store.Add(History, batch, (_, key) => "Grid " + key);
    }

    private Task<IReadOnlyList<MonitorSample>> SamplesAsync(params MonitorList[] lists) =>
        _api.For(History).SamplesAsync(_started, DateTime.UtcNow, lists, 20, CancellationToken.None).WaitAsync(s_wait);

    [Fact]
    public async Task Samples_come_oldest_first()
    {
        TwoSamples();

        IReadOnlyList<MonitorSample> samples = await SamplesAsync(MonitorList.Grids);

        Assert.Equal(2, samples.Count);
        Assert.True(samples[0].Time < samples[1].Time);
        Assert.Equal(History, samples[0].Server);
    }

    [Fact]
    public async Task A_sample_has_the_figures_from_that_moment()
    {
        TwoSamples();

        IReadOnlyList<MonitorSample> samples = await SamplesAsync(MonitorList.Grids);

        Assert.Equal(3L, samples[0].World!.Players);
        Assert.Equal(5L, samples[1].World!.Players);
        Assert.Equal(4L, samples[1].World!.StaticGrids);
        Assert.Equal(6L, samples[1].World!.DynamicGrids);
        Assert.Equal(9000L, samples[1].World!.Blocks);
        Assert.Equal(12.5d, samples[1].World!.CpuPercent, 6);
        Assert.Equal(900d, samples[1].World!.MemoryMb, 6);
        Assert.Equal(TimeSpan.FromSeconds(139), samples[1].World!.Uptime);

        Assert.Equal(4d, samples[0].Frame!.AverageFrameMs, 3);
        Assert.Equal(8d, samples[1].Frame!.AverageFrameMs, 3);
        Assert.Equal(4d, samples[1].PartsMs["Physics"], 3);
    }

    [Fact]
    public async Task A_grid_keeps_its_id_from_one_sample_to_the_next()
    {
        TwoSamples();

        IReadOnlyList<MonitorSample> samples = await SamplesAsync(MonitorList.Grids);

        Assert.Equal(1L, Assert.Single(samples[0].Lists[0].Rows).Id);
        Assert.Contains(samples[1].Lists[0].Rows, row => row.Id == 1L);
        Assert.Equal(500L, samples[1].Lists[0].Rows[0].Blocks);
    }

    [Fact]
    public async Task A_samples_rows_are_costliest_first_and_cut_to_the_top_asked_for()
    {
        TwoSamples();

        IReadOnlyList<MonitorSample> samples = await _api.For(History).SamplesAsync(_started, DateTime.UtcNow, [MonitorList.Grids], 1, CancellationToken.None).WaitAsync(s_wait);

        Assert.Equal(2L, Assert.Single(samples[1].Lists[0].Rows).Id);
    }

    [Fact]
    public async Task Samples_say_clusters_need_a_run()
    {
        TwoSamples();

        IReadOnlyList<MonitorSample> samples = await SamplesAsync(MonitorList.Clusters);

        MonitorListReply clusters = Assert.Single(samples[0].Lists);
        Assert.False(clusters.Available);
        Assert.Equal(ReplyBuilder.NotInHistory, clusters.Why);
    }

    [Fact]
    public async Task No_lists_means_every_list_history_keeps()
    {
        TwoSamples();

        IReadOnlyList<MonitorSample> samples = await SamplesAsync();

        Assert.Equal(ReplyBuilder.ListsInHistory.Count, samples[0].Lists.Count);
        Assert.DoesNotContain(samples[0].Lists, list => list.List == MonitorList.Clusters);
    }

    [Fact]
    public async Task A_new_sample_tells_subscribers_once_all_of_it_is_here()
    {
        var heard = new TaskCompletionSource<MonitorSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.For(History).SampleRecorded += sample => heard.TrySetResult(sample);

        TwoSamples();

        MonitorSample sample = await heard.Task.WaitAsync(s_wait);

        Assert.Equal(_started.AddSeconds(10), sample.Time);
        Assert.Equal(3L, sample.World!.Players);
        Assert.Equal(ReplyBuilder.ListsInHistory.Count, sample.Lists.Count);
    }

    [Fact]
    public async Task A_told_sample_covers_the_time_since_the_sample_before()
    {
        var heard = new List<MonitorSample>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _api.For(History).SampleRecorded += sample =>
        {
            heard.Add(sample);

            if (heard.Count == 2)
                both.TrySetResult();
        };

        TwoSamples(extraSecond: 70, extraFrameMs: 20U);

        await both.Task.WaitAsync(s_wait);

        Assert.Equal(14d, heard[1].Frame!.AverageFrameMs, 3);
        Assert.Equal(_started.AddSeconds(140), heard[1].Time);
    }

    [Fact]
    public async Task A_sample_counts_only_the_time_the_server_was_there()
    {
        TwoSamples(extraSecond: 70, extraFrameMs: 20U);

        IReadOnlyList<MonitorSample> samples = await SamplesAsync(MonitorList.Grids);

        Assert.Equal(2d, samples[1].Seconds, 3);
        Assert.Equal(120L, samples[1].Frames);
    }

    [Fact]
    public async Task The_first_sample_in_a_range_goes_back_to_the_one_before_it()
    {
        TwoSamples(extraSecond: 70, extraFrameMs: 20U);

        IReadOnlyList<MonitorSample> samples = await _api.For(History).SamplesAsync(_started.AddSeconds(100), DateTime.UtcNow, [MonitorList.Grids], 20, CancellationToken.None).WaitAsync(s_wait);

        Assert.Equal(_started.AddSeconds(140), Assert.Single(samples).Time);
        Assert.Equal(14d, samples[0].Frame!.AverageFrameMs, 3);
    }

    [Fact]
    public async Task A_range_from_the_start_of_time_is_all_of_history()
    {
        TwoSamples();

        IReadOnlyList<MonitorSample> samples = await _api.For(History).SamplesAsync(DateTime.MinValue, DateTime.UtcNow, [MonitorList.Grids], 20, CancellationToken.None).WaitAsync(s_wait);

        Assert.Equal(2, samples.Count);
    }

    [Fact]
    public async Task Lag_doesnt_count_double_across_a_range()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };

        void Grid(int second, uint mainMs) => batch.Groups.Add(new HistoryGroup
        {
            UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks,
            Dimension = HistoryDimension.Grids,
            Key = 1L,
            MainUnits = mainMs * 10000U,
        });

        Grid(10, 2U);
        Grid(130, 2U);
        Grid(250, 8U);
        Grid(310, 8U);

        _store.Add(History, batch, (_, key) => "Grid " + key);

        MonitorReply reply = await AskAsync(FromHistory(MonitorList.Grids));

        Assert.Equal(4d, Assert.Single(Assert.Single(reply.Lists).Rows).MainMs, 3);
    }

    [Fact]
    public async Task A_refused_run_doesnt_use_up_the_minute()
    {
        _servers.Refuse = "Busy";
        Assert.Equal(MonitorReplyState.Refused, (await AskAsync(Run(MonitorList.Grids))).State);

        _servers.Refuse = null;
        Assert.Equal(MonitorReplyState.Finished, (await AskAsync(Run(MonitorList.Grids))).State);
    }

    [Fact]
    public async Task A_failed_run_says_why()
    {
        _servers.Result = new RunResultResponse { State = RunState.Failed, Problem = "The run failed: a test said so" };

        MonitorReply reply = await AskAsync(Run(MonitorList.Grids));

        Assert.Equal(MonitorReplyState.Failed, reply.State);
        Assert.Equal("The run failed: a test said so", reply.Message);
    }

    [Fact]
    public void Runs_saved_in_one_second_all_keep()
    {
        for (int run = 0; run < 3; run++)
            _runs.SaveLagRun(History, new RunResultResponse { State = RunState.Finished, RequestedBy = RunLists.LagTrip });

        List<SavedRunInfo> saved = _runs.List(History);

        Assert.Equal(3, saved.Count);
        Assert.All(saved, run => Assert.True(run.ByLagTrip));
    }

    [Fact]
    public async Task A_run_followed_here_isnt_saved_twice()
    {
        await AskAsync(Run(MonitorList.Grids));

        _runs.NoteRunFinished(History, 1L);
        await _runs.CollectFinishedRunsAsync(s_server, CancellationToken.None);

        Assert.Single(_runs.List(History));
    }

    [Fact]
    public async Task A_finished_run_that_cant_be_read_is_asked_for_again()
    {
        _runs.NoteRunFinished(History, 5L);
        _servers.FailResults = true;

        await Assert.ThrowsAsync<TimeoutException>(() => _runs.CollectFinishedRunsAsync(s_server, CancellationToken.None));

        _servers.FailResults = false;
        await _runs.CollectFinishedRunsAsync(s_server, CancellationToken.None);

        Assert.Single(_runs.List(History));
    }

    [Fact]
    public async Task A_restart_forgets_runs_that_were_waiting()
    {
        _runs.NoteRunFinished(History, 5L);
        _runs.ForgetRuns(History);

        await _runs.CollectFinishedRunsAsync(s_server, CancellationToken.None);

        Assert.Empty(_runs.List(History));
    }

    [Fact]
    public void A_run_that_found_no_clusters_has_an_empty_list()
    {
        var run = new RunResultResponse { State = RunState.Finished, ClustersTimed = true };

        MonitorListReply clusters = Assert.Single(ReplyBuilder.FromRun(Run(MonitorList.Clusters), run, "id", DateTime.UtcNow).Lists);

        Assert.True(clusters.Available);
        Assert.Empty(clusters.Rows);
    }

    [Fact]
    public void A_replys_times_come_from_when_the_run_ended()
    {
        DateTime ended = _started.AddMinutes(1);
        var run = new RunResultResponse { State = RunState.Finished, Seconds = 10d, EndedUtcTicks = ended.Ticks };

        MonitorReply reply = ReplyBuilder.FromRun(Run(MonitorList.Grids), run, "id", DateTime.UtcNow);

        Assert.Equal(ended, reply.To);
        Assert.Equal(ended.AddSeconds(-10), reply.From);
    }

    [Fact]
    public async Task A_read_sample_and_a_told_one_are_the_same_figures()
    {
        var heard = new List<MonitorSample>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _api.For(History).SampleRecorded += sample =>
        {
            heard.Add(sample);

            if (heard.Count == 2)
                both.TrySetResult();
        };

        TwoSamples(extraSecond: 70, extraFrameMs: 20U);

        await both.Task.WaitAsync(s_wait);

        IReadOnlyList<MonitorSample> read = await SamplesAsync(MonitorList.Grids);

        for (int sample = 0; sample < 2; sample++)
        {
            Assert.Equal(heard[sample].Time, read[sample].Time);
            Assert.Equal(heard[sample].Frame, read[sample].Frame);
            Assert.Equal(heard[sample].Frames, read[sample].Frames);
        }
    }

    [Fact]
    public async Task A_total_still_arriving_isnt_a_sample_yet()
    {
        var first = new HistoryBatch { StartedUtcTicks = _started.Ticks, GroupFirst = 0L, GroupHead = 2L };
        first.Groups.Add(new HistoryGroup { UnitsSinceStart = TimeSpan.FromSeconds(10).Ticks, Dimension = HistoryDimension.Grids, Key = 1L });
        _store.Add(History, first, (_, key) => "Grid " + key);

        Assert.Empty(await SamplesAsync(MonitorList.Grids));

        var rest = new HistoryBatch { StartedUtcTicks = _started.Ticks, GroupFirst = 1L, GroupHead = 2L };
        rest.Groups.Add(new HistoryGroup { UnitsSinceStart = TimeSpan.FromSeconds(10).Ticks, Dimension = HistoryDimension.Grids, Key = 2L });
        _store.Add(History, rest, (_, key) => "Grid " + key);

        Assert.Equal(2, Assert.Single(await SamplesAsync(MonitorList.Grids)).Lists[0].Rows.Count);
    }

    private void TwoGridsOfOneName()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, GroupFirst = 0L, GroupHead = 3L };

        void Add(int second, long key, uint mainMs) => batch.Groups.Add(new HistoryGroup
        {
            UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks,
            Dimension = HistoryDimension.Grids,
            Key = key,
            MainUnits = mainMs * 10000U,
        });

        Add(10, 1L, 2U);
        Add(10, 2L, 8U);
        Add(140, 2L, 8U);

        _store.Add(History, batch, (_, _) => "Ship");
    }

    [Fact]
    public async Task Two_grids_with_one_name_stay_two_rows()
    {
        TwoGridsOfOneName();

        MonitorReply reply = await AskAsync(FromHistory(MonitorList.Grids));
        IReadOnlyList<MonitorRow> rows = Assert.Single(reply.Lists).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal(8d, rows[0].MainMs, 3);
        Assert.Equal(2L, rows[0].Id);
        Assert.Equal(1d, rows[0].SeenShare, 6);
        Assert.Equal(0.5d, rows[1].SeenShare, 6);
    }

    [Fact]
    public async Task One_grids_cost_over_time_can_be_read_by_its_id()
    {
        TwoGridsOfOneName();

        IReadOnlyList<MonitorCostPoint> byId = await _api.For(History).CostHistoryAsync(MonitorList.Grids, 2L, _started, DateTime.UtcNow, CancellationToken.None).WaitAsync(s_wait);
        IReadOnlyList<MonitorCostPoint> byName = await _api.For(History).CostHistoryAsync(MonitorList.Grids, "Ship", _started, DateTime.UtcNow, CancellationToken.None).WaitAsync(s_wait);

        Assert.Equal(2, byId.Count);
        Assert.Equal(3, byName.Count);
    }

    [Fact]
    public void Comparing_stretches_keeps_two_grids_with_one_name_apart()
    {
        WindowRow[] before = [new WindowRow("Ship", 1L, 1d, 0d, 0d, 0d, 1d), new WindowRow("Ship", 2L, 5d, 0d, 0d, 0d, 1d)];
        WindowRow[] after = [new WindowRow("Ship", 2L, 7d, 0d, 0d, 0d, 1d)];

        List<ComparedRow> rows = HistoryWindows.Compare(before, after);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("Ship", row.Name));
        Assert.Equal(2d, rows[0].Change);
    }

    [Fact]
    public async Task History_stops_at_the_top_rows_of_a_list()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };

        for (long key = 1L; key <= 25L; key++)
            batch.Groups.Add(new HistoryGroup { UnitsSinceStart = TimeSpan.FromSeconds(10).Ticks, Dimension = HistoryDimension.Grids, Key = key, MainUnits = 10000U });

        _store.Add(History, batch, (_, key) => "Grid " + key);

        MonitorReply reply = await AskAsync(FromHistory(MonitorList.Grids));

        Assert.Equal(ReplyBuilder.RowsPerList, Assert.Single(reply.Lists).Rows.Count);
    }

    [Theory]
    [InlineData(MonitorList.Players)]
    [InlineData(MonitorList.Factions)]
    public void Nobody_and_no_faction_have_no_id(MonitorList list)
    {
        Assert.Null(ReplyBuilder.StableId(list, -1L));
        Assert.Equal(9L, ReplyBuilder.StableId(list, 9L));
    }

    [Fact]
    public async Task A_server_thats_gone_has_nobody_online()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, HasDetail = true };
        batch.Players.Add(new HistoryPlayer { IdentityId = 5L });
        batch.Factions.Add(new HistoryFaction { FactionId = 6L });
        _store.Add(History, batch, (_, key) => "Thing " + key);

        Assert.Single(await _api.For(History).PlayersAsync(CancellationToken.None).WaitAsync(s_wait));

        _servers.Servers.Clear();

        Assert.Empty(await _api.For(History).PlayersAsync(CancellationToken.None).WaitAsync(s_wait));
        Assert.Empty(await _api.For(History).FactionsAsync(CancellationToken.None).WaitAsync(s_wait));
    }

    [Fact]
    public async Task A_refusal_for_a_server_thats_gone_doesnt_use_up_the_minute()
    {
        IMonitorTools tools = _api.For("gone");

        MonitorInside refused = await tools.LookInsideAsync("id", 5, "a test", CancellationToken.None).WaitAsync(s_wait);
        Assert.Equal(MonitorApi.NotConnected, refused.Message);

        _servers.Servers.Add(new MonitorServer("node-2", "Two", "gone"));

        MonitorInside next = await tools.LookInsideAsync("id", 5, "a test", CancellationToken.None).WaitAsync(s_wait);
        Assert.DoesNotContain("once a minute", next.Message ?? string.Empty);
    }

    [Fact]
    public void A_run_started_elsewhere_shows_once_its_heard_of()
    {
        _runs.NoteRunStarted(History, DateTime.UtcNow, 30);

        MonitorRunProgress run = _api.For(History).CurrentRun()!;

        Assert.InRange(run.SecondsLeft, 1, 30);
        Assert.True(_api.For(History).Status().RunGoing);
    }

    [Fact]
    public void A_run_started_elsewhere_stops_showing_when_its_time_is_up()
    {
        _runs.NoteRunStarted(History, DateTime.UtcNow.AddMinutes(-5), 30);

        Assert.Null(_api.For(History).CurrentRun());
    }

    [Fact]
    public async Task Cancelling_while_a_run_starts_still_follows_it_and_saves_it()
    {
        _servers.HoldStart = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();

        Task<MonitorReply> asked = _api.RequestAsync(Run(MonitorList.Grids), cancel.Token);

        for (int i = 0; i < 2000 && !_runs.Running(History); i++)
            await Task.Delay(5);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked.WaitAsync(s_wait));

        _servers.HoldStart.SetResult();

        for (int i = 0; i < 2000 && _runs.Running(History); i++)
            await Task.Delay(5);

        Assert.Single(_runs.List(History));
    }

    [Fact]
    public async Task A_run_in_the_way_says_how_long_it_has_left()
    {
        _servers.Refuse = "Busy";
        _servers.RefuseSeconds = 7;

        MonitorReply reply = await AskAsync(Run(MonitorList.Grids));

        Assert.Equal(MonitorReplyState.Refused, reply.State);
        Assert.Equal(TimeSpan.FromSeconds(7), reply.RetryAfter);
    }

    [Fact]
    public async Task A_lag_trips_run_is_saved_and_told_of()
    {
        var heard = new TaskCompletionSource<MonitorRunFinished>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.For(History).RunFinished += finished => heard.TrySetResult(finished);

        _runs.SaveLagRun(History, new RunResultResponse { State = RunState.Finished, RequestedBy = RunLists.LagTrip });

        Assert.Equal(History, (await heard.Task.WaitAsync(s_wait)).Server);
        Assert.Single(_runs.List(History));
    }

    [Fact]
    public async Task One_servers_tools_arent_told_about_another()
    {
        IMonitorTools other = _api.For("somewhere/else");
        bool heardOther = false;
        other.EventRecorded += _ => heardOther = true;

        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.For(History).EventRecorded += _ => heard.TrySetResult();

        _store.ServerStarted(History, _started.Ticks, [], []);

        // Events go one at a time, so anything the other server's tools were going to hear has been heard by now.
        await heard.Task.WaitAsync(s_wait);

        Assert.False(heardOther);
        Assert.Same(other, _api.For("somewhere/else"));
    }

    [Fact]
    public void Status_says_a_server_is_online_and_when_it_started()
    {
        TwoSamples();

        MonitorStatus status = _api.For(History).Status();

        Assert.True(status.Connected);
        Assert.Equal(_started, status.Started);
        Assert.NotNull(status.LastData);
        Assert.False(status.RunGoing);
    }

    [Fact]
    public void Latest_has_the_live_numbers()
    {
        TwoSamples();

        MonitorLatest latest = _api.For(History).Latest()!;

        Assert.Equal(5L, latest.World!.Players);
        Assert.Equal(7L, latest.World!.Asteroids);
        Assert.Equal(12.5d, latest.World!.CpuPercent, 6);
    }

    [Fact]
    public void A_run_says_what_the_frame_went_on_and_how_big_each_row_is()
    {
        var run = new RunResultResponse
        {
            State = RunState.Finished,
            Parts = { ["Physics"] = 3d },
            Rows =
            {
                new RunRow { List = RunLists.Grids, Name = "Ship (Someone [ABC])", PlainName = "Ship", Key = 7L, Blocks = 90L },
                new RunRow { List = RunLists.Factions, Name = "ABC", Key = 9L, Grids = 2L, Blocks = 40L },
                new RunRow { List = RunLists.Clusters, Name = "A cluster", Grids = 3L, SizeKm = 1.5d, TopGrids = [new RunRow { Name = "Ship", Key = 7L, Blocks = 90L }] },
            },
        };

        MonitorReply reply = ReplyBuilder.FromRun(Run(MonitorList.Grids, MonitorList.Factions, MonitorList.Clusters), run, "id", DateTime.UtcNow);

        Assert.Equal(3d, reply.PartsMs["Physics"]);

        MonitorRow grid = Assert.Single(reply.Lists[0].Rows);
        Assert.Equal(90L, grid.Blocks);
        Assert.Equal("Ship", grid.Name);

        MonitorRow faction = Assert.Single(reply.Lists[1].Rows);
        Assert.Equal(9L, faction.Id);
        Assert.Equal(2L, faction.Grids);

        MonitorRow cluster = Assert.Single(reply.Lists[2].Rows);
        Assert.Equal(3L, cluster.Grids);
        Assert.Equal(1.5d, cluster.SizeKm);
        Assert.Equal(7L, Assert.Single(cluster.Members).Id);
    }

    [Fact]
    public async Task A_run_answers_with_every_call_timed()
    {
        MonitorReply reply = await AskAsync(Run(MonitorList.Grids));

        Assert.Equal(MonitorReplyState.Finished, reply.State);
        Assert.Equal(MonitorAccuracy.EveryCall, reply.Accuracy);
        Assert.Equal(0.9d, reply.Frame!.SimSpeed, 6);
        Assert.Equal("a test", reply.Run!.RequestedBy);
        Assert.NotEmpty(reply.SlowCalls);

        MonitorRow row = Assert.Single(Assert.Single(reply.Lists).Rows);
        Assert.Equal(new MonitorRow("Big Ship", 7L, 3d, 1d, 9d, 12d, 1d) { Thread = MonitorThread.Mixed }, row);
    }

    [Fact]
    public async Task A_type_only_picks_what_comes_back()
    {
        MonitorReply reply = await AskAsync(Run(MonitorList.BlockTypes));

        Assert.Equal(MonitorList.BlockTypes, Assert.Single(reply.Lists).List);
    }

    [Fact]
    public async Task The_saved_run_keeps_every_list_whichever_was_asked_for()
    {
        await AskAsync(Run(MonitorList.BlockTypes));

        MonitorReply saved = await AskAsync(Saved(MonitorList.Grids));

        Assert.Single(Assert.Single(saved.Lists).Rows);
    }

    [Fact]
    public async Task No_lists_means_all_of_them()
    {
        MonitorReply reply = await AskAsync(Run());

        Assert.Equal(Enum.GetValues<MonitorList>().Length, reply.Lists.Count);
    }

    [Fact]
    public async Task A_type_with_nothing_in_it_is_available_and_empty()
    {
        MonitorReply reply = await AskAsync(Run(MonitorList.Scripts));

        MonitorListReply list = Assert.Single(reply.Lists);
        Assert.True(list.Available);
        Assert.Empty(list.Rows);
    }

    [Fact]
    public async Task A_type_id_doesnt_come_back_because_it_wont_last()
    {
        MonitorReply reply = await AskAsync(Run(MonitorList.BlockTypes));

        Assert.Null(Assert.Single(Assert.Single(reply.Lists).Rows).Id);
    }

    [Theory]
    [InlineData(new MonitorList[0], true)]
    [InlineData(new[] { MonitorList.Clusters }, true)]
    [InlineData(new[] { MonitorList.Grids }, false)]
    public async Task Clusters_only_run_when_theyre_wanted(MonitorList[] lists, bool clusters)
    {
        await AskAsync(Run(lists));

        Assert.Equal(clusters, Assert.Single(_servers.Started).Clusters);
    }

    [Fact]
    public async Task Clusters_that_werent_timed_say_why_and_the_rest_still_arrive()
    {
        _servers.Result.ClusterProblem = "another plugin patches physics";

        MonitorReply reply = await AskAsync(Run(MonitorList.Clusters, MonitorList.Grids));

        MonitorListReply clusters = reply.Lists.Single(list => list.List == MonitorList.Clusters);
        Assert.False(clusters.Available);
        Assert.Equal("another plugin patches physics", clusters.Why);
        Assert.NotEmpty(reply.Lists.Single(list => list.List == MonitorList.Grids).Rows);
    }

    [Fact]
    public async Task A_clusters_time_is_marked_as_not_per_frame()
    {
        _servers.Result.Rows.Add(new RunRow { List = RunLists.Clusters, Name = "Cluster 1", MainMsPerFrame = 8d });

        MonitorReply reply = await AskAsync(Run(MonitorList.Clusters));

        Assert.False(Assert.Single(reply.Lists).PerFrame);
    }

    [Fact]
    public async Task History_gives_the_same_shape_marked_as_background()
    {
        History2Sweeps();

        MonitorReply run = await AskAsync(Run(MonitorList.Grids));
        MonitorReply history = await AskAsync(FromHistory(MonitorList.Grids));

        Assert.Equal(run.State, history.State);
        Assert.Equal(MonitorAccuracy.Background, history.Accuracy);
        Assert.NotNull(history.Frame);
        Assert.Null(history.Run);
        Assert.NotEmpty(history.Timeline);
        Assert.NotEmpty(Assert.Single(history.Lists).Rows);
    }

    [Fact]
    public async Task A_row_in_some_sweeps_says_so_and_isnt_averaged_down()
    {
        History2Sweeps();

        MonitorReply reply = await AskAsync(FromHistory(MonitorList.Grids));

        MonitorRow row = Assert.Single(reply.Lists).Rows.Single(rows => rows.Name == "Grid 2");
        Assert.Equal(6d, row.MainMs, 3);
        Assert.Equal(0.5d, row.SeenShare, 6);
    }

    [Fact]
    public async Task A_row_in_every_sweep_is_their_average()
    {
        History2Sweeps();

        MonitorReply reply = await AskAsync(FromHistory(MonitorList.Grids));

        MonitorRow row = Assert.Single(reply.Lists).Rows.Single(rows => rows.Name == "Grid 1");
        Assert.Equal(3d, row.MainMs, 3);
        Assert.Equal(1d, row.SeenShare, 6);
        Assert.Equal(1L, row.Id);
    }

    [Fact]
    public async Task What_history_doesnt_keep_says_so()
    {
        MonitorListReply list = Assert.Single((await AskAsync(FromHistory(MonitorList.Clusters))).Lists);

        Assert.False(list.Available);
        Assert.NotNull(list.Why);
    }

    [Fact]
    public async Task History_keeps_mods()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, GroupFirst = 0L, GroupHead = 1L };

        batch.Groups.Add(new HistoryGroup
        {
            UnitsSinceStart = TimeSpan.FromSeconds(10).Ticks,
            Dimension = HistoryDimension.Mods,
            Key = 5L,
            MainUnits = 30000U,
            CallsPerFrame = 1U,
        });

        _store.Add(History, batch, (_, _) => "Mod: One");

        MonitorListReply list = Assert.Single((await AskAsync(FromHistory(MonitorList.Mods))).Lists);

        Assert.True(list.Available);
        Assert.Equal("Mod: One", Assert.Single(list.Rows).Name);
        Assert.Equal(3d, list.Rows[0].MainMs, 3);
    }

    [Fact]
    public async Task History_keeps_rpcs()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, GroupFirst = 0L, GroupHead = 1L };

        batch.Groups.Add(new HistoryGroup
        {
            UnitsSinceStart = TimeSpan.FromSeconds(10).Ticks,
            Dimension = HistoryDimension.Rpcs,
            Key = 5L,
            MainUnits = 20000U,
            CallsPerFrame = 1U,
        });

        _store.Add(History, batch, (_, _) => "MyCubeGrid.OnSomething");

        MonitorListReply list = Assert.Single((await AskAsync(FromHistory(MonitorList.Rpcs))).Lists);

        Assert.True(list.Available);
        Assert.Equal("MyCubeGrid.OnSomething", Assert.Single(list.Rows).Name);
    }

    [Fact]
    public async Task History_ranks_rpcs_sent_by_bytes()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, GroupFirst = 0L, GroupHead = 2L };

        void Add(long key, uint bytes) => batch.Groups.Add(new HistoryGroup
        {
            UnitsSinceStart = TimeSpan.FromSeconds(10).Ticks,
            Dimension = HistoryDimension.RpcsSent,
            Key = key,
            BytesPerSecond = bytes,
            CallsPerFrame = 0.5f,
        });

        Add(1L, 100U);
        Add(2L, 900U);

        _store.Add(History, batch, (_, key) => "Rpc " + key);

        MonitorListReply list = Assert.Single((await AskAsync(FromHistory(MonitorList.RpcsSent))).Lists);

        Assert.Equal("Rpc 2", list.Rows[0].Name);
        Assert.Equal(900d, list.Rows[0].BytesPerSecond, 3);
        Assert.Equal(0.5d, list.Rows[0].CallsPerFrame, 3);
    }

    [Fact]
    public void The_latest_figures_say_whats_crossing_the_network()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, RollupFirst = 0L, RollupHead = 1L };
        var scopes = new uint[43];
        scopes[HistoryScopes.Frame] = 600000U;

        batch.Rollups.Add(new HistoryRollup
        {
            UnitsSinceStart = TimeSpan.FromSeconds(10).Ticks,
            Frames = 60U,
            SimulatedFrames = 60U,
            WindowUnits = 10_000_000U,
            SentKb = 200U,
            ReceivedKb = 40U,
            RpcsIn = 12U,
            ScopeUnits = scopes,
        });

        batch.Census.Add(new HistoryCensus { UnitsSinceStart = TimeSpan.FromSeconds(10).Ticks, Blocks = 500U, ConcealedGrids = 2U, ConcealedBlocks = 100U });
        batch.CensusHead = 1L;

        _store.Add(History, batch, (_, key) => "Grid " + key);

        MonitorLatest latest = _api.Latest(History)!;

        Assert.Equal(new MonitorNetwork(200d, 40d, 12d), latest.Network);
        Assert.Equal(100L, latest.World!.ConcealedBlocks);
        Assert.Equal(2L, latest.World.ConcealedGrids);
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        MonitorReply reply = await AskAsync(FromHistory() with { To = _started.AddMinutes(-1) });

        Assert.Equal(MonitorReplyState.Refused, reply.State);
    }

    [Fact]
    public async Task A_saved_run_comes_back_the_same_as_when_it_ran()
    {
        MonitorReply run = await AskAsync(Run(MonitorList.Grids));
        MonitorReply saved = await AskAsync(Saved(MonitorList.Grids) with { RunId = run.Run!.RunId });

        Assert.Equal(MonitorSource.SavedRun, saved.Source);
        Assert.Equal(MonitorAccuracy.EveryCall, saved.Accuracy);
        Assert.Equal(run.Lists[0].Rows, saved.Lists[0].Rows);
        Assert.Equal("a test", saved.Run!.RequestedBy);
    }

    [Fact]
    public async Task No_saved_run_is_a_refusal()
    {
        Assert.Equal(MonitorReplyState.Refused, (await AskAsync(Saved())).State);
    }

    [Fact]
    public async Task Two_requests_share_one_run()
    {
        _servers.Hold = new TaskCompletionSource();

        Task<MonitorReply> first = AskAsync(Run(MonitorList.Grids));
        await UntilStartedAsync(1);
        Task<MonitorReply> second = AskAsync(Run(MonitorList.BlockTypes));

        _servers.Hold.SetResult();

        Assert.Equal(MonitorList.Grids, Assert.Single((await first).Lists).List);
        Assert.Equal(MonitorList.BlockTypes, Assert.Single((await second).Lists).List);
        Assert.Equal(1, _servers.Starts);
    }

    [Fact]
    public async Task A_run_on_one_grid_cant_be_shared()
    {
        _servers.Hold = new TaskCompletionSource();

        Task<MonitorReply> first = AskAsync(Run() with { GridId = 5L });
        await UntilStartedAsync(1);

        MonitorReply second = await AskAsync(Run(MonitorList.Grids));

        Assert.Equal(MonitorReplyState.Refused, second.State);
        Assert.NotNull(second.RetryAfter);

        _servers.Hold.SetResult();
        await first;
    }

    [Fact]
    public async Task A_run_without_clusters_cant_serve_a_request_for_them()
    {
        _servers.Hold = new TaskCompletionSource();

        Task<MonitorReply> first = AskAsync(Run(MonitorList.Grids));
        await UntilStartedAsync(1);

        Assert.Equal(MonitorReplyState.Refused, (await AskAsync(Run(MonitorList.Clusters))).State);

        _servers.Hold.SetResult();
        await first;
    }

    [Fact]
    public async Task Plugins_cant_keep_asking()
    {
        await AskAsync(Run(MonitorList.Grids));

        MonitorReply again = await AskAsync(Run(MonitorList.Grids));

        Assert.Equal(MonitorReplyState.Refused, again.State);
        Assert.True(again.RetryAfter > TimeSpan.Zero);
        Assert.Equal(1, _servers.Starts);
    }

    [Fact]
    public async Task A_lag_trip_counts_as_a_run()
    {
        _runs.NoteTriggeredRun(History, TimeSpan.FromSeconds(5));

        Assert.Equal(MonitorReplyState.Refused, (await AskAsync(Run(MonitorList.Grids))).State);
    }

    [Fact]
    public async Task A_server_that_isnt_there_is_a_refusal()
    {
        _servers.Servers.Clear();

        Assert.Equal(MonitorReplyState.Refused, (await AskAsync(Run(MonitorList.Grids))).State);
    }

    [Fact]
    public async Task A_run_has_to_say_who_asked()
    {
        Assert.Equal(MonitorReplyState.Refused, (await AskAsync(Run(MonitorList.Grids) with { RequestedBy = " " })).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public async Task Run_lengths_have_limits(int seconds)
    {
        Assert.Equal(MonitorReplyState.Refused, (await AskAsync(Run(MonitorList.Grids) with { Seconds = seconds })).State);
    }

    [Fact]
    public async Task A_worker_saying_no_is_a_refusal()
    {
        _servers.Refuse = "The run didn't start: Paused.";

        MonitorReply reply = await AskAsync(Run(MonitorList.Grids));

        Assert.Equal(MonitorReplyState.Refused, reply.State);
        Assert.Equal(_servers.Refuse, reply.Message);
    }

    [Fact]
    public async Task A_failed_run_is_a_failure()
    {
        _servers.Result = new RunResultResponse { State = RunState.Failed };

        Assert.Equal(MonitorReplyState.Failed, (await AskAsync(Run(MonitorList.Grids))).State);
    }

    [Fact]
    public async Task Cancelling_stops_the_wait_and_the_run_is_still_saved()
    {
        _servers.Hold = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();

        Task<MonitorReply> asked = _api.RequestAsync(Run(MonitorList.Grids), cancel.Token);
        await UntilStartedAsync(1);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked.WaitAsync(s_wait));

        _servers.Hold.SetResult();

        for (int i = 0; i < 2000 && _runs.Running(History); i++)
            await Task.Delay(5);

        Assert.Single(_runs.List(History));
    }

    [Fact]
    public void A_server_is_named_by_what_outlasts_a_replaced_node()
    {
        Assert.Equal(History, Assert.Single(_api.Servers()).Id);
    }

    [Fact]
    public void A_server_thats_gone_is_still_listed_while_theres_data()
    {
        _store.NoteServer(s_server);
        _servers.Servers.Clear();

        MonitorServerInfo server = Assert.Single(_api.Servers());

        Assert.Equal(History, server.Id);
        Assert.False(server.Connected);
    }

    [Fact]
    public void A_connected_server_says_so()
    {
        _store.NoteServer(s_server);

        Assert.True(Assert.Single(_api.Servers()).Connected);
    }

    [Fact]
    public async Task A_server_thats_gone_still_answers_from_history()
    {
        History2Sweeps();
        _servers.Servers.Clear();

        Assert.Equal(MonitorReplyState.Finished, (await AskAsync(FromHistory(MonitorList.Grids))).State);
    }

    [Fact]
    public void Servers_are_remembered_after_a_Quasar_restart()
    {
        _store.NoteServer(s_server);

        var reopened = new HistoryStore(_directory, new MonitorSettings(_directory), NullLogger<HistoryStore>.Instance);

        Assert.Equal("One", Assert.Single(reopened.KnownServers()).Name);
    }

    [Fact]
    public void A_server_gone_longer_than_history_is_kept_is_forgotten()
    {
        _store.NoteServer(s_server);
        string file = Directory.GetFiles(_directory, "server.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new KnownServer(History, "One", string.Empty, DateTime.UtcNow.AddDays(-30))));

        var reopened = new HistoryStore(_directory, new MonitorSettings(_directory), NullLogger<HistoryStore>.Instance);
        reopened.PruneQuietServers();

        Assert.Empty(reopened.KnownServers());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Nothing_collected_yet_has_no_latest()
    {
        Assert.Null(_api.Latest(History));
    }

    [Fact]
    public void Latest_names_the_parts_of_the_frame()
    {
        History2Sweeps();

        MonitorLatest latest = _api.Latest(History)!;

        Assert.Equal(1d, latest.Frame.SimSpeed, 3);
        Assert.Contains(FrameParts.IdleName, latest.PartsMs.Keys);
    }

    [Fact]
    public async Task A_run_finishing_tells_subscribers()
    {
        var heard = new TaskCompletionSource<MonitorRunFinished>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.RunFinished += heard.SetResult;

        MonitorReply reply = await AskAsync(Run(MonitorList.Grids));

        Assert.Equal(new MonitorRunFinished(History, reply.Run!.RunId!), await heard.Task.WaitAsync(s_wait));
    }

    [Fact]
    public async Task An_event_tells_subscribers()
    {
        var heard = new TaskCompletionSource<MonitorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.EventRecorded += heard.SetResult;

        _store.ServerStarted(History, _started.Ticks, [], []);

        Assert.Equal(MonitorEventKind.Restart, (await heard.Task.WaitAsync(s_wait)).Kind);
    }

    [Fact]
    public async Task A_broken_subscriber_doesnt_stop_the_next()
    {
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.EventRecorded += _ => throw new InvalidOperationException("A broken plugin.");
        _api.EventRecorded += _ => heard.TrySetResult();

        _store.ServerStarted(History, _started.Ticks, [], []);

        await heard.Task.WaitAsync(s_wait);
    }

    [Fact]
    public async Task A_slow_subscriber_doesnt_hold_up_collection()
    {
        using var release = new ManualResetEventSlim();
        _api.EventRecorded += _ => release.Wait(s_wait);

        try
        {
            await Task.Run(() =>
            {
                _store.ServerStarted(History, _started.Ticks, [], []);
                _store.ServerStarted(History, _started.AddMinutes(1).Ticks, [], []);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void The_api_assembly_needs_nothing_of_Monitors()
    {
        IEnumerable<string> references = typeof(IMonitorApi).Assembly.GetReferencedAssemblies().Select(name => name.Name!);

        Assert.DoesNotContain(references, name => name.StartsWith("SenX", StringComparison.Ordinal) || name.StartsWith("Quasar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_chart_image_is_a_png_of_real_history()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, RollupFirst = 0L, RollupHead = 60L };

        for (int second = 0; second < 60; second++)
        {
            var scopes = new uint[43];
            scopes[HistoryScopes.Frame] = 6000000U;
            batch.Rollups.Add(new HistoryRollup { UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks, Frames = 60U, SimulatedFrames = 60U, WindowUnits = 10_000_000U, ScopeUnits = scopes });
        }

        _store.Add(History, batch, (_, key) => "Row " + key);

        DateTime to = DateTime.UtcNow;
        byte[]? image = await _api.For(History).ChartImageAsync(MonitorChart.SimSpeed, to.AddMinutes(-20), to, 640, 320, CancellationToken.None);

        Assert.NotNull(image);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, image.Take(4).ToArray());
    }

    [Fact]
    public async Task A_chart_image_with_nothing_to_draw_is_null()
    {
        DateTime to = DateTime.UtcNow;

        Assert.Null(await _api.For(History).ChartImageAsync(MonitorChart.SimSpeed, to.AddMinutes(-20), to, 640, 320, CancellationToken.None));
        Assert.Null(await _api.For(History).ChartImageAsync(MonitorChart.SimSpeed, to, to.AddMinutes(-20), 640, 320, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_chart_image_stops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        DateTime to = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _api.For(History).ChartImageAsync(MonitorChart.SimSpeed, to.AddMinutes(-20), to, 640, 320, cancelled.Token));

        // The gate was released, so the next one still answers.
        Assert.Null(await _api.For(History).ChartImageAsync(MonitorChart.SimSpeed, to.AddMinutes(-20), to, 640, 320, CancellationToken.None));
    }

    [Fact]
    public async Task Many_chart_images_at_once_all_answer()
    {
        DateTime to = DateTime.UtcNow;
        IMonitorTools tools = _api.For(History);

        await Task.WhenAll(Enum.GetValues<MonitorChart>().Select(chart => tools.ChartImageAsync(chart, to.AddMinutes(-20), to, 640, 320, CancellationToken.None)));
    }

    [Fact]
    public void The_public_surface_only_changes_on_purpose()
    {
        string approved = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PublicApi.approved.txt"));

        Assert.Equal(approved.ReplaceLineEndings("\n").Trim(), PublicSurface.Describe(typeof(IMonitorApi).Assembly));
    }
}

/// <summary>Lists everything public in an assembly, one line each, in a fixed order.</summary>
internal static class PublicSurface
{
    public static string Describe(Assembly assembly)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        return string.Join('\n', assembly.GetExportedTypes()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .SelectMany(type => new[] { type.FullName! }.Concat(type.GetMembers(Declared)
                .Select(member => "  " + member)
                .Order(StringComparer.Ordinal))));
    }
}
