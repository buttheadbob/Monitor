using System;
using System.Collections.Generic;
using System.Linq;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

/// <summary>
/// The rule that matters most here: every active cluster steps exactly once per frame, however turns
/// fall. A cluster stepping twice or not at all would change the game's physics.
/// </summary>
public unsafe class ClusterTurnsTests
{
    private readonly FrameRecord* _record = Native.Allocate<FrameRecord>(1);

    public ClusterTurnsTests()
    {
        TestSetup.Reset();
        DetailController.Configure(new[] { Site.EntityBefore });
        DetailController.SetMode(DetailMode.Grouped);
        Calibration.Set(readBiasNanos: 0d, timedCallNanos: 50d);
        Capture.Inline = true;
        ClusterTurns.Reset();
    }

    private sealed class FakeCluster
    {
        public FakeCluster(int id, bool active = true, TimeSpan? cost = null)
        {
            Id = id;
            Active = active;
            Cost = cost ?? TimeSpan.Zero;
        }

        public int Id { get; }

        public bool Active { get; set; }

        public TimeSpan Cost { get; }

        public int StepsThisFrame { get; set; }

        public int TimesAlone { get; set; }
    }

    private readonly struct Fakes : IClusterList
    {
        private readonly List<FakeCluster> _clusters;

        public Fakes(List<FakeCluster> clusters) => _clusters = clusters;

        public int Count => _clusters.Count;

        public bool IsActive(int index) => _clusters[index].Active;

        public int Id(int index) => _clusters[index].Id;

        public void StepAlone(int index)
        {
            FakeCluster cluster = _clusters[index];
            cluster.StepsThisFrame++;
            cluster.TimesAlone++;

            long until = Clock.Now() + (long)(cluster.Cost.TotalSeconds * Clock.Frequency);
            while (Clock.Now() < until)
            {
            }
        }
    }

    // What PhysicsHooks and the game's parallel path do, around the real turn logic.
    private static void Step(List<FakeCluster> clusters, bool open = true)
    {
        foreach (FakeCluster cluster in clusters)
            cluster.StepsThisFrame = 0;

        if (open)
        {
            ClusterTurns.BeginStep();
            if (ClusterTurns.HasCredit)
            {
                var list = new Fakes(clusters);
                ClusterTurns.StepSomeAlone(ref list);
            }
        }

        var steppedInParallel = new List<FakeCluster>();
        foreach (FakeCluster cluster in clusters)
        {
            if (cluster.Active && !ClusterTurns.IsAlone(cluster.Id))
                steppedInParallel.Add(cluster);
        }

        // The game asks the same question again when finishing, and must get the same answer.
        foreach (FakeCluster cluster in steppedInParallel)
        {
            Assert.False(ClusterTurns.IsAlone(cluster.Id));
            cluster.StepsThisFrame++;
        }

    }

    private static void AssertExactlyOnce(List<FakeCluster> clusters)
    {
        foreach (FakeCluster cluster in clusters)
            Assert.Equal(cluster.Active ? 1 : 0, cluster.StepsThisFrame);
    }

    private static List<FakeCluster> Make(int count) =>
        Enumerable.Range(1, count).Select(id => new FakeCluster(id)).ToList();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(300)]
    public void Every_active_cluster_steps_exactly_once_per_frame(int count)
    {
        List<FakeCluster> clusters = Make(count);

        for (int frame = 0; frame < 500; frame++)
        {
            Step(clusters);
            AssertExactlyOnce(clusters);
        }
    }

    [Fact]
    public void Sleeping_clusters_are_never_woken()
    {
        List<FakeCluster> clusters = Make(10);
        clusters[3].Active = false;
        clusters[7].Active = false;

        for (int frame = 0; frame < 200; frame++)
        {
            Step(clusters);
            AssertExactlyOnce(clusters);
        }

        Assert.Equal(0, clusters[3].TimesAlone);
        Assert.Equal(0, clusters[7].TimesAlone);
    }

    [Fact]
    public void Clusters_can_appear_and_vanish_between_frames()
    {
        List<FakeCluster> clusters = Make(12);

        for (int frame = 0; frame < 300; frame++)
        {
            if (frame % 17 == 0 && clusters.Count > 2)
                clusters.RemoveAt(clusters.Count - 1);

            if (frame % 23 == 0)
                clusters.Add(new FakeCluster(1000 + frame));

            Step(clusters);
            AssertExactlyOnce(clusters);
        }
    }

    [Fact]
    public void Every_active_cluster_gets_a_turn()
    {
        List<FakeCluster> clusters = Make(50);

        for (int frame = 0; frame < 60; frame++)
            Step(clusters);

        Assert.All(clusters, cluster => Assert.True(cluster.TimesAlone > 0, "Cluster " + cluster.Id + " had no turn."));
    }

    [Fact]
    public void A_heavy_cluster_uses_up_the_budget_for_the_frames_after_it()
    {
        // 5 ms is six frames of budget, so it can't come straight back.
        var heavy = new FakeCluster(1, cost: TimeSpan.FromMilliseconds(5));
        var clusters = new List<FakeCluster> { heavy };

        Step(clusters);
        Assert.Equal(1, heavy.TimesAlone);

        for (int frame = 0; frame < 4; frame++)
        {
            Step(clusters);
            AssertExactlyOnce(clusters);
        }

        Assert.Equal(1, heavy.TimesAlone);
    }

    [Fact]
    public void Stepping_alone_stays_inside_its_share_of_frame_time()
    {
        var clusters = new List<FakeCluster>
        {
            new(1, cost: TimeSpan.FromMilliseconds(4)),
            new(2, cost: TimeSpan.FromMilliseconds(3)),
            new(3, cost: TimeSpan.FromMilliseconds(5)),
        };

        const int Frames = 600;
        long started = Clock.Now();
        for (int frame = 0; frame < Frames; frame++)
            Step(clusters);

        double spentSeconds = (Clock.Now() - started) / (double)Clock.Frequency;
        double budgetSeconds = Frames / 60d * ClusterTurns.FrameShare;

        // One step can overshoot, and that's paid back later.
        Assert.True(spentSeconds < budgetSeconds * 1.5d, $"Spent {spentSeconds:0.000} s of a {budgetSeconds:0.000} s budget.");
        Assert.All(clusters, cluster => Assert.True(cluster.TimesAlone >= 2, "Cluster " + cluster.Id + " was barely timed."));
    }

    [Fact]
    public void No_cluster_steps_alone_twice_in_a_frame()
    {
        // Free clusters never spend the budget, so only the sweep limit stops the loop.
        List<FakeCluster> clusters = Make(5);

        Step(clusters);

        Assert.All(clusters, cluster => Assert.Equal(1, cluster.StepsThisFrame));
    }

    [Fact]
    public void Marks_from_one_step_never_reach_the_next()
    {
        List<FakeCluster> clusters = Make(3);

        Step(clusters);
        Assert.Contains(clusters, cluster => ClusterTurns.IsAlone(cluster.Id));

        ClusterTurns.BeginStep();

        Assert.All(clusters, cluster => Assert.False(ClusterTurns.IsAlone(cluster.Id)));
    }

    [Fact]
    public void A_step_that_threw_cannot_stop_a_cluster_stepping_next_frame()
    {
        List<FakeCluster> clusters = Make(3);
        var list = new Fakes(clusters);

        ClusterTurns.BeginStep();
        ClusterTurns.StepSomeAlone(ref list);

        // The rest of that step never ran. The next one must still step everything once.
        Step(clusters);

        AssertExactlyOnce(clusters);
    }

    [Fact]
    public void Turns_allocate_nothing_once_warmed_up()
    {
        List<FakeCluster> clusters = Make(20);
        var list = new Fakes(clusters);

        for (int i = 0; i < 100; i++)
        {
            ClusterTurns.BeginStep();
            ClusterTurns.StepSomeAlone(ref list);
        }

        long bytes = Allocation.Measure(() =>
        {
            var inner = new Fakes(clusters);
            for (int i = 0; i < 10_000; i++)
            {
                ClusterTurns.BeginStep();
                ClusterTurns.StepSomeAlone(ref inner);
                ClusterTurns.IsAlone(1);
            }
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void A_run_opens_cluster_timing_only_when_asked()
    {
        RunToMeasuring(clusters: false);
        Assert.False(ClusterTurns.Open);
        FinishRun(out _);

        RunToMeasuring(clusters: true);
        Assert.True(ClusterTurns.Open);
        FinishRun(out CaptureResult? result);

        Assert.True(result!.ClustersTimed);
        Assert.False(ClusterTurns.Open);
    }

    [Fact]
    public void Neither_detail_mode_ever_opens_cluster_timing()
    {
        // Stepping alone changes how physics is scheduled, so only a run may do it.
        DetailController.Configure(new[] { Site.EntityBefore, Site.PhysicsCluster });
        DetailController.SetMode(DetailMode.Everything);
        Assert.False(ClusterTurns.Open);

        DetailController.SetMode(DetailMode.Grouped);
        for (int i = 0; i < DetailController.TurnFrames * 5; i++)
            DetailController.OnFrame(true, false, 1000L);

        Assert.False(ClusterTurns.Open);
    }

    [Fact]
    public void A_run_reports_each_cluster_with_its_own_steps()
    {
        RunToMeasuring(clusters: true);

        var clusters = new List<FakeCluster> { new(0), new(5, cost: TimeSpan.FromMilliseconds(1)), new(9) };
        for (int frame = 0; frame < 200; frame++)
            Step(clusters);

        FinishRun(out CaptureResult? result);

        Assert.Equal(3, result!.Clusters.Count);

        RollupRow[] rows = result.Clusters.Top(3, row => row.TotalUnits / (double)row.Calls);
        Assert.Equal(5, ClusterTurns.ClusterIdOf(rows[0]));
        Assert.Contains(rows, row => ClusterTurns.ClusterIdOf(row) == 0);
        Assert.True(rows[0].MainUnits / rows[0].Calls >= 9_000L, "A 1 ms step read " + (rows[0].MainUnits / rows[0].Calls) + " units.");
        Assert.Equal(clusters[1].TimesAlone, (int)rows[0].Calls);
    }

    [Fact]
    public void Ranking_by_average_step_is_fair_to_clusters_timed_less_often()
    {
        var table = new RollupTable(1 << 8);
        table.Add(1L, 1000L, 0L, 100L, 0U, 0L);
        table.Add(2L, 500L, 0L, 5L, 0U, 0L);

        Assert.Equal(1L, table.Top(1)[0].Key);
        Assert.Equal(2L, table.Top(1, row => row.TotalUnits / (double)row.Calls)[0].Key);
    }

    [Fact]
    public void A_row_can_be_found_without_adding_one()
    {
        var table = new RollupTable(1 << 8);
        table.Add(42L, 10L, 5L, 1L, 0U, 0L);

        Assert.True(table.TryFind(42L, out RollupRow row));
        Assert.Equal(15L, row.TotalUnits);
        Assert.False(table.TryFind(43L, out _));
        Assert.Equal(1, table.Count);
    }

    [Theory]
    [InlineData("--inspect=abc")]
    [InlineData("--inspect=-1")]
    [InlineData("--takeme=abc")]
    public void Bad_cluster_options_are_refused(string option)
    {
        Assert.Null(CaptureArgs.Parse(new[] { option }, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Cluster_options_are_read()
    {
        Assert.Equal(2, CaptureArgs.Parse(new[] { "--inspect=2" }, out _)!.Inspect);
        Assert.Equal(3, CaptureArgs.Parse(new[] { "--takeme=3" }, out _)!.TakeMe);
        Assert.True(CaptureArgs.Parse(new[] { "--takeme=done" }, out _)!.TakeMeDone);
        Assert.True(CaptureArgs.Parse(new[] { "--inspect=0" }, out _)!.ForPhysicsOnly);
        Assert.False(CaptureArgs.Parse(new[] { "--secs=5" }, out _)!.ForPhysicsOnly);
    }

    private void RunToMeasuring(bool clusters)
    {
        Assert.Equal(CaptureStart.Started, Capture.TryStart(60, null, result => s_result = result, clusters));

        for (int i = 0; i < 16 && Capture.State != CaptureState.Measuring; i++)
            Capture.OnFrame(_record, true, false);

        Assert.Equal(CaptureState.Measuring, Capture.State);
    }

    private void FinishRun(out CaptureResult? result)
    {
        Capture.ExpireForTests();

        for (int i = 0; i < 16 && Capture.State != CaptureState.Idle; i++)
            Capture.OnFrame(_record, true, false);

        Capture.Deliver();
        result = s_result;
    }

    private static CaptureResult? s_result;
}
