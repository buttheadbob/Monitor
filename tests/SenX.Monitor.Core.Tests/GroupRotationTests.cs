using System.Collections.Generic;
using System.Linq;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class GroupRotationTests
{
    private static readonly int[] AllSites = SiteGroups.All.SelectMany(group => group.Sites).Distinct().ToArray();

    public GroupRotationTests()
    {
        TestSetup.Reset();
        DetailController.TurnFrames = DetailController.DefaultTurnFrames;
        DetailController.ResetCounters();
        Calibration.Set(readBiasNanos: 0d, timedCallNanos: 45d);
        DetailController.Configure(AllSites);
    }

    private static void Frames(int count, bool simulated = true, bool paused = false)
    {
        for (int i = 0; i < count; i++)
            DetailController.OnFrame(simulated, paused, 100_000L);
    }

    private static bool IsOpen(int site) => Gates.Read(site) != Gates.Closed;

    private static int[] OpenSites() => AllSites.Where(IsOpen).ToArray();

    private static void Record(int site, int calls)
    {
        for (int i = 0; i < calls; i++)
            Recorder.AddKey(site, 1000L + site, 100L, Gates.Read(site));
    }

    [Fact]
    public void Every_site_belongs_to_a_group()
    {
        for (int site = Site.First; site <= Site.Last; site++)
        {
            // Stepping clusters alone changes how physics runs, so only a measuring run may do it.
            if (site == Site.PhysicsCluster)
                Assert.DoesNotContain(site, AllSites);
            else
                Assert.Contains(site, AllSites);
        }
    }

    [Fact]
    public void Rpcs_are_measured_with_the_session_group()
    {
        Assert.Contains(SiteGroups.All, group => group.Name == "Session" && group.Sites.Contains(Site.Rpc));
        Assert.False(SiteGroups.IsNested(Site.Rpc));
    }

    [Fact]
    public void Groups_hold_only_real_sites()
    {
        foreach (int site in AllSites)
            Assert.InRange(site, Site.First, Site.Last);
    }

    [Fact]
    public void Every_site_has_a_readable_name()
    {
        foreach (int site in AllSites)
            Assert.DoesNotContain("Site ", SiteGroups.Name(site));
    }

    [Fact]
    public void A_turn_is_long_enough_to_see_every_update100_entity() =>
        Assert.True(DetailController.DefaultTurnFrames >= 100);

    [Fact]
    public void Off_leaves_everything_closed()
    {
        DetailController.SetMode(DetailMode.Off);
        Frames(500);

        Assert.Empty(OpenSites());
    }

    [Fact]
    public void Grouped_opens_only_the_first_group()
    {
        DetailController.SetMode(DetailMode.Grouped);

        Assert.Equal(SiteGroups.All[0].Sites.OrderBy(s => s), OpenSites().OrderBy(s => s));
    }

    [Fact]
    public void Everything_opens_every_site_timing_every_call()
    {
        DetailController.SetMode(DetailMode.Everything);

        foreach (int site in AllSites)
            Assert.Equal(0, Gates.Read(site));
    }

    [Fact]
    public void Everything_stays_open_across_turns()
    {
        DetailController.SetMode(DetailMode.Everything);
        Frames(DetailController.TurnFrames * 3 + 7);

        Assert.Equal(AllSites.Length, OpenSites().Length);
    }

    [Fact]
    public void A_group_stays_open_for_its_whole_turn()
    {
        DetailController.SetMode(DetailMode.Grouped);
        Frames(DetailController.TurnFrames - 1);

        Assert.Equal(0, DetailController.ActiveGroup);
    }

    [Fact]
    public void The_next_group_takes_over_when_a_turn_ends()
    {
        DetailController.SetMode(DetailMode.Grouped);
        Frames(DetailController.TurnFrames);

        Assert.Equal(1, DetailController.ActiveGroup);
        Assert.Equal(SiteGroups.All[1].Sites.OrderBy(s => s), OpenSites().OrderBy(s => s));
    }

    [Fact]
    public void Groups_take_turns_in_order_and_wrap_around()
    {
        DetailController.SetMode(DetailMode.Grouped);

        for (int cycle = 0; cycle < 3; cycle++)
        {
            for (int group = 0; group < SiteGroups.All.Length; group++)
            {
                Assert.Equal(group, DetailController.ActiveGroup);
                Frames(DetailController.TurnFrames);
            }
        }
    }

    [Fact]
    public void Every_site_gets_measured_in_a_full_cycle()
    {
        DetailController.SetMode(DetailMode.Grouped);
        var seen = new HashSet<int>();

        for (int group = 0; group < SiteGroups.All.Length; group++)
        {
            seen.UnionWith(OpenSites());
            Frames(DetailController.TurnFrames);
        }

        Assert.Equal(AllSites.Length, seen.Count);
    }

    [Fact]
    public void Turns_count_simulated_frames_not_time()
    {
        // A laggy or idle stretch must not cut a group's turn short.
        DetailController.SetMode(DetailMode.Grouped);
        Frames(10_000, simulated: false);

        Assert.Equal(0, DetailController.ActiveGroup);
        Assert.Equal(0, DetailController.FramesIntoTurn);
    }

    [Fact]
    public void A_paused_server_closes_detail_and_resumes_cleanly()
    {
        DetailController.SetMode(DetailMode.Grouped);
        Frames(10);

        Frames(5, simulated: false, paused: true);
        Assert.Empty(OpenSites());

        Frames(1);
        Assert.Equal(SiteGroups.All[0].Sites.OrderBy(s => s), OpenSites().OrderBy(s => s));
    }

    [Fact]
    public void Sites_a_game_update_removed_never_open()
    {
        int missing = SiteGroups.All[0].Sites[0];
        DetailController.Configure(AllSites.Where(site => site != missing).ToArray());
        DetailController.SetMode(DetailMode.Everything);

        Assert.False(IsOpen(missing));
    }

    [Fact]
    public void A_group_with_no_patched_sites_is_skipped()
    {
        int[] kept = AllSites.Except(SiteGroups.All[1].Sites).ToArray();
        DetailController.Configure(kept);

        Assert.Equal(SiteGroups.All.Length - 1, DetailController.Groups.Length);
        Assert.DoesNotContain(DetailController.Groups, group => group.Name == SiteGroups.All[1].Name);
    }

    [Fact]
    public void With_no_sites_patched_nothing_breaks()
    {
        DetailController.Configure(new int[0]);
        DetailController.SetMode(DetailMode.Grouped);
        Frames(500);

        Assert.Empty(OpenSites());
    }

    [Fact]
    public void Inside_a_turn_calls_are_counted_exactly()
    {
        int site = SiteGroups.All[0].Sites[0];
        DetailController.SetMode(DetailMode.Grouped);

        Record(site, DetailController.TurnFrames * 3);
        Frames(DetailController.TurnFrames);

        Assert.Equal(3d, DetailController.CallsPerFrame(site), 6);
        Assert.Equal(DetailController.TurnFrames, DetailController.FramesMeasured(site));
    }

    [Fact]
    public void Time_per_frame_is_over_the_frames_measured_only()
    {
        // A site measured for 120 frames out of 480 must report its real per-frame time, not a quarter of it.
        int site = SiteGroups.All[0].Sites[0];
        DetailController.SetMode(DetailMode.Grouped);
        Record(site, DetailController.TurnFrames);

        for (int group = 0; group < SiteGroups.All.Length; group++)
            Frames(DetailController.TurnFrames);

        Assert.Equal(Clock.ToUnits(100L), DetailController.UnitsPerFrame(site), 6);
    }

    [Fact]
    public void A_group_that_costs_too_much_is_sampled_on_its_next_turn()
    {
        int site = SiteGroups.All[0].Sites[0];
        DetailController.TurnFrames = 100;
        DetailController.SetMode(DetailMode.Grouped);

        for (int i = 0; i < 2_000_000; i++)
            Recorder.AddKey(site, 1000L + i % 16, 100L, 0);

        Frames(DetailController.TurnFrames);

        Assert.True(DetailController.ShiftOf(0) > 0, "The group kept timing every call.");
    }

    [Fact]
    public void A_group_within_its_ceiling_times_every_call()
    {
        int site = SiteGroups.All[0].Sites[0];
        DetailController.SetMode(DetailMode.Grouped);

        Record(site, 100);
        Frames(DetailController.TurnFrames);

        Assert.Equal(0, DetailController.ShiftOf(0));
    }

    [Theory]
    [InlineData(100d, 1000d, 0)]
    [InlineData(1000d, 1000d, 0)]
    [InlineData(1001d, 1000d, 1)]
    [InlineData(8000d, 1000d, 3)]
    [InlineData(1e12, 1000d, Gates.MaxShift)]
    public void Shift_is_the_smallest_that_fits(double cost, double ceiling, int expected) =>
        Assert.Equal(expected, DetailController.ShiftFor(cost, ceiling));

    [Fact]
    public void Rotation_allocates_nothing()
    {
        DetailController.SetMode(DetailMode.Grouped);

        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < 50_000; i++)
                DetailController.OnFrame(true, false, 100_000L);
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Calibration_measures_a_real_clock()
    {
        Calibration.Run();

        Assert.InRange(Calibration.ClockPairNanos, 1d, 100_000d);
        Assert.InRange(Calibration.ReadBiasNanos, 0d, Calibration.ClockPairNanos);
        Assert.True(Calibration.TimedCallNanos > Calibration.ClockPairNanos);
    }

    [Fact]
    public void Clock_overhead_is_taken_out_of_reported_time()
    {
        // Monitor must not report the cost of its own clock as game time.
        int site = SiteGroups.All[0].Sites[0];
        long raw = (long)(1000 * (double)Clock.Frequency / Clock.UnitsPerSecond);
        Calibration.Set(readBiasNanos: 2000d, timedCallNanos: 45d);
        DetailController.SetMode(DetailMode.Everything);

        for (int i = 0; i < DetailController.TurnFrames; i++)
            Recorder.AddKey(site, 7L, raw, 0);

        Frames(DetailController.TurnFrames);

        Assert.InRange(DetailController.UnitsPerFrame(site), 979d, 981d);
    }

    [Fact]
    public void Clock_overhead_can_never_push_time_below_zero()
    {
        int site = SiteGroups.All[0].Sites[0];
        Calibration.Set(readBiasNanos: 1_000_000d, timedCallNanos: 45d);
        DetailController.SetMode(DetailMode.Everything);

        Recorder.AddKey(site, 7L, 1L, 0);
        Frames(DetailController.TurnFrames);

        Assert.Equal(0d, DetailController.UnitsPerFrame(site));
    }

    [Fact]
    public void Main_thread_time_is_kept_apart_from_worker_time()
    {
        // Workers run at once, so their summed time is CPU time and must not read as frame time.
        int site = SiteGroups.All[0].Sites[0];
        long raw = (long)(1000 * (double)Clock.Frequency / Clock.UnitsPerSecond);
        DetailController.TurnFrames = 100;
        DetailController.SetMode(DetailMode.Everything);

        for (int i = 0; i < 100; i++)
            Recorder.AddKey(site, 7L, raw, 0);

        var worker = new System.Threading.Thread(() =>
        {
            for (int i = 0; i < 300; i++)
                Recorder.AddKey(site, 7L, raw, 0);
        });
        worker.Start();
        worker.Join();

        Frames(100);

        Assert.InRange(DetailController.MainUnitsPerFrame(site), 999d, 1001d);
        Assert.InRange(DetailController.WorkerUnitsPerFrame(site), 2997d, 3003d);
        Assert.Equal(DetailController.MainUnitsPerFrame(site) + DetailController.WorkerUnitsPerFrame(site), DetailController.UnitsPerFrame(site), 6);
    }

    [Fact]
    public void Each_measurement_records_how_long_frames_took_while_it_ran()
    {
        int site = SiteGroups.All[0].Sites[0];
        DetailController.SetMode(DetailMode.Grouped);

        for (int i = 0; i < DetailController.TurnFrames; i++)
            DetailController.OnFrame(true, false, 333_000L);

        Assert.Equal(333_000d, DetailController.FrameUnitsDuring(site), 6);
    }

    [Fact]
    public void A_later_turn_replaces_the_context_of_an_earlier_one()
    {
        // A group measured in calm frames must not inherit the context of the group before it.
        int first = SiteGroups.All[0].Sites[0];
        int second = SiteGroups.All[1].Sites[0];
        DetailController.SetMode(DetailMode.Grouped);

        for (int i = 0; i < DetailController.TurnFrames; i++)
            DetailController.OnFrame(true, false, 500_000L);

        for (int i = 0; i < DetailController.TurnFrames; i++)
            DetailController.OnFrame(true, false, 100_000L);

        Assert.Equal(500_000d, DetailController.FrameUnitsDuring(first), 6);
        Assert.Equal(100_000d, DetailController.FrameUnitsDuring(second), 6);
    }

    [Fact]
    public void Sampled_groups_still_report_full_call_counts_and_time()
    {
        int site = SiteGroups.All[0].Sites[0];
        long raw = (long)(1000 * (double)Clock.Frequency / Clock.UnitsPerSecond);
        DetailController.TurnFrames = 100;
        DetailController.SetMode(DetailMode.Grouped);

        for (int i = 0; i < 2_000_000; i++)
            Recorder.AddKey(site, 7L, 1L, 0);

        Frames(100);
        int shift = DetailController.ShiftOf(0);
        Assert.True(shift > 0);

        for (int group = 1; group < SiteGroups.All.Length; group++)
            Frames(100);

        Recorder.AddKey(site, 7L, raw, shift);
        Frames(100);

        Assert.Equal((double)(1 << shift) / 100d, DetailController.CallsPerFrame(site), 6);
    }

    [Fact]
    public void Nested_sites_are_marked_so_totals_can_skip_them()
    {
        Assert.True(SiteGroups.IsNested(Site.GridCallback));
        Assert.True(SiteGroups.IsNested(Site.EntityLogic));
        Assert.True(SiteGroups.IsNested(Site.Script));
        Assert.False(SiteGroups.IsNested(Site.EntityBefore));
        Assert.False(SiteGroups.IsNested(Site.ParallelBefore));
    }
}
