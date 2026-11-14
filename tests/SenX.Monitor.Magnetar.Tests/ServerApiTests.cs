using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using SenX.Monitor.Api;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Hooks;
using SenX.Monitor.Magnetar.Patching;
using SenX.Monitor.Magnetar.Reporting;
using VRage.Plugins;
using Xunit;

namespace SenX.Monitor.Magnetar.Tests;

public sealed class ServerApiTests
{
    public ServerApiTests()
    {
        MonitorCore.Initialize();
        FrameEngine.Start();
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddMinutes(-5).Ticks;
    }

    // A total with a grid that moved to workers, and the row saying three more jumps weren't listed.
    private static long WriteTotalWithJumps(long grid)
    {
        long since = FrameEngine.UnitsSinceStart(Clock.Now());

        Sweep.WriteForTests(
            new GroupRecord { UnitsSinceStart = since, Dimension = GroupDimension.Grids, Key = grid, Representative = grid, WorkerUnits = 500U },
            new GroupRecord
            {
                UnitsSinceStart = since,
                Dimension = GroupDimension.ThreadJumps,
                Key = grid,
                Representative = grid,
                WorkerUnits = 500U,
                Blocks = (uint)GroupDimension.Grids,
                Grids = ((uint)ThreadUse.Main << 8) | (uint)ThreadUse.Worker,
            },
            new GroupRecord { UnitsSinceStart = since, Dimension = GroupDimension.ThreadJumps, Key = RollupTable.NoKey, MaxUnits = 3U });

        return since;
    }

    [Fact]
    public void Reading_thread_jumps_never_asks_the_game_thread()
    {
        long since = WriteTotalWithJumps(9101L);
        int calls = ServerApi.GameThreadCalls;

        List<MonitorEvent> events = ServerApi.ReadAllEvents(since, since);

        Assert.Equal(calls, ServerApi.GameThreadCalls);
        Assert.Contains(events, row => row.Kind == MonitorEventKind.ThreadJump && row.Text == "Grid #9101 moved from the game thread to worker threads");
        Assert.Contains(events, row => row.Kind == MonitorEventKind.ThreadJump && row.Text == "3 more thread changes not listed");
        Assert.Equal(2, ServerApi.ReadJumps(since, since).Count);
    }

    [Fact]
    public void Thread_jumps_arent_a_list_of_costs()
    {
        long since = WriteTotalWithJumps(9102L);

        List<GroupRecord>? grids = LagReport.LatestSweep(GroupDimension.Grids, out long latest);

        Assert.Equal(since, latest);
        GroupRecord grid = Assert.Single(grids!);
        Assert.Equal(9102L, grid.Key);
        Assert.Equal(GroupDimension.Grids, grid.Dimension);
    }

    private static MonitorRequest Run() => new() { Source = MonitorSource.Run, Seconds = 1, RequestedBy = "a plugin" };

    private static Task<MonitorReply> AskAsync(MonitorRequest request) =>
        ServerApi.Instance.RequestAsync(request, CancellationToken.None);

    [Fact]
    public async Task A_chart_image_is_for_hosts_with_history()
    {
        DateTime to = DateTime.UtcNow;

        Assert.Null(await ServerApi.Instance.ChartImageAsync(MonitorChart.SimSpeed, to.AddMinutes(-5), to, 640, 320, CancellationToken.None));
    }

    [Fact]
    public async Task A_run_has_to_say_who_asked()
    {
        Assert.Equal(MonitorReplyState.Refused, (await AskAsync(Run() with { RequestedBy = "" })).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public async Task Run_lengths_have_limits(int seconds)
    {
        Assert.Equal(MonitorReplyState.Refused, (await AskAsync(Run() with { Seconds = seconds })).State);
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        var request = new MonitorRequest { Source = MonitorSource.History, From = DateTime.UtcNow, To = DateTime.UtcNow.AddMinutes(-1) };

        Assert.Equal(MonitorReplyState.Refused, (await AskAsync(request)).State);
    }

    [Fact]
    public async Task History_says_what_it_doesnt_keep()
    {
        var request = new MonitorRequest
        {
            Source = MonitorSource.History,
            Lists = new[] { MonitorList.Clusters },
            From = DateTime.UtcNow.AddMinutes(-10),
            To = DateTime.UtcNow,
        };

        MonitorReply reply = await AskAsync(request);

        Assert.Equal(MonitorAccuracy.Background, reply.Accuracy);
        Assert.False(Assert.Single(reply.Lists).Available);
    }

    [Fact]
    public async Task A_finished_run_can_be_asked_for_again()
    {
        ServerApi.Instance.OnRunFinished(77L, new RunResultResponse { State = RunState.Finished, SimSpeed = 0.8d });

        MonitorReply reply = await AskAsync(new MonitorRequest { Source = MonitorSource.SavedRun, RunId = "77" });

        Assert.Equal(MonitorReplyState.Finished, reply.State);
        Assert.Equal(0.8d, reply.Frame!.SimSpeed, 6);
    }

    [Fact]
    public async Task Only_the_latest_run_is_kept()
    {
        ServerApi.Instance.OnRunFinished(78L, new RunResultResponse { State = RunState.Finished });

        Assert.Equal(MonitorReplyState.Refused, (await AskAsync(new MonitorRequest { Source = MonitorSource.SavedRun, RunId = "1" })).State);
    }

    [Fact]
    public async Task Events_arrive_one_at_a_time_and_in_order()
    {
        var heard = new System.Collections.Generic.List<string>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0;
        bool overlapped = false;

        void Handler(MonitorRunFinished finished)
        {
            overlapped |= Interlocked.Increment(ref running) > 1;
            Thread.Sleep(5);
            heard.Add(finished.RunId);
            Interlocked.Decrement(ref running);

            if (finished.RunId == "204")
                done.TrySetResult(true);
        }

        ServerApi.Instance.RunFinished += Handler;

        try
        {
            for (long id = 200L; id <= 204L; id++)
                ServerApi.Instance.OnRunFinished(id, new RunResultResponse { State = RunState.Finished });

            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            ServerApi.Instance.RunFinished -= Handler;
        }

        Assert.False(overlapped);
        Assert.Equal(new[] { "200", "201", "202", "203", "204" }, heard);
    }

    [Fact]
    public async Task A_broken_handler_doesnt_stop_the_next()
    {
        var heard = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Broken(MonitorRunFinished finished) => throw new InvalidOperationException("A broken plugin.");

        void Next(MonitorRunFinished finished) => heard.TrySetResult(true);

        ServerApi.Instance.RunFinished += Broken;
        ServerApi.Instance.RunFinished += Next;

        try
        {
            ServerApi.Instance.OnRunFinished(300L, new RunResultResponse { State = RunState.Finished });

            Assert.Same(heard.Task, await Task.WhenAny(heard.Task, Task.Delay(TimeSpan.FromSeconds(10))));
        }
        finally
        {
            ServerApi.Instance.RunFinished -= Broken;
            ServerApi.Instance.RunFinished -= Next;
        }
    }

    [Fact]
    public async Task A_lag_trip_counts_towards_the_gap_between_plugin_runs()
    {
        ServerApi.Instance.NoteTriggeredRun(30);

        MonitorReply reply = await AskAsync(Run());

        Assert.Equal(MonitorReplyState.Refused, reply.State);
        Assert.True(reply.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void A_lag_trip_makes_player_tests_wait_too()
    {
        ServerApi.Instance.NoteTriggeredRun(30);

        Assert.True(ServerApi.Instance.UntilNextRun() > TimeSpan.Zero);
    }

    [Fact]
    public async Task Saved_runs_list_the_latest_one()
    {
        ServerApi.Instance.OnRunFinished(400L, new RunResultResponse { State = RunState.Finished, Seconds = 10d });

        MonitorSavedRun run = Assert.Single(await ServerApi.Instance.SavedRunsAsync(CancellationToken.None));

        Assert.Equal("400", run.Id);
    }

    [Fact]
    public void The_locator_gives_back_whatever_Monitor_last_registered()
    {
        MonitorServerApi.Register(null);
        Assert.Null(MonitorServerApi.Current);

        MonitorServerApi.Register(ServerApi.Instance);
        Assert.Same(ServerApi.Instance, MonitorServerApi.Current);

        MonitorServerApi.Register(null);
        Assert.Null(MonitorServerApi.Current);
    }
}

public sealed class FakePlugin : IPlugin
{
    public int Updates;

    public void Init(object gameInstance)
    {
    }

    public void Update() => Updates++;

    public void Dispose()
    {
    }
}

public sealed class PluginHooksTests : IDisposable
{
    private const string HarmonyId = "senx.monitor.tests.plugins";

    private readonly Harmony _harmony = new(HarmonyId);

    public PluginHooksTests() => MonitorCore.Initialize();

    public void Dispose() => SenX.Monitor.Magnetar.Patching.OwnPatches.Remove(_harmony);

    [Fact]
    public void A_plugins_update_gets_hooked()
    {
        Assert.Equal(1, PluginHooks.Apply(_harmony, new[] { typeof(FakePlugin) }));
    }

    [Fact]
    public void The_same_method_is_only_hooked_once()
    {
        Assert.Equal(1, PluginHooks.Apply(_harmony, new[] { typeof(FakePlugin), typeof(FakePlugin) }));
    }

    [Fact]
    public void A_hooked_update_still_runs()
    {
        PluginHooks.Apply(_harmony, new[] { typeof(FakePlugin) });

        var plugin = new FakePlugin();
        plugin.Update();

        Assert.Equal(1, plugin.Updates);
    }

    [Fact]
    public void A_type_that_isnt_a_plugin_only_loses_its_own_row()
    {
        Assert.Equal(1, PluginHooks.Apply(_harmony, new[] { typeof(string), typeof(FakePlugin) }));
    }
}
