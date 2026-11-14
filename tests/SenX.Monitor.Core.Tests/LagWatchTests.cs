using System.Collections.Generic;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class LagWatchTests
{
    private readonly List<(LagReason Reason, double Value)> _trips = new();
    private long _now;

    public LagWatchTests()
    {
        TestSetup.Reset();
        FrameEngine.Initialize();
        FrameEngine.Start();
        DetailController.Configure(new[] { Site.EntityBefore });
        Markers.Flush();

        // Well past any save an earlier test wrote, unless a test says otherwise.
        _now = Clock.Now() + (Clock.Frequency * 60L);
        LagWatch.Tripped = (reason, value) => _trips.Add((reason, value));
    }

    private void Second(uint frames = 60U, uint p95Ms = 10U, uint worstMs = 16U)
    {
        var row = new RollupRecord
        {
            Frames = frames,
            SimulatedFrames = frames,
            WindowUnits = (uint)Clock.UnitsPerSecond,
            FrameP95 = p95Ms * 10000U,
            FrameMax = worstMs * 10000U,
        };

        LagWatch.OnSecond(&row, _now);
        _now += Clock.Frequency;
    }

    private void SlowSeconds(int count)
    {
        for (int i = 0; i < count; i++)
            Second(frames: 30U);
    }

    [Fact]
    public void Sim_speed_staying_low_trips_it()
    {
        SlowSeconds(5);

        (LagReason reason, double value) = Assert.Single(_trips);
        Assert.Equal(LagReason.SimSpeed, reason);
        Assert.Equal(0.5d, value, 6);
    }

    [Fact]
    public void One_second_short_doesnt()
    {
        SlowSeconds(4);

        Assert.Empty(_trips);
    }

    [Fact]
    public void A_good_second_starts_the_count_again()
    {
        SlowSeconds(4);
        Second();
        SlowSeconds(4);

        Assert.Empty(_trips);
    }

    [Fact]
    public void Lag_that_carries_on_waits_for_the_cooldown()
    {
        LagWatch.Limits = new LagLimits { CooldownSeconds = 20 };

        SlowSeconds(19);
        Assert.Single(_trips);

        SlowSeconds(10);
        Assert.Equal(2, _trips.Count);
    }

    [Fact]
    public void Frame_time_is_off_until_its_set()
    {
        for (int i = 0; i < 10; i++)
            Second(p95Ms: 100U);

        Assert.Empty(_trips);
    }

    [Fact]
    public void Frame_time_trips_it_once_set()
    {
        LagWatch.Limits = new LagLimits { FrameP95AboveMs = 20d };

        for (int i = 0; i < 5; i++)
            Second(p95Ms: 25U);

        (LagReason reason, double value) = Assert.Single(_trips);
        Assert.Equal(LagReason.FrameTime, reason);
        Assert.Equal(25d, value, 6);
    }

    [Fact]
    public void A_spike_trips_it_straight_away()
    {
        Second(worstMs: 1500U);

        (LagReason reason, double value) = Assert.Single(_trips);
        Assert.Equal(LagReason.Spike, reason);
        Assert.Equal(1500d, value, 6);
    }

    [Fact]
    public void A_spike_of_zero_means_off()
    {
        LagWatch.Limits = new LagLimits { SpikeMs = 0d };

        Second(worstMs: 5000U);

        Assert.Empty(_trips);
    }

    [Fact]
    public void A_save_about_to_be_written_isnt_a_spike()
    {
        Markers.NoteSave(succeeded: true);

        Second(worstMs: 5000U);

        Assert.Empty(_trips);
        Markers.Flush();
    }

    [Fact]
    public void A_save_just_written_isnt_a_spike()
    {
        Markers.NoteSave(succeeded: true);
        Markers.Flush();
        _now = Clock.Now();

        Second(worstMs: 5000U);

        Assert.Empty(_trips);
    }

    [Fact]
    public void A_spike_long_after_a_save_counts()
    {
        Markers.NoteSave(succeeded: true);
        Markers.Flush();
        _now = Clock.Now() + (Clock.Frequency * 10L);

        Second(worstMs: 5000U);

        Assert.Single(_trips);
    }

    [Fact]
    public void A_spike_just_after_a_spawn_is_a_join_hitch_not_lag()
    {
        var hitches = new List<double>();
        LagWatch.JoinHitch = ms => hitches.Add(ms);
        Markers.NoteSpawn();
        _now = Clock.Now() + (Clock.Frequency * 6L);
        long marked = Markers.Rows.Head;

        Second(worstMs: 6000U);

        Assert.Empty(_trips);
        Assert.Equal(6000d, Assert.Single(hitches), 6);
        Assert.Equal(marked + 1, Markers.Rows.Head);
    }

    [Fact]
    public void A_join_hitch_leaves_the_cooldown_for_real_lag()
    {
        Markers.NoteSpawn();
        _now = Clock.Now() + (Clock.Frequency * 6L);
        Second(worstMs: 6000U);

        _now += Clock.Frequency * 20L;
        Second(worstMs: 1500U);

        (LagReason reason, _) = Assert.Single(_trips);
        Assert.Equal(LagReason.Spike, reason);
    }

    [Fact]
    public void A_spike_after_the_grace_is_lag_again()
    {
        LagWatch.Limits = new LagLimits { JoinGraceSeconds = 15 };
        Markers.NoteSpawn();
        _now = Clock.Now() + (Clock.Frequency * 16L);

        Second(worstMs: 6000U);

        Assert.Single(_trips);
    }

    [Fact]
    public void A_grace_of_zero_treats_a_join_spike_as_lag()
    {
        LagWatch.Limits = new LagLimits { JoinGraceSeconds = 0 };
        Markers.NoteSpawn();

        // Past an earlier test's save grace, and well inside the default join grace.
        _now = Clock.Now() + (Clock.Frequency * 5L);

        Second(worstMs: 6000U);

        Assert.Single(_trips);
    }

    [Fact]
    public void A_players_spawn_opens_the_grace()
    {
        Markers.NoteCharacterSpawn(npc: false, steamId: 76561198000000001UL, serialId: 0);
        _now = Clock.Now() + (Clock.Frequency * 6L);

        Second(worstMs: 6000U);

        Assert.Empty(_trips);
    }

    [Theory]
    [InlineData(true, 0UL, 0)] // An NPC identity, as MES and other mods spawn
    [InlineData(false, 0UL, 0)] // An identity with no player
    [InlineData(false, 90000000000000001UL, 3)] // One of the game's bots, on the server's Steam id
    [InlineData(true, 76561198000000001UL, 0)] // Marked NPC whatever else it has
    public void An_npc_spawn_doesnt_open_the_grace(bool npc, ulong steamId, int serialId)
    {
        Markers.NoteCharacterSpawn(npc, steamId, serialId);
        _now = Clock.Now() + (Clock.Frequency * 6L);

        Second(worstMs: 6000U);

        Assert.Equal(LagReason.Spike, Assert.Single(_trips).Reason);
    }

    [Fact]
    public void Only_game_thread_frames_can_trip_it()
    {
        LagWatch.Limits = new LagLimits { FrameP95AboveMs = 20d };

        // Network and worker figures share the row, and none of them is lag on its own.
        for (int i = 0; i < 30; i++)
        {
            var row = new RollupRecord
            {
                Frames = 60U,
                SimulatedFrames = 60U,
                WindowUnits = (uint)Clock.UnitsPerSecond,
                FrameP95 = 10U * 10000U,
                FrameMax = 16U * 10000U,
            };
            FillOtherThreads(&row);

            LagWatch.OnSecond(&row, _now);
            _now += Clock.Frequency;
        }

        Assert.Empty(_trips);
    }

    // Every other field set high: network, RPCs, worker-thread scope time, GC and the other percentiles.
    private static void FillOtherThreads(RollupRecord* row)
    {
        var bytes = (byte*)row;
        var frames = new RollupRecord
        {
            Frames = row->Frames,
            SimulatedFrames = row->SimulatedFrames,
            WindowUnits = row->WindowUnits,
            FrameP95 = row->FrameP95,
            FrameMax = row->FrameMax,
        };

        for (int i = 0; i < sizeof(RollupRecord); i++)
            bytes[i] = 0x7F;

        row->Frames = frames.Frames;
        row->SimulatedFrames = frames.SimulatedFrames;
        row->WindowUnits = frames.WindowUnits;
        row->FrameP95 = frames.FrameP95;
        row->FrameMax = frames.FrameMax;
    }

    [Fact]
    public void Low_sim_speed_after_a_spawn_is_still_lag()
    {
        Markers.NoteSpawn();
        _now = Clock.Now();

        SlowSeconds(5);

        Assert.Equal(LagReason.SimSpeed, Assert.Single(_trips).Reason);
    }

    [Fact]
    public void Switching_it_off_stops_it()
    {
        LagWatch.Limits = new LagLimits { Enabled = false };

        SlowSeconds(10);
        Second(worstMs: 5000U);

        Assert.Empty(_trips);
    }

    [Fact]
    public void Nothing_trips_before_detail_has_started()
    {
        DetailController.Configure(new int[0]);

        SlowSeconds(10);

        Assert.Empty(_trips);
    }

    [Fact]
    public void A_runs_own_cost_isnt_lag()
    {
        Assert.Equal(CaptureStart.Started, Capture.TryStart(60, null, _ => { }));

        SlowSeconds(10);

        Assert.Empty(_trips);
    }

    [Fact]
    public void Slow_seconds_during_a_run_dont_count_afterwards()
    {
        Assert.Equal(CaptureStart.Started, Capture.TryStart(60, null, _ => { }));
        SlowSeconds(4);

        Capture.ResetForTests();
        SlowSeconds(4);

        Assert.Empty(_trips);
    }

    [Fact]
    public void Nobody_listening_is_not_a_fault()
    {
        LagWatch.Tripped = null;

        SlowSeconds(10);
    }

    [Fact]
    public void Checking_a_second_allocates_nothing()
    {
        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < 1000; i++)
                Second();
        });

        Assert.Equal(0L, bytes);
    }
}
