using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SenX.Monitor.Api;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class ThreadJumpTests
{
    private const long Grid = 7001L;

    public ThreadJumpTests()
    {
        TestSetup.Reset();
    }

    // One background total with these grids in it, as main and worker units per frame.
    private static void Total(params (long Key, long Main, long Worker)[] grids)
    {
        CaptureResult result = Capture.SharedResult();
        result.Reset();
        result.SimulatedFrames = 1L;

        foreach ((long key, long main, long worker) in grids)
            result.Grids.Add(key, main, worker, 1L, 0U, key);

        ThreadJumps.Update(result);
    }

    private static void OnMain(long key = Grid) => Total((key, 100L, 0L));

    private static void OnWorkers(long key = Grid) => Total((key, 0L, 100L));

    [Theory]
    [InlineData(0L, 0L, ThreadUse.None)]
    [InlineData(100L, 0L, ThreadUse.Main)]
    [InlineData(951L, 49L, ThreadUse.Main)] // Workers have 4.9%, which doesn't count
    [InlineData(95L, 5L, ThreadUse.Mixed)] // Exactly 5% each way counts
    [InlineData(5L, 95L, ThreadUse.Mixed)]
    [InlineData(49L, 951L, ThreadUse.Worker)]
    [InlineData(0L, 100L, ThreadUse.Worker)]
    [InlineData(50L, 50L, ThreadUse.Mixed)]
    public void Which_threads_something_is_on(long main, long worker, ThreadUse expected)
    {
        Assert.Equal(expected, ThreadUses.Of(main, worker));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(100L, 0L)]
    [InlineData(951L, 49L)]
    [InlineData(950L, 50L)]
    [InlineData(50L, 950L)]
    [InlineData(49L, 951L)]
    [InlineData(1L, 1L)]
    [InlineData(0L, 7L)]
    public void The_worker_and_the_public_api_agree(long main, long worker)
    {
        Assert.Equal((int)ThreadUses.Of(main, worker), (int)MonitorThreads.Of(main / 10000d, worker / 10000d));
    }

    [Fact]
    public void Something_new_doesnt_jump()
    {
        OnWorkers();

        Assert.Equal(0, ThreadJumps.Count);
    }

    [Fact]
    public void A_change_has_to_hold_for_two_totals()
    {
        OnMain();
        OnMain();

        OnWorkers();
        Assert.Equal(0, ThreadJumps.Count);

        OnWorkers();
        Assert.Equal(1, ThreadJumps.Count);
        ThreadJump jump = ThreadJumps.At(0);
        Assert.Equal(Grid, jump.Key);
        Assert.Equal(GroupDimension.Grids, jump.Dimension);
        Assert.Equal(ThreadUse.Main, jump.From);
        Assert.Equal(ThreadUse.Worker, jump.To);
        Assert.Equal(100L, jump.WorkerUnits);

        // Once settled, staying there isn't another jump.
        OnWorkers();
        Assert.Equal(0, ThreadJumps.Count);
    }

    [Fact]
    public void A_row_near_the_line_doesnt_flap()
    {
        OnMain();

        for (int i = 0; i < 6; i++)
        {
            Total((Grid, 90L, 10L));
            Assert.Equal(0, ThreadJumps.Count);
            Total((Grid, 99L, 1L));
            Assert.Equal(0, ThreadJumps.Count);
        }
    }

    [Fact]
    public void A_total_with_too_little_time_breaks_the_run()
    {
        OnMain();
        OnWorkers();
        Total((Grid, 0L, 5L));
        OnWorkers();
        Assert.Equal(0, ThreadJumps.Count);

        OnWorkers();
        Assert.Equal(1, ThreadJumps.Count);
    }

    [Fact]
    public void Something_first_seen_with_too_little_time_settles_without_a_jump()
    {
        Total((Grid, 0L, 5L));
        OnWorkers();
        OnWorkers();
        Assert.Equal(0, ThreadJumps.Count);

        OnMain();
        OnMain();
        Assert.Equal(ThreadUse.Worker, ThreadJumps.At(0).From);
        Assert.Equal(ThreadUse.Main, ThreadJumps.At(0).To);
    }

    [Fact]
    public void The_row_for_jumps_not_listed_says_how_many()
    {
        var notListed = new GroupRecord { Dimension = GroupDimension.ThreadJumps, Key = RollupTable.NoKey, MaxUnits = 7U };
        var jump = new GroupRecord { Dimension = GroupDimension.ThreadJumps, Key = Grid, Blocks = (uint)GroupDimension.Grids, MaxUnits = 7U };
        var grid = new GroupRecord { Dimension = GroupDimension.Grids, Key = Grid, MaxUnits = 7U };

        Assert.Equal(7U, notListed.JumpsNotListed);
        Assert.Equal(0U, jump.JumpsNotListed);
        Assert.Equal(0U, grid.JumpsNotListed);
    }

    [Fact]
    public void Too_little_time_says_nothing()
    {
        OnMain();
        Total((Grid, 0L, 5L));
        Total((Grid, 0L, 5L));
        Assert.Equal(0, ThreadJumps.Count);

        OnWorkers();
        OnWorkers();
        Assert.Equal(1, ThreadJumps.Count);
    }

    [Fact]
    public void Something_gone_is_forgotten()
    {
        OnMain();
        OnMain(Grid + 1L);

        OnWorkers();
        OnWorkers();

        Assert.Equal(0, ThreadJumps.Count);
    }

    [Fact]
    public void The_costliest_jumps_are_kept()
    {
        int grids = ThreadJumps.MostPerSweep + 5;
        (long, long, long)[] onMain = Enumerable.Range(1, grids).Select(i => ((long)i, (long)i * 100L, 0L)).ToArray();
        (long, long, long)[] onWorkers = Enumerable.Range(1, grids).Select(i => ((long)i, 0L, (long)i * 100L)).ToArray();

        Total(onMain);
        Total(onWorkers);
        Total(onWorkers);

        Assert.Equal(ThreadJumps.MostPerSweep, ThreadJumps.Count);
        Assert.Equal(grids, ThreadJumps.Total);

        long[] kept = Enumerable.Range(0, ThreadJumps.Count).Select(i => ThreadJumps.At(i).Key).ToArray();
        Assert.Equal(Enumerable.Range(6, ThreadJumps.MostPerSweep).Select(i => (long)i).Reverse(), kept);
    }

    [Fact]
    public void Owners_and_factions_are_sums_and_dont_jump()
    {
        Assert.DoesNotContain(GroupDimension.Owners, ThreadJumps.Followed);
        Assert.DoesNotContain(GroupDimension.Factions, ThreadJumps.Followed);
        Assert.DoesNotContain(GroupDimension.RpcsSent, ThreadJumps.Followed);
    }

    [Fact]
    public void The_tree_says_where_the_game_thread_waits()
    {
        Assert.Equal(Scope.TaskWait, TreeShape.TaskWait);
        Assert.Equal(Scope.BeforeParallel, TreeShape.BeforeParallel);
        Assert.Equal(Scope.AfterParallel, TreeShape.AfterParallel);
        Assert.Equal(Scope.PhysicsStep, TreeShape.PhysicsStep);
        Assert.Equal("Main, waits on workers", TreeShape.ThreadOf(Scope.BeforeParallel));
        Assert.Equal("Main", TreeShape.ThreadOf(Scope.Gps));
    }

    [Theory]
    [InlineData(0L, 0L, 0L, 0L, 0L, 0L)]
    [InlineData(20L, 0L, 0L, 0L, 0L, 20L)] // The task wait is all waiting
    [InlineData(0L, 100L, 30L, 0L, 0L, 70L)] // The game thread worked 30 of its 100 in the parallel update
    [InlineData(0L, 100L, 30L, 50L, 50L, 70L)] // It worked all of the second one
    [InlineData(0L, 10L, 30L, 0L, 0L, 0L)] // Its own work can't make waiting negative
    [InlineData(5L, 100L, 30L, 40L, 10L, 105L)]
    public void A_runs_main_wait_is_the_parallel_time_the_game_thread_didnt_work(long taskWait, long before, long beforeOwnWork, long after, long afterOwnWork, long expected)
    {
        Assert.Equal(expected, CaptureResult.MainWait(taskWait, before, beforeOwnWork, after, afterOwnWork));
    }

    private static void StartDetail()
    {
        Calibration.Set(readBiasNanos: 0d, timedCallNanos: 50d);
        Background.Inline = true;
        FrameEngine.Initialize();
        FrameEngine.Start();
        DetailController.TurnFrames = 10;
        DetailController.ResetCounters();
        DetailController.Configure(SiteGroups.All.SelectMany(group => group.Sites).Distinct().ToArray());
        DetailController.SetMode(DetailMode.Everything);
    }

    private static List<GroupRecord> JumpRowsSince(long from)
    {
        GroupRecord row = default;
        var jumps = new List<GroupRecord>();

        for (long sequence = from; sequence < Sweep.WholeRows; sequence++)
        {
            Assert.True(Sweep.Rows.TryCopy(sequence, (byte*)&row));
            if (row.Dimension == GroupDimension.ThreadJumps)
                jumps.Add(row);
        }

        return jumps;
    }

    [Fact]
    public void A_total_says_how_many_jumps_it_didnt_list()
    {
        const int Grids = ThreadJumps.MostPerSweep + 5;
        StartDetail();

        Sweep1(onWorker: false, grids: Grids);
        Sweep1(onWorker: true, grids: Grids);
        long from = Sweep.Rows.Head;
        Sweep1(onWorker: true, grids: Grids);

        List<GroupRecord> jumps = JumpRowsSince(from);
        GroupRecord notListed = Assert.Single(jumps, jump => jump.JumpsNotListed > 0U);
        Assert.Equal(ThreadJumps.MostPerSweep, jumps.Count - 1);
        Assert.Equal((uint)(ThreadJumps.Total - ThreadJumps.Count), notListed.JumpsNotListed);
        Assert.True(ThreadJumps.Total >= Grids);
    }

    [Fact]
    public void A_jump_rides_in_the_background_total()
    {
        StartDetail();

        Sweep1(onWorker: false);
        Sweep1(onWorker: true);
        long from = Sweep.Rows.Head;
        Sweep1(onWorker: true);

        List<GroupRecord> jumps = JumpRowsSince(from);
        Assert.DoesNotContain(jumps, jump => jump.JumpsNotListed > 0U);

        GroupRecord gridJump = Assert.Single(jumps, jump => jump.JumpedList == GroupDimension.Grids);
        Assert.Equal(Grid, gridJump.Key);
        Assert.Equal(ThreadUse.Main, gridJump.JumpFrom);
        Assert.Equal(ThreadUse.Worker, gridJump.JumpTo);
        Assert.True(gridJump.WorkerUnits > 0U);
        Assert.Equal(0U, gridJump.MainUnits);
    }

    // One background total with the test grids' time on the game thread or a worker.
    private static void Sweep1(bool onWorker, int grids = 1)
    {
        // One worker thread for the whole total, because tests have only a few worker blocks.
        if (onWorker)
        {
            var worker = new Thread(() =>
            {
                for (int i = 0; i < grids; i++)
                    AddBlock(Site.ParallelBefore, 100L * DetailController.TurnFrames, i);
            });
            worker.Start();
            worker.Join();
        }

        for (int frame = 0; frame < DetailController.TurnFrames; frame++)
        {
            for (int i = 0; !onWorker && i < grids; i++)
                AddBlock(Site.EntityBefore, 100L, i);

            Sweep.OnFrame(Clock.Now(), true, false);
            DetailController.OnFrame(true, false, 1000L);
        }

        Sweep.MakeDue();
        Sweep.OnFrame(Clock.Now(), false, false);

        for (int i = 0; i < 16 && Sweep.Busy; i++)
            Sweep.OnFrame(Clock.Now(), true, false);

        Assert.False(Sweep.Busy);
    }

    private static void AddBlock(int site, long units, int index = 0)
    {
        long raw = (long)Math.Ceiling(units * (double)Clock.Frequency / Clock.UnitsPerSecond);
        EntitySlot* created = Recorder.AddEntity(site, Phase.Before, 1L + index, raw, 0);
        if (created == null)
            return;

        created->GridId = Grid + index;
        created->OwnerId = 501L;
        created->DefinitionKey = 0x11110000L;
    }
}
