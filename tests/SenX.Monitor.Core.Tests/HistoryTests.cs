using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class HistoryTests
{
    private readonly byte[] _buffer = new byte[HistoryWriter.MaxBytes];

    public HistoryTests()
    {
        TestSetup.Reset();
        FrameEngine.Initialize();
        FrameEngine.Start();
    }

    private byte[] Write(long fromRollup, long fromCensus, long fromDetail)
    {
        fixed (byte* output = _buffer)
        {
            // From "now", so rows left in the ring by other tests can't fill the batch.
            int length = HistoryWriter.Write(fromRollup, fromCensus, fromDetail, output, _buffer.Length, Sweep.Rows.Head);
            return _buffer.Take(length).ToArray();
        }
    }

    // Starting from "now" by default, so rows left in the rings by other tests can't fill the batch.
    private HistoryBatch Pull(long fromRollup = -1L, long fromCensus = -1L, long fromDetail = 0L)
    {
        long rollup = fromRollup < 0L ? FrameEngine.Rollups.Head : fromRollup;
        long census = fromCensus < 0L ? Census.Rows.Head : fromCensus;

        HistoryBatch? batch = HistoryReader.Read(Write(rollup, census, fromDetail));
        Assert.NotNull(batch);
        return batch!;
    }

    private static void Seconds(int count, int simulatedFrames = 1)
    {
        for (int second = 0; second < count; second++)
        {
            for (int frame = 0; frame < simulatedFrames; frame++)
            {
                TreeCounters.Calls[Scope.Components] = 1L;
                TreeCounters.Add(Scope.Physics, Clock.Frequency / 1000L);
                FrameEngine.OnFrameEnd(Clock.Now());
            }

            FrameEngine.CommitSecondNow();
        }
    }

    private struct World : ICensusSource
    {
        public static int Players;
        public static int Factions;

        public int Snapshot() => 4;

        public void Visit(int index) => Census.AddGrid(index + 1L, 10, 5);

        public void Finish()
        {
            for (int i = 1; i <= Factions; i++)
                Census.AddGrid(1000L + i, 1, 1);

            for (int i = 1; i <= Players; i++)
                Census.AddPlayer(5000L + i, i, 25);
        }
    }

    private static void CensusPass(int players = 2, int factions = 0)
    {
        World.Players = players;
        World.Factions = factions;

        var world = default(World);
        Census.MakeDue();
        Census.Step(ref world, paused: false);
    }

    [Fact]
    public void The_header_size_matches_the_reader()
    {
        Assert.Equal(HistoryReader.HeaderBytes, HistoryWriter.HeaderBytes);
    }

    // The reader names columns by position, so moving a field must fail here and not corrupt charts.
    [Theory]
    [InlineData("UnitsSinceStart", 0)]
    [InlineData("Frames", 2)]
    [InlineData("SimulatedFrames", 3)]
    [InlineData("WindowUnits", 4)]
    [InlineData("FrameP50", 5)]
    [InlineData("FrameP95", 6)]
    [InlineData("FrameP99", 7)]
    [InlineData("FrameMax", 8)]
    [InlineData("Gen0", 9)]
    [InlineData("Gen1", 10)]
    [InlineData("Gen2", 11)]
    [InlineData("AllocatedKb", 12)]
    [InlineData("Flags", 13)]
    [InlineData("RpcsIn", 19)]
    [InlineData("ScopeUnits", 20)]
    public void Rollup_fields_are_in_the_columns_the_reader_expects(string field, int column)
    {
        Assert.Equal(column * sizeof(uint), (int)Marshal.OffsetOf<RollupRecord>(field));
    }

    [Theory]
    [InlineData("UnitsSinceStart", 0)]
    [InlineData("Entities", 2)]
    [InlineData("Grids", 3)]
    [InlineData("Blocks", 4)]
    [InlineData("Pcu", 5)]
    [InlineData("Characters", 6)]
    [InlineData("FloatingObjects", 7)]
    [InlineData("Voxels", 8)]
    [InlineData("OnlinePlayers", 9)]
    [InlineData("Factions", 10)]
    [InlineData("PingMeanTenths", 11)]
    [InlineData("PingWorst", 12)]
    [InlineData("PassUnits", 13)]
    [InlineData("WorkingSetMb", 14)]
    [InlineData("ManagedMb", 15)]
    [InlineData("ConcealedGrids", 16)]
    [InlineData("ConcealedBlocks", 17)]
    [InlineData("StaticGrids", 18)]
    [InlineData("Asteroids", 19)]
    [InlineData("Planets", 20)]
    [InlineData("CpuTenths", 21)]
    public void Census_fields_are_in_the_columns_the_reader_expects(string field, int column)
    {
        Assert.Equal(column * sizeof(uint), (int)Marshal.OffsetOf<CensusRecord>(field));
    }

    [Theory]
    [InlineData("BytesPerSecond", 11)]
    [InlineData("Blocks", 12)]
    [InlineData("Grids", 13)]
    public void Group_fields_are_in_the_columns_the_reader_expects(string field, int column)
    {
        Assert.Equal(column * sizeof(uint), (int)Marshal.OffsetOf<GroupRecord>(field));
    }

    [Fact]
    public void A_total_still_being_written_isnt_sent()
    {
        long whole = Sweep.WholeRows;

        *(GroupRecord*)Sweep.Rows.Reserve() = default;
        Sweep.Rows.Commit();

        fixed (byte* output = _buffer)
        {
            int length = HistoryWriter.Write(FrameEngine.Rollups.Head, Census.Rows.Head, 0L, output, _buffer.Length, whole);
            HistoryBatch batch = HistoryReader.Read(_buffer.Take(length).ToArray())!;

            Assert.Empty(batch.Groups);
            Assert.Equal(whole, batch.GroupHead);
        }
    }

    [Theory]
    [InlineData(HistoryScopes.Frame, Scope.Frame)]
    [InlineData(HistoryScopes.Update, Scope.Update)]
    [InlineData(HistoryScopes.Network, Scope.Network)]
    [InlineData(HistoryScopes.Plugins, Scope.Plugins)]
    [InlineData(HistoryScopes.Session, Scope.Session)]
    [InlineData(HistoryScopes.RunCallbacks, Scope.RunCallbacks)]
    [InlineData(HistoryScopes.ReplicationBefore, Scope.ReplicationBefore)]
    [InlineData(HistoryScopes.Components, Scope.Components)]
    [InlineData(HistoryScopes.Gps, Scope.Gps)]
    [InlineData(HistoryScopes.ReplicationSend, Scope.ReplicationSend)]
    [InlineData(HistoryScopes.Physics, Scope.Physics)]
    [InlineData(HistoryScopes.EntitiesBefore, Scope.EntitiesBefore)]
    [InlineData(HistoryScopes.EntitiesAfter, Scope.EntitiesAfter)]
    [InlineData(HistoryScopes.EntitiesSimulate, Scope.EntitiesSimulate)]
    [InlineData(HistoryScopes.EntitiesOnce, Scope.EntitiesOnce)]
    [InlineData(HistoryScopes.Monitor, Scope.Monitor)]
    public void Chart_scope_ids_match_the_worker(int chart, int worker)
    {
        Assert.Equal(worker, chart);
    }

    [Fact]
    public void Player_and_faction_rows_are_the_size_the_reader_expects()
    {
        Assert.Equal(10 * sizeof(uint), sizeof(CensusPlayer));
        Assert.Equal(6 * sizeof(uint), sizeof(CensusFaction));
        Assert.Equal(4 * sizeof(uint), (int)Marshal.OffsetOf<CensusPlayer>("PingMs"));
        Assert.Equal(2 * sizeof(uint), (int)Marshal.OffsetOf<CensusFaction>("Grids"));
        Assert.Equal(5 * sizeof(uint), (int)Marshal.OffsetOf<CensusFaction>("OnlineMembers"));
    }

    [Fact]
    public void Every_rollup_field_survives_the_trip()
    {
        long from = FrameEngine.Rollups.Head;
        Seconds(3, simulatedFrames: 2);

        RollupRecord expected = default;
        Assert.True(FrameEngine.Rollups.TryCopy(from + 2L, (byte*)&expected));

        HistoryBatch batch = Pull(from);
        Assert.Equal(3, batch.Rollups.Count);

        HistoryRollup row = batch.Rollups[2];

        Assert.Equal(from + 2L, row.Sequence);
        Assert.Equal(expected.UnitsSinceStart, row.UnitsSinceStart);
        Assert.Equal(expected.Frames, row.Frames);
        Assert.Equal(expected.SimulatedFrames, row.SimulatedFrames);
        Assert.Equal(expected.WindowUnits, row.WindowUnits);
        Assert.Equal(expected.FrameP50, row.FrameP50);
        Assert.Equal(expected.FrameP95, row.FrameP95);
        Assert.Equal(expected.FrameP99, row.FrameP99);
        Assert.Equal(expected.FrameMax, row.FrameMax);
        Assert.Equal(expected.Gen0, row.Gen0);
        Assert.Equal(expected.Gen1, row.Gen1);
        Assert.Equal(expected.Gen2, row.Gen2);
        Assert.Equal(expected.AllocatedKb, row.AllocatedKb);
        Assert.Equal(expected.SentKb, row.SentKb);
        Assert.Equal(expected.ReceivedKb, row.ReceivedKb);
        Assert.Equal(expected.RpcsIn, row.RpcsIn);
        Assert.Equal(Scope.Count, row.ScopeUnits.Length);

        for (int scope = 0; scope < Scope.Count; scope++)
            Assert.Equal(expected.ScopeUnits[scope], row.ScopeUnits[scope]);

        Assert.True(row.ScopeUnits[Scope.Physics] > 0U);
        Assert.Equal(expected.SimSpeed, row.SimSpeed, 9);
    }

    [Fact]
    public void Every_census_field_survives_the_trip()
    {
        long from = Census.Rows.Head;
        CensusPass();

        CensusRecord expected = default;
        Assert.True(Census.Rows.TryCopy(from, (byte*)&expected));

        HistoryCensus row = Pull(fromCensus: from).Census.Single();

        Assert.Equal(from, row.Sequence);
        Assert.Equal(expected.UnitsSinceStart, row.UnitsSinceStart);
        Assert.Equal(expected.Entities, row.Entities);
        Assert.Equal(4U, row.Grids);
        Assert.Equal(40U, row.Blocks);
        Assert.Equal(20U, row.Pcu);
        Assert.Equal(2U, row.OnlinePlayers);
        Assert.Equal(25d, row.PingMean, 9);
        Assert.Equal(25U, row.PingWorst);
        Assert.Equal(expected.PassUnits, row.PassUnits);
    }

    [Fact]
    public void Players_and_factions_survive_the_trip()
    {
        CensusPass(players: 2);

        HistoryBatch batch = Pull();

        Assert.True(batch.HasDetail);
        Assert.Contains(batch.Players, p => p.IdentityId == 5001L && p.FactionId == 1L && p.PingMs == 25U);
        Assert.Contains(batch.Factions, f => f.FactionId == 1L && f.Grids == 1U && f.Blocks == 10U && f.Pcu == 5U && f.OnlineMembers == 1U);
    }

    [Fact]
    public void Players_are_only_sent_once_per_pass()
    {
        CensusPass();

        HistoryBatch first = Pull();
        HistoryBatch second = Pull(fromDetail: first.DetailSequence);

        Assert.True(first.HasDetail);
        Assert.False(second.HasDetail);
        Assert.Equal(first.DetailSequence, second.DetailSequence);
    }

    [Fact]
    public void A_pull_picks_up_exactly_where_the_last_one_stopped()
    {
        long from = FrameEngine.Rollups.Head;
        Seconds(5);

        HistoryBatch first = Pull(from);
        Seconds(3);
        HistoryBatch second = Pull(first.NextRollup);

        Assert.Equal(5, first.Rollups.Count);
        Assert.Equal(3, second.Rollups.Count);
        Assert.Equal(first.NextRollup, second.RollupFirst);
    }

    [Fact]
    public void Nothing_new_is_an_empty_batch_and_not_a_fault()
    {
        long from = FrameEngine.Rollups.Head;

        HistoryBatch batch = Pull(from, Census.Rows.Head, Census.DetailSequence);

        Assert.Empty(batch.Rollups);
        Assert.Empty(batch.Census);
        Assert.False(batch.HasDetail);
    }

    [Fact]
    public void A_long_backlog_comes_in_pieces_with_nothing_lost_or_repeated()
    {
        long from = FrameEngine.Rollups.Head;
        Seconds(1000, simulatedFrames: 3);

        var seen = new List<long>();
        long next = from;
        int pulls = 0;

        while (next < FrameEngine.Rollups.Head)
        {
            byte[] bytes = Write(next, Census.Rows.Head, Census.DetailSequence);
            Assert.True(bytes.Length <= HistoryWriter.MaxBytes);

            HistoryBatch batch = HistoryReader.Read(bytes)!;
            Assert.NotEmpty(batch.Rollups);

            seen.AddRange(batch.Rollups.Select(row => row.Sequence));
            next = batch.NextRollup;
            pulls++;

            // A pull that stops moving forward should fail here, not hang.
            Assert.True(pulls < 1000, "Still pulling after " + pulls + " batches.");
        }

        Assert.Equal(Enumerable.Range(0, 1000).Select(i => from + i), seen);
        Assert.True(pulls >= 1);
    }

    [Fact]
    public void A_batch_stays_under_the_large_object_heap_as_base64()
    {
        // JSON layers hold the string as UTF-16, so two bytes a character, plus quotes and escapes.
        int characters = (HistoryWriter.MaxBytes + 2) / 3 * 4;

        Assert.True(characters * 2 < 85_000, characters * 2 + " bytes of string.");
    }

    [Fact]
    public void A_reader_away_too_long_sees_the_gap()
    {
        Seconds(FrameEngine.RollupRows + 50);

        HistoryBatch batch = Pull(fromRollup: 0L);

        Assert.True(batch.RollupFirst > 0L);
        Assert.Equal(FrameEngine.Rollups.Oldest, batch.RollupFirst);
    }

    [Fact]
    public void Details_that_dont_fit_come_with_the_next_pull()
    {
        long from = FrameEngine.Rollups.Head;
        Seconds(1000, simulatedFrames: 3);
        CensusPass(players: Census.MaxPlayers, factions: Census.MaxFactions - 300);

        HistoryBatch full = Pull(from);
        Assert.False(full.HasDetail);
        Assert.Equal(0L, full.DetailSequence);

        HistoryBatch next = Pull(FrameEngine.Rollups.Head, Census.Rows.Head, full.DetailSequence);
        Assert.True(next.HasDetail);
        Assert.Equal(Census.MaxPlayers, next.Players.Count);
    }

    [Fact]
    public void A_paused_server_writes_no_rows_and_marks_the_gap()
    {
        Seconds(2);
        long head = FrameEngine.Rollups.Head;

        // Frames keep running, but nothing simulates.
        for (int frame = 0; frame < 40; frame++)
            FrameEngine.OnFrameEnd(Clock.Now());

        Assert.True(FrameEngine.Paused);

        for (int second = 0; second < 100; second++)
            FrameEngine.CommitSecondNow();

        Assert.Equal(head, FrameEngine.Rollups.Head);

        Seconds(2);

        List<HistoryRollup> rows = Pull(head).Rollups;
        Assert.True(rows[0].AfterGap);
        Assert.False(rows[1].AfterGap);
    }

    [Fact]
    public void Time_before_a_pause_is_not_lost_to_the_first_row_after_it()
    {
        Seconds(2);
        long head = FrameEngine.Rollups.Head;

        for (int frame = 0; frame < 40; frame++)
            FrameEngine.OnFrameEnd(Clock.Now());

        FrameEngine.CommitSecondNow();
        Seconds(1);

        HistoryRollup row = Pull(head).Rollups.Single();
        Assert.Equal(1U, row.SimulatedFrames);
        Assert.Equal(1U, row.Frames);
    }

    [Fact]
    public void A_restart_shows_as_a_new_start_time()
    {
        HistoryWriter.StartedUtcTicks = 1234L;
        Assert.Equal(1234L, Pull().StartedUtcTicks);

        HistoryWriter.StartedUtcTicks = 5678L;
        Assert.Equal(5678L, Pull().StartedUtcTicks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(95)]
    public void Too_short_to_be_a_batch_is_refused(int length)
    {
        Assert.Null(HistoryReader.Read(new byte[length]));
    }

    [Fact]
    public void A_cut_off_batch_is_refused_and_not_misread()
    {
        long from = FrameEngine.Rollups.Head;
        Seconds(5);
        byte[] whole = Write(from, 0L, 0L);

        Assert.NotNull(HistoryReader.Read(whole));
        Assert.Null(HistoryReader.Read(whole.Take(whole.Length - 3).ToArray()));
    }

    [Fact]
    public void Random_damage_never_crashes_the_reader()
    {
        long from = FrameEngine.Rollups.Head;
        Seconds(20);
        CensusPass();
        byte[] whole = Write(from, 0L, 0L);
        var random = new Random(12345);

        for (int round = 0; round < 2000; round++)
        {
            var damaged = (byte[])whole.Clone();
            for (int hit = 0; hit < 1 + (round % 8); hit++)
                damaged[random.Next(damaged.Length)] = (byte)random.Next(256);

            HistoryReader.Read(damaged);
        }
    }

    [Fact]
    public void A_different_format_is_refused()
    {
        byte[] whole = Write(0L, 0L, 0L);
        whole[0] ^= 0xFF;

        Assert.Null(HistoryReader.Read(whole));
    }

#if NET
    [Fact]
    public void The_channel_can_read_it_straight_into_bytes()
    {
        // Quasar's SendAsync<TRequest, byte[]> does exactly this to the payload.
        long from = FrameEngine.Rollups.Head;
        Seconds(3);
        byte[] bytes = Write(from, 0L, 0L);

        string payload = "\"" + Convert.ToBase64String(bytes) + "\"";
        byte[]? decoded = System.Text.Json.JsonSerializer.Deserialize<byte[]>(payload);

        Assert.Equal(bytes, decoded);
        Assert.Equal(3, HistoryReader.Read(decoded!)!.Rollups.Count);
    }
#endif

    [Fact]
    public void A_header_asking_for_any_number_of_columns_is_refused()
    {
        // With nothing in the batch, so no rows would be there to pay for the memory this asks for.
        byte[] data = Write(long.MaxValue, long.MaxValue, long.MaxValue);

        Assert.NotNull(HistoryReader.Read(data));

        BitConverter.GetBytes(1025).CopyTo(data, 4);

        Assert.Null(HistoryReader.Read(data));
    }
}
