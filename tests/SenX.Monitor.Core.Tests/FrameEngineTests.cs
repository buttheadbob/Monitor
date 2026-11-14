using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class FrameEngineTests
{
    public FrameEngineTests()
    {
        TestSetup.Reset();
        FrameEngine.Start();
        FrameEngine.SecondElapsed = null;

        // Other tests leave frames in the window, so start each test from an empty one.
        FrameEngine.CommitSecondNow();
    }

    private static long Raw(long units) => (long)(units * (double)Stopwatch.Frequency / Clock.UnitsPerSecond);

    private static FrameRecord LastFrame()
    {
        FrameRecord record = default;
        Assert.True(FrameEngine.Flight.TryCopy(FrameEngine.Flight.Head - 1, (byte*)&record));
        return record;
    }

    private static void SimulatedFrame(long componentUnits = 100)
    {
        long start = Clock.Now();
        TreeCounters.Add(Scope.Components, Raw(componentUnits));
        FrameEngine.OnFrameEnd(start);
    }

    private static void PausedFrame() => FrameEngine.OnFrameEnd(Clock.Now());

    [Fact]
    public void Frame_commits_scope_time_and_clears_the_counters()
    {
        TreeCounters.Add(Scope.Physics, Raw(500));
        SimulatedFrame();

        FrameRecord record = LastFrame();
        Assert.InRange(record.ScopeUnits[Scope.Physics], 499u, 501u);
        Assert.True((record.Flags & FrameFlags.Simulated) != 0);
        Assert.Equal(0L, TreeCounters.RawTicks[Scope.Physics]);
        Assert.Equal(0L, TreeCounters.Calls[Scope.Physics]);
    }

    [Fact]
    public void Frame_records_its_own_scope()
    {
        Thread.Sleep(2);
        long start = Clock.Now();
        Thread.Sleep(1);
        TreeCounters.Add(Scope.Components, 1L);
        FrameEngine.OnFrameEnd(start);

        FrameRecord record = LastFrame();
        Assert.True(record.BusyUnits > 0U);
        Assert.Equal(record.BusyUnits, record.ScopeUnits[Scope.Frame]);
    }

    [Fact]
    public void Monitor_cost_shows_up_in_the_next_frame()
    {
        SimulatedFrame();
        long afterFirst = TreeCounters.Calls[Scope.Monitor];
        Assert.Equal(1L, afterFirst);

        SimulatedFrame();
        Assert.Equal(1L, TreeCounters.Calls[Scope.Monitor]);
    }

    [Fact]
    public void Frames_without_simulation_are_not_counted_as_simulated()
    {
        PausedFrame();
        Assert.True((LastFrame().Flags & FrameFlags.Simulated) == 0);
    }

    [Fact]
    public void Sustained_frames_without_simulation_mean_paused()
    {
        SimulatedFrame();
        Assert.False(FrameEngine.Paused);

        for (int i = 0; i < 40; i++)
            PausedFrame();
        Assert.True(FrameEngine.Paused);

        SimulatedFrame();
        Assert.False(FrameEngine.Paused);
    }

    [Fact]
    public void Salt_changes_every_frame()
    {
        ulong before = Sampler.FrameSalt;
        SimulatedFrame();
        Assert.NotEqual(before, Sampler.FrameSalt);
    }

    [Fact]
    public void Second_row_sums_scopes_and_counts_frames()
    {
        for (int i = 0; i < 10; i++)
        {
            TreeCounters.Add(Scope.Physics, Raw(100));
            SimulatedFrame();
        }

        for (int i = 0; i < 5; i++)
            PausedFrame();

        FrameEngine.CommitSecondNow();

        Assert.True(FrameEngine.TryReadLatest(out RollupRecord row));
        Assert.Equal(15u, row.Frames);
        Assert.Equal(10u, row.SimulatedFrames);
        Assert.InRange(row.ScopeUnits[Scope.Physics], 990u, 1010u);
    }

    [Fact]
    public void Sim_speed_is_simulated_frames_against_sixty_per_second()
    {
        var row = new RollupRecord { SimulatedFrames = 45, WindowUnits = (uint)Clock.UnitsPerSecond };
        Assert.Equal(0.75, row.SimSpeed, 3);

        row.SimulatedFrames = 0;
        Assert.Equal(0d, row.SimSpeed);
    }

    [Fact]
    public void Paused_time_does_not_pretend_to_be_full_speed()
    {
        for (int i = 0; i < 60; i++)
            PausedFrame();

        long rows = FrameEngine.Rollups.Head;

        FrameEngine.CommitSecondNow();

        // A paused second leaves no row at all, which is what makes it a gap in history.
        Assert.Equal(rows, FrameEngine.Rollups.Head);

        // Other test classes start runs, which a paused engine would refuse.
        SimulatedFrame();
    }

    [Fact]
    public void Second_boundary_raises_the_callback_once()
    {
        int calls = 0;
        FrameEngine.SecondElapsed = () => calls++;

        SimulatedFrame();
        FrameEngine.CommitSecondNow();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Frame_commit_allocates_nothing()
    {
        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < 20_000; i++)
                SimulatedFrame();
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Second_commit_allocates_nothing()
    {
        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < 2_000; i++)
            {
                SimulatedFrame();
                FrameEngine.CommitSecondNow();
            }
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Every_scope_has_a_unique_id_and_a_known_parent()
    {
        var ids = new HashSet<int>();
        foreach (ScopeDefinition scope in ScopeTable.All)
        {
            Assert.True(ids.Add(scope.Id), $"Scope {scope.Id} appears twice.");
            Assert.InRange(scope.Id, 1, Scope.Count - 1);
        }

        foreach (ScopeDefinition scope in ScopeTable.All)
            Assert.True(scope.Parent == Scope.None || ids.Contains(scope.Parent), $"{scope.Name} has an unknown parent.");

        Assert.Equal(Scope.Count - 1, ScopeTable.All.Length);
    }

    [Fact]
    public void Sites_sit_above_the_tree_and_inside_the_gates() =>
        Assert.True(Site.First >= Scope.Count && Site.Last < Gates.Capacity);

    [Fact]
    public void A_failure_in_the_frame_hook_stays_out_of_the_game()
    {
        TestSetup.Reset();
        FrameEngine.Initialize();
        FrameEngine.Start();
        FrameEngine.SecondElapsed = () => throw new InvalidOperationException("Broken on purpose.");
        Gates.Open(Site.First, 0);

        try
        {
            // This runs inside the game's own frame, so anything that escapes would end the server.
            FrameEngine.OnFrameEnd(Clock.Now());
            Thread.Sleep(1100);
            FrameEngine.OnFrameEnd(Clock.Now());

            Assert.IsType<InvalidOperationException>(FrameEngine.TakeFault());
            Assert.Null(FrameEngine.TakeFault());
            Assert.Equal(Gates.Closed, Gates.Read(Site.First));

            ulong frame = FrameEngine.Frame;
            FrameEngine.OnFrameEnd(Clock.Now());
            Assert.Equal(frame, FrameEngine.Frame);
        }
        finally
        {
            FrameEngine.SecondElapsed = null;
            FrameEngine.Start();
        }
    }
}
