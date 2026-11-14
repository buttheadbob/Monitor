using System;
using System.Collections.Generic;
using System.Linq;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class SweepTests
{
    private const int TurnFrames = 10;
    private const long Grid = 1001L;
    private const long Owner = 501L;
    private const long Type = 0x11110000L;

    private static readonly int[] s_allSites = SiteGroups.All.SelectMany(group => group.Sites).Distinct().ToArray();

    private readonly byte[] _buffer = new byte[HistoryWriter.MaxBytes];
    private readonly FrameRecord* _record = Native.Allocate<FrameRecord>(1);

    public SweepTests()
    {
        TestSetup.Reset();
        FrameEngine.Initialize();
        FrameEngine.Start();
        DetailController.TurnFrames = TurnFrames;
        DetailController.ResetCounters();
        DetailController.Configure(s_allSites);
        Calibration.Set(readBiasNanos: 0d, timedCallNanos: 50d);
        Background.Inline = true;
    }

    private static long Raw(long units) => (long)Math.Ceiling(units * (double)Clock.Frequency / Clock.UnitsPerSecond);

    private static void AddBlock(int site, int phase, long units)
    {
        EntitySlot* created = Recorder.AddEntity(site, phase, 1L, Raw(units), 0);
        if (created == null)
            return;

        created->GridId = Grid;
        created->OwnerId = Owner;
        created->DefinitionKey = Type;
    }

    private static void Frames(int count, bool simulated = true, bool paused = false)
    {
        for (int i = 0; i < count; i++)
        {
            Sweep.OnFrame(Clock.Now(), simulated, paused);
            DetailController.OnFrame(simulated, paused, 1000L);
        }
    }

    // This frame isn't one of the test's, so it doesn't count as simulated. A test's figures are over the frames it asked for.
    private static void RunSweep()
    {
        Sweep.MakeDue();
        Sweep.OnFrame(Clock.Now(), false, false);

        for (int i = 0; i < 16 && Sweep.Busy; i++)
            Sweep.OnFrame(Clock.Now(), true, false);

        Assert.False(Sweep.Busy);
    }

    private static List<GroupRecord> RowsSince(long from, GroupDimension dimension)
    {
        var rows = new List<GroupRecord>();

        for (long sequence = from; sequence < Sweep.Rows.Head; sequence++)
        {
            GroupRecord row = default;
            Assert.True(Sweep.Rows.TryCopy(sequence, (byte*)&row));

            if (row.Dimension == dimension)
                rows.Add(row);
        }

        return rows;
    }

    [Fact]
    public void A_sweep_totals_the_time_per_frame()
    {
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        for (int frame = 0; frame < TurnFrames; frame++)
        {
            AddBlock(Site.EntityBefore, Phase.Before, 100L);
            Frames(1);
        }

        RunSweep();

        GroupRecord row = Assert.Single(RowsSince(from, GroupDimension.Grids));
        Assert.Equal(Grid, row.Key);
        Assert.Equal(1L, row.Representative);
        Assert.Equal(100U, row.MainUnits);
        Assert.Equal(Owner, Assert.Single(RowsSince(from, GroupDimension.Owners)).Key);
        Assert.Equal(Type, Assert.Single(RowsSince(from, GroupDimension.BlockTypes)).Key);
    }

    [Fact]
    public void A_site_measured_part_of_the_time_reads_as_if_measured_all_of_it()
    {
        // Four groups taking turns, so each sees a quarter of the frames. Grouped must not read 25 for 100.
        DetailController.SetMode(DetailMode.Grouped);
        long from = Sweep.Rows.Head;
        int groups = DetailController.Groups.Length;
        Assert.Equal(4, groups);

        for (int frame = 0; frame < TurnFrames * groups; frame++)
        {
            if (Gates.Read(Site.EntityBefore) != Gates.Closed)
                AddBlock(Site.EntityBefore, Phase.Before, 100L);

            Frames(1);
        }

        RunSweep();

        Assert.InRange(Assert.Single(RowsSince(from, GroupDimension.Grids)).MainUnits, 95U, 105U);
    }

    [Fact]
    public void Phases_measured_at_different_times_add_up_on_one_grid()
    {
        // The whole point of scaling each phase: a grid's rows come from different groups.
        DetailController.SetMode(DetailMode.Grouped);
        long from = Sweep.Rows.Head;
        int groups = DetailController.Groups.Length;

        for (int frame = 0; frame < TurnFrames * groups; frame++)
        {
            if (Gates.Read(Site.EntityBefore) != Gates.Closed)
                AddBlock(Site.EntityBefore, Phase.Before, 100L);

            if (Gates.Read(Site.EntityBefore10) != Gates.Closed)
                AddBlock(Site.EntityBefore10, Phase.Every10, 40L);

            Frames(1);
        }

        RunSweep();

        Assert.InRange(Assert.Single(RowsSince(from, GroupDimension.Grids)).MainUnits, 130U, 150U);
    }

    [Fact]
    public void A_sweep_waits_until_every_group_has_had_a_turn()
    {
        // Under heavy lag a cycle can outlast the period. Sweeping early would drop a whole group from the charts.
        DetailController.SetMode(DetailMode.Grouped);

        Frames(TurnFrames);
        Sweep.MakeDue();
        Sweep.OnFrame(Clock.Now(), true, false);

        Assert.False(Sweep.Busy);

        // It's still due, so it goes by itself on the frame the last group has had its turn.
        bool went = false;

        for (int frame = 0; frame < TurnFrames * DetailController.Groups.Length && !went; frame++)
        {
            Frames(1);
            went = Sweep.Busy;
        }

        Assert.True(went);
    }

    [Fact]
    public void A_sweep_clears_the_tables_so_they_cant_fill_up()
    {
        DetailController.SetMode(DetailMode.Everything);
        AddBlock(Site.EntityBefore, Phase.Before, 100L);
        Recorder.AddKey(Site.SessionBefore, SiteKeys.Pack(77L, Site.SessionBefore), Raw(100L), 0);
        Frames(TurnFrames);

        RunSweep();

        ThreadBlock* block = ThreadBlocks.At(0);
        for (int i = 0; i < block->Entities.Capacity; i++)
            Assert.Equal(0L, block->Entities.At(i)->Id);

        for (int i = 0; i < block->Keys.Capacity; i++)
            Assert.Equal(0L, block->Keys.At(i)->Key);
    }

    [Fact]
    public void Time_from_one_sweep_doesnt_reach_the_next()
    {
        DetailController.SetMode(DetailMode.Everything);

        AddBlock(Site.EntityBefore, Phase.Before, 1000L);
        Frames(TurnFrames);
        RunSweep();

        long from = Sweep.Rows.Head;
        for (int frame = 0; frame < TurnFrames; frame++)
        {
            AddBlock(Site.EntityBefore, Phase.Before, 10L);
            Frames(1);
        }

        RunSweep();

        Assert.Equal(10U, Assert.Single(RowsSince(from, GroupDimension.Grids)).MainUnits);
    }

    [Fact]
    public void Detail_comes_back_after_a_sweep()
    {
        DetailController.SetMode(DetailMode.Everything);
        Frames(TurnFrames);

        RunSweep();

        Assert.Equal(0, Gates.Read(Site.EntityBefore));
        Assert.Equal(DetailMode.Everything, DetailController.Mode);
    }

    [Fact]
    public void Gates_are_closed_while_tables_are_totalled()
    {
        DetailController.SetMode(DetailMode.Everything);
        Frames(TurnFrames);

        Sweep.MakeDue();
        Sweep.OnFrame(Clock.Now(), true, false);

        Assert.True(Sweep.Busy);
        Assert.Equal(Gates.Closed, Gates.Read(Site.EntityBefore));
    }

    [Fact]
    public void Site_numbers_stay_right_across_a_sweep()
    {
        DetailController.SetMode(DetailMode.Everything);
        AddBlock(Site.EntityBefore, Phase.Before, 999_999L);
        Frames(TurnFrames);
        RunSweep();

        AddBlock(Site.EntityBefore, Phase.Before, 100L);
        Frames(TurnFrames);

        Assert.Equal(10d, DetailController.UnitsPerFrame(Site.EntityBefore), 6);
    }

    [Fact]
    public void No_sweep_runs_with_detail_off()
    {
        DetailController.SetMode(DetailMode.Off);
        long from = Sweep.Rows.Head;

        Frames(TurnFrames);
        Sweep.MakeDue();
        Sweep.OnFrame(Clock.Now(), true, false);

        Assert.False(Sweep.Busy);
        Assert.Equal(from, Sweep.Rows.Head);
    }

    [Fact]
    public void No_sweep_runs_while_paused()
    {
        DetailController.SetMode(DetailMode.Everything);
        Frames(TurnFrames);

        Sweep.MakeDue();
        Sweep.OnFrame(Clock.Now(), false, paused: true);

        Assert.False(Sweep.Busy);
    }

    [Fact]
    public void A_measuring_run_waits_for_a_sweep_to_finish()
    {
        DetailController.SetMode(DetailMode.Everything);
        Frames(TurnFrames);

        Sweep.MakeDue();
        Sweep.OnFrame(Clock.Now(), true, false);
        Assert.True(Sweep.Busy);

        Assert.Equal(CaptureStart.Started, Capture.TryStart(60, null, result => { }));
        Capture.OnFrame(_record, true, false);

        Assert.Equal(CaptureState.Requested, Capture.State);

        for (int i = 0; i < 16 && Sweep.Busy; i++)
            Sweep.OnFrame(Clock.Now(), true, false);

        Capture.OnFrame(_record, true, false);

        Assert.Equal(CaptureState.Closing, Capture.State);
    }

    [Fact]
    public void No_sweep_starts_during_a_run()
    {
        DetailController.SetMode(DetailMode.Everything);
        Frames(TurnFrames);

        Assert.Equal(CaptureStart.Started, Capture.TryStart(60, null, result => { }));
        Sweep.MakeDue();
        Sweep.OnFrame(Clock.Now(), true, false);

        Assert.False(Sweep.Busy);
    }

    [Fact]
    public void A_list_keeps_only_its_top_rows()
    {
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        for (long id = 1L; id <= Sweep.RowsPerDimension + 15L; id++)
        {
            EntitySlot* created = Recorder.AddEntity(Site.EntityBefore, Phase.Before, id, Raw(id * 10L), 0);
            created->GridId = 5000L + id;
        }

        Frames(TurnFrames);
        RunSweep();

        List<GroupRecord> rows = RowsSince(from, GroupDimension.Grids);

        Assert.Equal(Sweep.RowsPerDimension, rows.Count);
        Assert.Equal(5000L + Sweep.RowsPerDimension + 15L, rows[0].Key);
    }

    [Fact]
    public void The_full_tables_can_be_read_after_a_sweep()
    {
        DetailController.SetMode(DetailMode.Everything);

        for (int frame = 0; frame < TurnFrames; frame++)
        {
            AddBlock(Site.EntityBefore, Phase.Before, 100L);
            Frames(1);
        }

        RunSweep();

        Assert.NotNull(Sweep.LatestTotals);
        Assert.True(Sweep.LatestTotals!.Grids.Count > 0);
    }

    [Fact]
    public void There_are_no_full_tables_before_a_sweep()
    {
        Assert.Null(Sweep.LatestTotals);
    }

    [Fact]
    public void A_runs_tables_arent_taken_for_a_sweeps()
    {
        DetailController.SetMode(DetailMode.Everything);
        Frames(TurnFrames);
        RunSweep();

        Assert.NotNull(Sweep.LatestTotals);
        Assert.Equal(CaptureStart.Started, Capture.TryStart(1, new CaptureFilter { GridId = 5L }, _ => { }));

        Assert.Null(Sweep.LatestTotals);
    }

    [Fact]
    public void A_total_that_fails_leaves_nothing_behind_and_says_so()
    {
        Sweep.TakeFault();
        Sweep.OwnerOf = _ => throw new InvalidOperationException("A broken owner.");
        DetailController.SetMode(DetailMode.Everything);

        long head = Sweep.Rows.Head;
        long whole = Sweep.WholeRows;

        AddKeyed(Site.SessionBefore, 77L, 100L);
        Frames(TurnFrames);
        RunSweep();

        Assert.Equal(head, Sweep.Rows.Head);
        Assert.Equal(whole, Sweep.WholeRows);
        Assert.Null(Sweep.LatestTotals);
        Assert.NotNull(Sweep.TakeFault());
        Assert.Null(Sweep.TakeFault());
    }

    [Fact]
    public void Group_rows_survive_the_trip_to_quasar()
    {
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        for (int frame = 0; frame < TurnFrames; frame++)
        {
            AddBlock(Site.EntityBefore, Phase.Before, 100L);
            Frames(1);
        }

        RunSweep();

        int length;
        fixed (byte* output = _buffer)
            length = HistoryWriter.Write(FrameEngine.Rollups.Head, Census.Rows.Head, Census.DetailSequence, output, _buffer.Length, from);

        HistoryBatch? batch = HistoryReader.Read(_buffer.Take(length).ToArray());
        Assert.NotNull(batch);

        HistoryGroup row = batch!.Groups.Single(group => group.Dimension == HistoryDimension.Grids);

        Assert.Equal(Grid, row.Key);
        Assert.Equal(1L, row.Representative);
        Assert.Equal(100U, row.MainUnits);
        Assert.Equal(from + batch.Groups.Count, batch.NextGroups);
        Assert.Equal(Sweep.Rows.Head, batch.GroupHead);
    }

    [Theory]
    [InlineData(GroupDimension.Grids, HistoryDimension.Grids)]
    [InlineData(GroupDimension.BlockTypes, HistoryDimension.BlockTypes)]
    [InlineData(GroupDimension.Definitions, HistoryDimension.Definitions)]
    [InlineData(GroupDimension.Owners, HistoryDimension.Owners)]
    [InlineData(GroupDimension.EntityTypes, HistoryDimension.EntityTypes)]
    [InlineData(GroupDimension.Session, HistoryDimension.Session)]
    [InlineData(GroupDimension.Logic, HistoryDimension.Logic)]
    [InlineData(GroupDimension.Callbacks, HistoryDimension.Callbacks)]
    [InlineData(GroupDimension.Scripts, HistoryDimension.Scripts)]
    [InlineData(GroupDimension.Mods, HistoryDimension.Mods)]
    [InlineData(GroupDimension.ModParts, HistoryDimension.ModParts)]
    [InlineData(GroupDimension.Rpcs, HistoryDimension.Rpcs)]
    [InlineData(GroupDimension.RpcsSent, HistoryDimension.RpcsSent)]
    [InlineData(GroupDimension.Factions, HistoryDimension.Factions)]
    public void Both_sides_number_the_lists_the_same(GroupDimension worker, HistoryDimension quasar)
    {
        Assert.Equal((uint)worker, (uint)quasar);
        Assert.Equal(worker.ToString(), quasar.ToString());
    }

    [Fact]
    public void A_group_row_has_the_size_the_reader_expects()
    {
        Assert.Equal(14 * sizeof(uint), sizeof(GroupRecord));
    }

    private struct OneFaction : ICensusSource
    {
        public int Snapshot() => 0;

        public void Visit(int index)
        {
        }

        public void Finish() => Census.NoteMember(Owner, 77L);
    }

    private struct SizedGrid : ICensusSource
    {
        public int Snapshot() => 1;

        public void Visit(int index) => Census.AddGrid(77L, 40, 5, gridId: Grid, ownerId: Owner);

        public void Finish() => Census.NoteMember(Owner, 77L);
    }

    private struct NoMembers : ICensusSource
    {
        public int Snapshot() => 0;

        public void Visit(int index)
        {
        }

        public void Finish()
        {
        }
    }

    private static void CensusPass<T>() where T : struct, ICensusSource
    {
        var world = default(T);
        Census.MakeDue();
        Census.Step(ref world, paused: false);
    }

    [Fact]
    public void Rows_say_how_big_their_grid_owner_and_faction_are()
    {
        CensusPass<SizedGrid>();
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        for (int frame = 0; frame < TurnFrames; frame++)
        {
            AddBlock(Site.EntityBefore, Phase.Before, 100L);
            Frames(1);
        }

        RunSweep();

        Assert.Equal(40U, Assert.Single(RowsSince(from, GroupDimension.Grids)).Blocks);

        GroupRecord owner = Assert.Single(RowsSince(from, GroupDimension.Owners));
        Assert.Equal(40U, owner.Blocks);
        Assert.Equal(1U, owner.Grids);

        GroupRecord faction = Assert.Single(RowsSince(from, GroupDimension.Factions));
        Assert.Equal(40U, faction.Blocks);
        Assert.Equal(1U, faction.Grids);

        Assert.Equal(0U, Assert.Single(RowsSince(from, GroupDimension.BlockTypes)).Blocks);
        Assert.Equal(Sweep.Rows.Head, Sweep.WholeRows);
    }

    [Fact]
    public void Owners_are_totalled_by_faction()
    {
        CensusPass<OneFaction>();
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        for (int frame = 0; frame < TurnFrames; frame++)
        {
            AddBlock(Site.EntityBefore, Phase.Before, 100L);
            Frames(1);
        }

        RunSweep();

        GroupRecord row = Assert.Single(RowsSince(from, GroupDimension.Factions));
        Assert.Equal(77L, row.Key);
        Assert.Equal(100U, row.MainUnits);
    }

    [Fact]
    public void An_owner_in_no_faction_goes_in_its_own_row()
    {
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        Recorder.AddEntity(Site.EntityBefore, Phase.Before, 9L, Raw(100L), 0)->GridId = Grid;
        Frames(TurnFrames);
        RunSweep();

        Assert.Equal(Census.NoFaction, Assert.Single(RowsSince(from, GroupDimension.Factions)).Key);
    }

    [Fact]
    public void Someone_who_left_a_faction_stops_counting_for_it()
    {
        CensusPass<OneFaction>();
        Assert.Equal(77L, Census.FactionOf(Owner));

        CensusPass<NoMembers>();
        CensusPass<NoMembers>();

        Assert.Equal(Census.NoFaction, Census.FactionOf(Owner));
    }

    private const long ModOne = 7L;
    private const long PartA = 88L;
    private const long PartB = 89L;
    private const long GameCode = 90L;

    private static void AddKeyed(int site, long key, long units) =>
        Recorder.AddKey(site, SiteKeys.Pack(key, site), Raw(units), 0);

    [Fact]
    public void Code_is_totalled_by_whose_it_is()
    {
        Sweep.OwnerOf = key => key == GameCode ? 0L : ModOne;
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        AddKeyed(Site.SessionBefore, PartA, 1000L);
        AddKeyed(Site.GameLogic, PartB, 500L);
        Frames(TurnFrames);
        RunSweep();

        GroupRecord mod = Assert.Single(RowsSince(from, GroupDimension.Mods));
        Assert.Equal(ModOne, mod.Key);
        Assert.Equal(150U, mod.MainUnits);
    }

    [Fact]
    public void A_mods_parts_stay_apart_and_say_whose_they_are()
    {
        Sweep.OwnerOf = key => ModOne;
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        AddKeyed(Site.SessionBefore, PartA, 1000L);
        AddKeyed(Site.GameLogic, PartB, 500L);
        Frames(TurnFrames);
        RunSweep();

        List<GroupRecord> parts = RowsSince(from, GroupDimension.ModParts);

        Assert.Equal(2, parts.Count);
        Assert.Equal(PartA, parts[0].Key);
        Assert.Equal(100U, parts[0].MainUnits);
        Assert.All(parts, part => Assert.Equal(ModOne, part.Representative));
    }

    [Theory]
    [InlineData(Site.SessionBefore)]
    [InlineData(Site.GameLogic)]
    [InlineData(Site.GridCallback)]
    [InlineData(Site.PluginUpdate)]
    public void Each_kind_of_code_counts_towards_its_mod(int site)
    {
        Sweep.OwnerOf = key => ModOne;
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        AddKeyed(site, PartA, 1000L);
        Frames(TurnFrames);
        RunSweep();

        Assert.Equal(100U, Assert.Single(RowsSince(from, GroupDimension.Mods)).MainUnits);
    }

    [Fact]
    public void The_games_own_code_isnt_a_mod()
    {
        Sweep.OwnerOf = key => 0L;
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        AddKeyed(Site.SessionBefore, GameCode, 1000L);
        Frames(TurnFrames);
        RunSweep();

        Assert.Empty(RowsSince(from, GroupDimension.Mods));
        Assert.Empty(RowsSince(from, GroupDimension.ModParts));
        Assert.Single(RowsSince(from, GroupDimension.Session));
    }

    [Fact]
    public void Nothing_says_whose_code_it_is_so_there_are_no_mod_rows()
    {
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        AddKeyed(Site.SessionBefore, PartA, 1000L);
        Frames(TurnFrames);
        RunSweep();

        Assert.Empty(RowsSince(from, GroupDimension.Mods));
    }

    [Fact]
    public void Every_mod_gets_a_line_up_to_more_than_the_other_lists_keep()
    {
        Sweep.OwnerOf = key => key;
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        for (long key = 1L; key <= Sweep.ModRows + 10L; key++)
            AddKeyed(Site.SessionBefore, key, key * 10L);

        Frames(TurnFrames);
        RunSweep();

        List<GroupRecord> rows = RowsSince(from, GroupDimension.Mods);

        Assert.True(Sweep.ModRows > Sweep.RowsPerDimension);
        Assert.Equal(Sweep.ModRows, rows.Count);
        Assert.Equal(Sweep.ModRows + 10L, rows[0].Key);
    }

    [Fact]
    public void An_hour_of_full_lists_fits_in_the_ring()
    {
        // Twelve lists of 20, one of 40 and one of 80. A Quasar restart has to find its backlog still there.
        long rowsPerSweep = (12L * Sweep.RowsPerDimension) + Sweep.ModRows + Sweep.ModPartRows;
        long sweepsPerHour = 3600L / Sweep.PeriodSeconds;

        Assert.True(Sweep.RingRows >= rowsPerSweep * sweepsPerHour);
    }

    [Fact]
    public void Mods_total_without_garbage()
    {
        Sweep.OwnerOf = key => ModOne;
        DetailController.SetMode(DetailMode.Everything);
        Frames(TurnFrames);
        RunSweep();

        long bytes = Allocation.Measure(() =>
        {
            for (int round = 0; round < 20; round++)
            {
                AddKeyed(Site.SessionBefore, PartA, 1000L);
                Frames(TurnFrames);
                RunSweep();
            }
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Mods_can_be_asked_for_by_name()
    {
        Assert.Equal(GroupDimension.Mods, GroupDimensions.Named("mods"));
        Assert.Equal(GroupDimension.Mods, GroupDimensions.Named("MODS"));
    }

    [Fact]
    public void Rpcs_can_be_asked_for_by_name()
    {
        Assert.Equal(GroupDimension.Rpcs, GroupDimensions.Named("rpcs"));
    }

    [Fact]
    public void An_rpc_is_totalled_with_other_rpcs()
    {
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        AddKeyed(Site.Rpc, PartA, 1000L);
        Frames(TurnFrames);
        RunSweep();

        GroupRecord row = Assert.Single(RowsSince(from, GroupDimension.Rpcs));

        Assert.Equal(PartA, row.Key);
        Assert.Equal(100U, row.MainUnits);
    }

    [Fact]
    public void Rpcs_sent_can_be_asked_for_by_name()
    {
        Assert.Equal(GroupDimension.RpcsSent, GroupDimensions.Named("rpcssent"));
    }

    [Fact]
    public void Rpcs_sent_are_ranked_by_bytes_and_hold_no_time()
    {
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        NetTraffic.SentRpc(PartA, 1000L);
        NetTraffic.SentRpc(PartB, 9000L);
        NetTraffic.SentRpc(PartB, 9000L);
        Frames(TurnFrames);
        RunSweep();

        List<GroupRecord> rows = RowsSince(from, GroupDimension.RpcsSent);

        Assert.Equal(2, rows.Count);
        Assert.Equal(PartB, rows[0].Key);
        Assert.Equal(0U, rows[0].MainUnits);
        Assert.True(rows[0].BytesPerSecond > rows[1].BytesPerSecond);
        Assert.Equal(20U, rows[0].CentiCallsPerFrame);
    }

    [Fact]
    public void A_list_of_time_has_no_bytes_left_over_from_an_old_row()
    {
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        AddBlock(Site.EntityBefore, Phase.Before, 100L);
        Frames(TurnFrames);
        RunSweep();

        Assert.Equal(0U, Assert.Single(RowsSince(from, GroupDimension.Grids)).BytesPerSecond);
    }

    [Fact]
    public void Calls_below_one_a_frame_arent_lost()
    {
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        AddKeyed(Site.Rpc, PartA, 100L);
        Frames(TurnFrames);
        RunSweep();

        Assert.Equal(10U, Assert.Single(RowsSince(from, GroupDimension.Rpcs)).CentiCallsPerFrame);
    }

    [Fact]
    public void Bytes_and_small_call_counts_survive_the_trip_to_quasar()
    {
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        NetTraffic.SentRpc(PartA, 5000L);
        Frames(TurnFrames);
        RunSweep();

        int length;
        fixed (byte* output = _buffer)
            length = HistoryWriter.Write(FrameEngine.Rollups.Head, Census.Rows.Head, Census.DetailSequence, output, _buffer.Length, from);

        HistoryBatch? batch = HistoryReader.Read(_buffer.Take(length).ToArray());
        Assert.NotNull(batch);

        HistoryGroup row = batch!.Groups.Single(group => group.Dimension == HistoryDimension.RpcsSent);

        Assert.True(row.BytesPerSecond > 0U);
        Assert.Equal(0.1f, row.CallsPerFrame, 3);
    }

    [Fact]
    public void An_rpc_isnt_counted_as_anyones_mod()
    {
        Sweep.OwnerOf = key => ModOne;
        DetailController.SetMode(DetailMode.Everything);
        long from = Sweep.Rows.Head;

        AddKeyed(Site.Rpc, PartA, 1000L);
        Frames(TurnFrames);
        RunSweep();

        Assert.Empty(RowsSince(from, GroupDimension.Mods));
    }

    [Fact]
    public void A_mods_parts_arent_a_list_of_their_own()
    {
        Assert.Equal(GroupDimension.None, GroupDimensions.Named("modparts"));
    }

    [Fact]
    public void Composite_logic_is_measured_with_other_logic_and_counted_as_nested()
    {
        Assert.Contains(SiteGroups.All, group => group.Name == "Logic and scripts" && group.Sites.Contains(Site.CompositeLogic));
        Assert.True(SiteGroups.IsNested(Site.CompositeLogic));
    }

    [Fact]
    public void Composite_parts_are_listed_with_game_logic()
    {
        DetailController.SetMode(DetailMode.Everything);
        Recorder.AddKey(Site.CompositeLogic, SiteKeys.Pack(88L, Site.CompositeLogic), Raw(100L), 0);
        long from = Sweep.Rows.Head;

        Frames(TurnFrames);
        RunSweep();

        Assert.Equal(88L, Assert.Single(RowsSince(from, GroupDimension.Logic)).Key);
    }

    [Fact]
    public void A_sweep_allocates_nothing()
    {
        DetailController.SetMode(DetailMode.Everything);
        Frames(TurnFrames);
        RunSweep();

        long bytes = Allocation.Measure(() =>
        {
            for (int round = 0; round < 20; round++)
            {
                Frames(TurnFrames);
                RunSweep();
            }
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Copying_the_top_matches_asking_for_it()
    {
        var table = new RollupTable(1 << 8);
        for (long key = 1L; key <= 50L; key++)
            table.Add(key, key * 10L, key, 1L, 0U, key);

        RollupRow* rows = stackalloc RollupRow[3];
        int count = table.CopyTop(rows, 3);

        Assert.Equal(3, count);
        Assert.Equal(table.Top(3).Select(row => row.Key), new[] { rows[0].Key, rows[1].Key, rows[2].Key });
    }
}
