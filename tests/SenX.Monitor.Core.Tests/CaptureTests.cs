using System;
using System.Linq;
using System.Threading;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class CaptureTests
{
    private const long GridA = 1001L;
    private const long GridB = 1002L;
    private const long OwnerA = 501L;
    private const long OwnerB = 502L;
    private const long RefineryType = 0x11110000L;
    private const long ReactorType = 0x22220000L;

    private readonly FrameRecord* _record = Native.Allocate<FrameRecord>(1);
    private CaptureResult? _result;
    private bool _delivered;

    public CaptureTests()
    {
        TestSetup.Reset();
        DetailController.TurnFrames = 10;
        DetailController.ResetCounters();
        DetailController.Configure(new[] { Site.EntityBefore, Site.ParallelBefore, Site.SessionBefore, Site.Script, Site.GridCallback });
        Calibration.Set(readBiasNanos: 0d, timedCallNanos: 50d);
        Capture.Inline = true;
    }

    [Fact]
    public void Default_detail_is_left_alone_until_a_run_starts()
    {
        DetailController.SetMode(DetailMode.Grouped);

        Assert.Equal(DetailMode.Grouped, DetailController.Mode);
        Assert.False(Capture.Active);
    }

    [Fact]
    public void A_run_times_every_site_then_puts_grouped_back()
    {
        DetailController.SetMode(DetailMode.Grouped);
        Assert.Equal(Gates.Closed, Gates.Read(Site.SessionBefore));

        RunToMeasuring();

        foreach (int site in new[] { Site.EntityBefore, Site.ParallelBefore, Site.SessionBefore, Site.Script, Site.GridCallback })
            Assert.Equal(0, Gates.Read(site));

        FinishRun();

        Assert.Equal(DetailMode.Grouped, DetailController.Mode);
        Assert.Equal(0, Gates.Read(Site.EntityBefore));
        Assert.Equal(Gates.Closed, Gates.Read(Site.SessionBefore));
    }

    [Fact]
    public void A_run_only_counts_rpcs_sent_while_it_ran()
    {
        // Bytes are counted all the time, so what came before has to be left out.
        NetTraffic.SentRpc(77L, 5000L);

        RunToMeasuring();
        NetTraffic.SentRpc(77L, 1000L);
        NetTraffic.SentRpc(78L, 3000L);
        FinishRun();

        RollupRow[] rows = _result!.RpcsSent.Top(10).ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Equal(78L, rows[0].Key);
        Assert.Equal(3000L, rows[0].MainUnits);
        Assert.Equal(1000L, rows[1].MainUnits);
        Assert.True(_result.TrafficSeconds > 0d);
    }

    [Fact]
    public void A_run_leaves_everything_on_when_an_admin_holds_it_on()
    {
        DetailController.SetMode(DetailMode.Everything);

        RunToMeasuring();
        FinishRun();

        Assert.Equal(DetailMode.Everything, DetailController.Mode);
        Assert.Equal(0, Gates.Read(Site.SessionBefore));
    }

    [Fact]
    public void A_run_works_with_detail_off_and_leaves_it_off()
    {
        DetailController.SetMode(DetailMode.Off);

        RunToMeasuring();
        Assert.Equal(0, Gates.Read(Site.EntityBefore));
        FinishRun();

        Assert.Equal(DetailMode.Off, DetailController.Mode);
        Assert.Equal(Gates.Closed, Gates.Read(Site.EntityBefore));
    }

    [Fact]
    public void An_admin_change_during_a_run_applies_when_it_finishes()
    {
        DetailController.SetMode(DetailMode.Grouped);
        RunToMeasuring();

        DetailController.SetMode(DetailMode.Everything);
        Assert.Equal(0, Gates.Read(Site.EntityBefore));

        FinishRun();

        Assert.Equal(DetailMode.Everything, DetailController.Mode);
        Assert.Equal(0, Gates.Read(Site.SessionBefore));
    }

    [Theory]
    [InlineData(DetailMode.Off)]
    [InlineData(DetailMode.Grouped)]
    public void An_admin_change_during_a_run_never_narrows_it(DetailMode mode)
    {
        DetailController.SetMode(DetailMode.Everything);
        RunToMeasuring();

        DetailController.SetMode(mode);

        foreach (int site in new[] { Site.EntityBefore, Site.ParallelBefore, Site.SessionBefore, Site.Script, Site.GridCallback })
            Assert.Equal(0, Gates.Read(site));

        FinishRun();
        Assert.Equal(mode, DetailController.Mode);
    }

    [Fact]
    public void Only_one_run_at_a_time()
    {
        Assert.Equal(CaptureStart.Started, Start());
        Assert.Equal(CaptureStart.Busy, Start());
    }

    [Fact]
    public void A_finished_run_must_be_collected_before_the_next_one()
    {
        // Results are reused, so a new run must not overwrite one nobody has read.
        RunToMeasuring();
        EndAndSettle();

        Assert.Equal(CaptureStart.Busy, Start());

        Capture.Deliver();
        Assert.True(_delivered);
        Assert.Equal(CaptureStart.Started, Start());
    }

    [Fact]
    public void A_run_is_refused_until_detail_has_started()
    {
        DetailController.Configure(new int[0]);

        Assert.Equal(CaptureStart.NotReady, Start());
        Assert.False(Capture.Active);
    }

    [Fact]
    public void Data_from_before_the_run_is_not_counted()
    {
        DetailController.SetMode(DetailMode.Everything);
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 999_999);

        RunToMeasuring();
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 100);
        FinishRun();

        Assert.Equal(100L, Single(_result!.Grids).MainUnits);
        Assert.Equal(100L, _result.SiteTotal(Site.EntityBefore).MainUnits);
    }

    [Fact]
    public void Time_is_totalled_by_grid_owner_type_and_definition()
    {
        RunToMeasuring();
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, EntityKeys.Pack(7, RefineryType), units: 100);
        AddBlock(Site.EntityBefore, 2L, GridA, OwnerB, EntityKeys.Pack(8, RefineryType), units: 200);
        AddBlock(Site.EntityBefore, 3L, GridB, OwnerB, EntityKeys.Pack(9, ReactorType), units: 400);
        FinishRun();

        Assert.Equal(new[] { (GridB, 400L), (GridA, 300L) }, Totals(_result!.Grids));
        Assert.Equal(new[] { (OwnerB, 600L), (OwnerA, 100L) }, Totals(_result.Owners));
        Assert.Equal(new[] { (ReactorType, 400L), (RefineryType, 300L) }, Totals(_result.BlockTypes));
        Assert.Equal(3, _result.Definitions.Count);
        Assert.Equal(new[] { (EntityKeys.Blocks, 700L) }, Totals(_result.EntityTypes));
    }

    [Fact]
    public void A_grid_counts_towards_grids_but_not_block_types()
    {
        RunToMeasuring();
        AddBlock(Site.EntityBefore, GridA, GridA, OwnerA, EntityKeys.Pack(0, RefineryType), units: 100);
        FinishRun();

        Assert.Equal(100L, Single(_result!.Grids).MainUnits);
        Assert.Equal(0, _result.BlockTypes.Count);
        Assert.Equal(EntityKeys.TypeOf(RefineryType), Single(_result.EntityTypes).Key);
    }

    [Fact]
    public void An_unowned_grid_is_kept_in_its_own_row()
    {
        RunToMeasuring();
        AddBlock(Site.EntityBefore, 1L, GridA, 0L, RefineryType, units: 100);
        FinishRun();

        Assert.Equal(RollupTable.NoKey, Single(_result!.Owners).Key);
    }

    [Fact]
    public void Main_and_worker_time_stay_apart_for_the_same_entity()
    {
        RunToMeasuring();
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 100);

        var worker = new Thread(() => AddBlock(Site.ParallelBefore, 1L, GridA, OwnerA, RefineryType, units: 300));
        worker.Start();
        worker.Join();

        FinishRun();

        RollupRow row = Single(_result!.Grids);
        Assert.Equal(100L, row.MainUnits);
        Assert.Equal(300L, row.WorkerUnits);
        Assert.Equal(400L, row.TotalUnits);
    }

    [Fact]
    public void Script_time_inside_a_block_is_not_counted_twice()
    {
        RunToMeasuring();
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 500);
        Recorder.AddEntity(Site.Script, Phase.Other, 1L, Raw(400), 0);
        FinishRun();

        Assert.Equal(500L, Single(_result!.Grids).MainUnits);
        Assert.Equal(1L, Single(_result.Scripts).Key);
        Assert.Equal(400L, Single(_result.Scripts).MainUnits);
    }

    [Fact]
    public void Clock_overhead_is_removed_from_every_row()
    {
        Calibration.Set(readBiasNanos: 1000d, timedCallNanos: 50d);

        RunToMeasuring();
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 100);
        FinishRun();

        Assert.Equal(90L, Single(_result!.Grids).MainUnits);
        Assert.Equal(90L, _result.SiteTotal(Site.EntityBefore).MainUnits);
    }

    [Fact]
    public void Keyed_time_lands_in_the_matching_list()
    {
        RunToMeasuring();
        Recorder.AddKey(Site.SessionBefore, SiteKeys.Pack(77L, Site.SessionBefore), Raw(100), 0);
        Recorder.AddKey(Site.GridCallback, SiteKeys.Pack(88L, Site.GridCallback), Raw(200), 0);
        FinishRun();

        Assert.Equal(new[] { (77L, 100L) }, Totals(_result!.Session));
        Assert.Equal(new[] { (88L, 200L) }, Totals(_result.Callbacks));
    }

    [Fact]
    public void One_type_is_one_session_row_across_before_and_after()
    {
        RunToMeasuring();
        Recorder.AddKey(Site.SessionBefore, SiteKeys.Pack(77L, Site.SessionBefore), Raw(100), 0);
        Recorder.AddKey(Site.SessionAfter, SiteKeys.Pack(77L, Site.SessionAfter), Raw(50), 0);
        FinishRun();

        Assert.Equal(new[] { (77L, 150L) }, Totals(_result!.Session));
    }

    [Fact]
    public void A_filter_keeps_only_that_grid()
    {
        RunToMeasuring(new CaptureFilter { GridId = GridA });
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 100);
        AddBlock(Site.EntityBefore, 2L, GridB, OwnerB, RefineryType, units: 900);
        FinishRun();

        Assert.Equal(new[] { (GridA, 100L) }, Totals(_result!.Grids));
    }

    [Fact]
    public void A_filter_keeps_only_those_owners()
    {
        RunToMeasuring(new CaptureFilter { Owners = new[] { OwnerB } });
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 100);
        AddBlock(Site.EntityBefore, 2L, GridB, OwnerB, RefineryType, units: 900);
        FinishRun();

        Assert.Equal(new[] { (GridB, 900L) }, Totals(_result!.Grids));
    }

    [Fact]
    public void A_filter_can_leave_out_blocks()
    {
        RunToMeasuring(new CaptureFilter { GridsOnly = true });
        AddBlock(Site.EntityBefore, GridA, GridA, OwnerA, RefineryType, units: 100);
        AddBlock(Site.EntityBefore, 2L, GridA, OwnerA, RefineryType, units: 900);
        FinishRun();

        Assert.Equal(new[] { (GridA, 100L) }, Totals(_result!.Grids));
    }

    [Fact]
    public void A_filter_keeps_only_blocks_of_those_classes()
    {
        RunToMeasuring(new CaptureFilter { BlockTypes = new[] { RefineryType } });
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 100);
        AddBlock(Site.EntityBefore, 2L, GridA, OwnerA, ReactorType, units: 900);
        FinishRun();

        Assert.Equal(new[] { (GridA, 100L) }, Totals(_result!.Grids));
    }

    [Fact]
    public void A_block_filter_goes_by_class_whatever_the_definition()
    {
        RunToMeasuring(new CaptureFilter { BlockTypes = new[] { EntityKeys.TypeOf(RefineryType) } });
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, EntityKeys.Pack(7, RefineryType), units: 100);
        AddBlock(Site.EntityBefore, 2L, GridA, OwnerA, EntityKeys.Pack(9, RefineryType), units: 50);
        FinishRun();

        Assert.Equal(new[] { (GridA, 150L) }, Totals(_result!.Grids));
    }

    [Fact]
    public void A_block_filter_leaves_out_the_grids_own_updates()
    {
        RunToMeasuring(new CaptureFilter { BlockTypes = new[] { RefineryType } });
        AddBlock(Site.EntityBefore, GridA, GridA, OwnerA, EntityKeys.Pack(0, 0x33330000L), units: 500);
        FinishRun();

        Assert.Empty(_result!.Grids.Top(10));
    }

    [Fact]
    public void Slow_calls_from_the_run_are_kept()
    {
        RunToMeasuring();
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: Clock.UnitsPerSecond / 100L);
        FinishRun();

        Assert.Equal(1L, _result!.Slowest[0].Id);
    }

    [Fact]
    public void Slow_calls_from_just_before_the_run_are_kept()
    {
        AddBlock(Site.EntityBefore, 5L, GridA, OwnerA, RefineryType, units: Clock.UnitsPerSecond / 100L);

        RunToMeasuring();
        FinishRun();

        Assert.Equal(5L, _result!.Slowest[0].Id);
    }

    [Fact]
    public void Frames_and_tree_are_counted_only_while_measuring()
    {
        _record->BusyUnits = 100_000U;
        _record->ScopeUnits[Scope.Physics] = 40_000U;

        RunToMeasuring();
        Frame();
        Frame();
        _record->BusyUnits = 300_000U;
        Frame();
        FinishRun();

        // FinishRun's own last frame is still inside the run.
        Assert.Equal(4L, _result!.SimulatedFrames);
        Assert.Equal(800_000L, _result.BusyUnits);
        Assert.Equal(300_000U, _result.WorstFrameUnits);
        Assert.Equal(160_000L, _result.ScopeUnits(Scope.Physics));
        Assert.Equal(200_000d, _result.AverageFrameUnits, 6);
    }

    [Fact]
    public void A_pause_ends_the_run_early_and_says_so()
    {
        RunToMeasuring();

        Capture.OnFrame(_record, simulated: false, paused: true);
        Assert.Equal(CaptureState.Settling, Capture.State);

        Settle();
        Capture.Deliver();

        Assert.True(_result!.EndedEarly);
    }

    [Fact]
    public void A_full_table_shows_up_as_lost_coverage()
    {
        RunToMeasuring();
        for (long id = 1L; id <= TestSetup.MainSlots * 2L; id++)
            AddBlock(Site.EntityBefore, id, GridA, OwnerA, RefineryType, units: 10);

        FinishRun();

        Assert.True(_result!.Coverage < 1d, $"Coverage was {_result.Coverage}.");
        Assert.True(_result.SiteTotal(Site.EntityBefore).OverflowUnits > 0L);
    }

    [Fact]
    public void Timing_cost_is_reported()
    {
        RunToMeasuring();
        for (int i = 0; i < 10; i++)
            AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 10);

        FinishRun();

        // Ten calls at 50 ns, over FinishRun's single frame.
        Assert.Equal(500d, _result!.TimingNanosPerFrame, 6);
    }

    [Fact]
    public void Background_numbers_skip_the_run()
    {
        DetailController.SetMode(DetailMode.Everything);

        RunToMeasuring();
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 999_999);
        FinishRun();

        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 100);
        for (int i = 0; i < DetailController.TurnFrames; i++)
            DetailController.OnFrame(true, false, 1000L);

        Assert.Equal(10d, DetailController.UnitsPerFrame(Site.EntityBefore), 6);
    }

    [Fact]
    public void Background_turns_stop_during_a_run()
    {
        DetailController.SetMode(DetailMode.Grouped);
        RunToMeasuring();
        int active = DetailController.ActiveGroup;

        // One turn and a bit, because a whole number of cycles would land back on the same group.
        for (int i = 0; i < DetailController.TurnFrames + 1; i++)
            DetailController.OnFrame(true, false, 1000L);

        Assert.Equal(active, DetailController.ActiveGroup);
        Assert.Equal(0, DetailController.FramesIntoTurn);
        Assert.Equal(0, Gates.Read(Site.EntityBefore));
        Assert.Equal(0, Gates.Read(Site.SessionBefore));
    }

    [Fact]
    public void A_run_completes_on_real_background_threads()
    {
        Capture.Inline = false;
        Assert.Equal(CaptureStart.Started, Start());

        WaitFor(CaptureState.Measuring);
        AddBlock(Site.EntityBefore, 1L, GridA, OwnerA, RefineryType, units: 100);
        Capture.ExpireForTests();
        WaitFor(CaptureState.Idle);
        Capture.Deliver();

        Assert.Equal(100L, Single(_result!.Grids).MainUnits);
    }

    [Fact]
    public void Measuring_allocates_nothing()
    {
        RunToMeasuring();

        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < 100_000; i++)
                Capture.OnFrame(_record, true, false);
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Top_rows_come_back_largest_first()
    {
        var table = new RollupTable(1 << 8);
        for (long key = 1L; key <= 50L; key++)
            table.Add(key, key * 10L, key, 1L, 0U, key);

        RollupRow[] top = table.Top(3);

        Assert.Equal(new[] { 50L, 49L, 48L }, Array.ConvertAll(top, row => row.Key));
        Assert.Equal(550L, top[0].TotalUnits);
    }

    [Fact]
    public void Top_handles_asking_for_more_rows_than_exist()
    {
        var table = new RollupTable(1 << 8);
        table.Add(1L, 10L, 0L, 1L, 0U, 1L);

        Assert.Single(table.Top(10));
        Assert.Empty(new RollupTable(1 << 8).Top(10));
    }

    [Fact]
    public void A_row_is_named_after_its_heaviest_entity()
    {
        var table = new RollupTable(1 << 8);
        table.Add(1L, 10L, 0L, 1L, 0U, 100L);
        table.Add(1L, 90L, 0L, 1L, 0U, 200L);
        table.Add(1L, 20L, 0L, 1L, 0U, 300L);

        Assert.Equal(200L, table.Top(1)[0].Representative);
    }

    [Fact]
    public void A_key_of_zero_still_gets_a_row()
    {
        var table = new RollupTable(1 << 8);
        table.Add(0L, 10L, 0L, 1L, 0U, 1L);

        Assert.Equal(RollupTable.NoKey, table.Top(1)[0].Key);
    }

    [Fact]
    public void Options_are_read()
    {
        CaptureArgs? args = CaptureArgs.Parse(
            new[] { "--secs=30", "--top=5", "--grid=123", "--player=Sam", "--faction=ABC", "--this", "--noblocks", "--details", "--gps" },
            out string? error);

        Assert.Null(error);
        Assert.Equal(30, args!.Seconds);
        Assert.Equal(5, args.Top);
        Assert.Equal(123L, args.GridId);
        Assert.Equal("Sam", args.Player);
        Assert.Equal("ABC", args.Faction);
        Assert.True(args.This && args.NoBlocks && args.Details && args.Gps);
    }

    [Theory]
    [InlineData("--grid=5")]
    [InlineData("--this")]
    [InlineData("--player=Sam")]
    [InlineData("--faction=ABC")]
    [InlineData("--noblocks")]
    [InlineData("--block=Drill")]
    public void A_filter_is_refused_where_it_cant_apply(string option)
    {
        CaptureArgs args = CaptureArgs.Parse(new[] { option }, out string? _)!;

        Assert.NotNull(args.ProblemWith(CaptureOptions.None));
        Assert.Null(args.ProblemWith(CaptureOptions.Filters));
    }

    [Fact]
    public void Gps_is_refused_where_it_does_nothing()
    {
        CaptureArgs args = CaptureArgs.Parse(new[] { "--gps" }, out string? _)!;

        Assert.NotNull(args.ProblemWith(CaptureOptions.Filters));
        Assert.Null(args.ProblemWith(CaptureOptions.Gps));
    }

    [Fact]
    public void Details_are_refused_where_they_do_nothing()
    {
        CaptureArgs args = CaptureArgs.Parse(new[] { "--details" }, out string? _)!;

        Assert.NotNull(args.ProblemWith(CaptureOptions.Filters));
        Assert.Null(args.ProblemWith(CaptureOptions.Details));
    }

    [Fact]
    public void How_long_and_how_many_suit_every_command()
    {
        CaptureArgs args = CaptureArgs.Parse(new[] { "--secs=5", "--top=3" }, out string? _)!;

        Assert.Null(args.ProblemWith(CaptureOptions.None));
    }

    [Fact]
    public void Reading_history_takes_an_hour()
    {
        Assert.Equal(3600, CaptureArgs.ParseWindow(new[] { "--secs=3600" }, out string? _)!.Seconds);
    }

    [Theory]
    [InlineData("--secs=3601")]
    [InlineData("--secs=0")]
    [InlineData("--top=5")]
    [InlineData("--grid=5")]
    [InlineData("--player=Sam")]
    public void Reading_history_refuses_what_it_cant_do(string option)
    {
        Assert.Null(CaptureArgs.ParseWindow(new[] { option }, out string? error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("grids", GroupDimension.Grids)]
    [InlineData("blocks", GroupDimension.Definitions)]
    [InlineData("definitions", GroupDimension.Definitions)]
    [InlineData("players", GroupDimension.Owners)]
    [InlineData("owners", GroupDimension.Owners)]
    [InlineData("Factions", GroupDimension.Factions)]
    [InlineData("entities", GroupDimension.EntityTypes)]
    [InlineData("nonsense", GroupDimension.None)]
    public void Lists_go_by_the_same_words_as_the_commands(string name, GroupDimension list)
    {
        Assert.Equal(list, GroupDimensions.Named(name));
    }

    [Fact]
    public void A_block_class_is_read()
    {
        Assert.Equal("Drill", CaptureArgs.Parse(new[] { "--block=Drill" }, out string? _)!.BlockType);
    }

    [Fact]
    public void Blocks_and_no_blocks_cant_go_together()
    {
        Assert.Null(CaptureArgs.Parse(new[] { "--block=Drill", "--noblocks" }, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void No_options_means_the_defaults()
    {
        CaptureArgs? args = CaptureArgs.Parse(new string[0], out string? error);

        Assert.Null(error);
        Assert.Equal(CaptureArgs.DefaultSeconds, args!.Seconds);
        Assert.Equal(CaptureArgs.DefaultTop, args.Top);
    }

    [Theory]
    [InlineData("--nonsense")]
    [InlineData("--secs=0")]
    [InlineData("--secs=601")]
    [InlineData("--secs=abc")]
    [InlineData("--top=0")]
    [InlineData("--grid=abc")]
    [InlineData("--player=")]
    [InlineData("--block=")]
    [InlineData("30")]
    public void Bad_options_are_refused(string option)
    {
        Assert.Null(CaptureArgs.Parse(new[] { option }, out string? error));
        Assert.NotNull(error);
    }

    private static long Raw(long units) => (long)Math.Ceiling(units * (double)Clock.Frequency / Clock.UnitsPerSecond);

    private static void AddBlock(int site, long id, long grid, long owner, long definition, long units)
    {
        EntitySlot* created = Recorder.AddEntity(site, Phase.Before, id, Raw(units), 0);
        if (created == null)
            return;

        created->GridId = grid;
        created->OwnerId = owner;
        created->DefinitionKey = definition;
    }

    private static RollupRow Single(RollupTable table) => Assert.Single(table.Top(10));

    private static (long Key, long Units)[] Totals(RollupTable table) =>
        Array.ConvertAll(table.Top(10), row => (row.Key, row.TotalUnits));

    private CaptureStart Start(CaptureFilter? filter = null) =>
        Capture.TryStart(60, filter, result =>
        {
            _result = result;
            _delivered = true;
        });

    private void Frame() => Capture.OnFrame(_record, true, false);

    private void RunToMeasuring(CaptureFilter? filter = null)
    {
        Assert.Equal(CaptureStart.Started, Start(filter));

        for (int i = 0; i < 16 && Capture.State != CaptureState.Measuring; i++)
            Frame();

        Assert.Equal(CaptureState.Measuring, Capture.State);
    }

    private void EndAndSettle()
    {
        Capture.ExpireForTests();
        Frame();
        Settle();
    }

    private void Settle()
    {
        for (int i = 0; i < 16 && Capture.State != CaptureState.Idle; i++)
            Frame();

        Assert.Equal(CaptureState.Idle, Capture.State);
    }

    private void FinishRun()
    {
        EndAndSettle();
        Capture.Deliver();
        Assert.True(_delivered);
        Assert.NotNull(_result);
    }

    private void WaitFor(CaptureState state)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(30);

        while (Capture.State != state)
        {
            Assert.True(DateTime.UtcNow < giveUp, "Timed out waiting for " + state);
            Frame();
            Thread.Sleep(1);
        }
    }
}
