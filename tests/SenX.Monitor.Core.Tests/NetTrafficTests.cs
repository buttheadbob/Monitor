using System.Linq;
using System.Threading;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class NetTrafficTests
{
    private const ulong Peer = 1001UL;
    private const ulong Other = 1002UL;

    private readonly byte[] _buffer = new byte[HistoryWriter.MaxBytes];

    public NetTrafficTests()
    {
        TestSetup.Reset();
        FrameEngine.Initialize();
        FrameEngine.Start();
    }

    private static RollupRecord Roll()
    {
        RollupRecord row = default;
        NetTraffic.Roll(&row);
        return row;
    }

    private static void Pass(params (ulong Peer, int Queued)[] players)
    {
        var world = new Players { Online = players };
        Census.MakeDue();
        Census.Step(ref world, paused: false);

        for (int i = 0; i < 1000 && Census.Walking; i++)
            Census.Step(ref world, paused: false);

        Assert.False(Census.Walking);
    }

    private struct Players : ICensusSource
    {
        public (ulong Peer, int Queued)[] Online;

        public int Snapshot() => 0;

        public void Visit(int index)
        {
        }

        public void Finish()
        {
            for (int i = 0; i < Online.Length; i++)
                Census.AddPlayer(100L + i, Census.NoFaction, 20, Online[i].Peer, Online[i].Queued);
        }
    }

    private struct Grids : ICensusSource
    {
        public int Snapshot() => 3;

        public void Visit(int index) => Census.AddGrid(Census.NoFaction, 100 + index, 10, concealed: index != 0);

        public void Finish()
        {
        }
    }

    private static CensusPlayer[] Detail()
    {
        var players = new CensusPlayer[Census.MaxPlayers];
        var factions = new CensusFaction[Census.MaxFactions];
        int count;

        fixed (CensusPlayer* p = players)
        fixed (CensusFaction* f = factions)
        {
            Assert.True(Census.TryCopyDetail(p, f, out count, out int _, out int _, out long _));
        }

        return players.Take(count).ToArray();
    }

    [Fact]
    public void Each_peer_has_its_own_totals()
    {
        NetTraffic.Sent(Peer, 1000, NetKind.StateSync, accepted: true);
        NetTraffic.Sent(Other, 300, NetKind.StateSync, accepted: true);
        NetTraffic.Received(Peer, 50U);

        PeerDelta first = NetTraffic.TakeDelta(Peer);
        PeerDelta second = NetTraffic.TakeDelta(Other);

        Assert.Equal(1000L, first.SentBytes);
        Assert.Equal(50L, first.ReceivedBytes);
        Assert.Equal(300L, second.SentBytes);
        Assert.Equal(0L, second.ReceivedBytes);
    }

    [Fact]
    public void A_delta_is_only_whats_new()
    {
        NetTraffic.Sent(Peer, 1000, NetKind.Other, accepted: true);
        NetTraffic.TakeDelta(Peer);

        NetTraffic.Sent(Peer, 200, NetKind.Other, accepted: true);

        Assert.Equal(200L, NetTraffic.TakeDelta(Peer).SentBytes);
        Assert.Equal(0L, NetTraffic.TakeDelta(Peer).SentBytes);
    }

    [Fact]
    public void A_refused_packet_is_counted_as_refused_and_not_as_sent()
    {
        NetTraffic.Sent(Peer, 1000, NetKind.StateSync, accepted: false);

        PeerDelta delta = NetTraffic.TakeDelta(Peer);

        Assert.Equal(0L, delta.SentBytes);
        Assert.Equal(1L, delta.FailedSends);
        Assert.Equal(1L, NetTraffic.FailedSends);
        Assert.Equal(0U, Roll().SentKb);
    }

    [Fact]
    public void An_rpc_counts_for_its_peer()
    {
        NetTraffic.Received(Peer, 50U);
        NetTraffic.NoteRpc(Peer);
        NetTraffic.NoteRpc(Peer);

        Assert.Equal(2L, NetTraffic.TakeDelta(Peer).Rpcs);
        Assert.Equal(2U, Roll().RpcsIn);
    }

    [Fact]
    public void An_rpc_from_nobody_counts_in_the_total_and_leaves_no_mark_on_a_free_slot()
    {
        NetTraffic.NoteRpc(0UL);

        Assert.Equal(1U, Roll().RpcsIn);

        // A zero key matches an empty slot, so anything written there would go to whoever claims it next.
        NetTraffic.Received(Peer, 50U);

        Assert.Equal(0L, NetTraffic.TakeDelta(Peer).Rpcs);
    }

    [Fact]
    public void A_peer_it_has_never_seen_has_done_nothing()
    {
        Assert.Equal(default, NetTraffic.TakeDelta(Peer));
        Assert.Equal(default, NetTraffic.TakeDelta(0UL));
    }

    [Fact]
    public void A_second_of_history_splits_what_was_sent_by_kind()
    {
        NetTraffic.Sent(Peer, 3 * 1024, NetKind.StateSync, accepted: true);
        NetTraffic.Sent(Peer, 2 * 1024, NetKind.Events, accepted: true);
        NetTraffic.Sent(Peer, 4 * 1024, NetKind.Replication, accepted: true);
        NetTraffic.Sent(Peer, 1 * 1024, NetKind.Other, accepted: true);
        NetTraffic.Received(Peer, 5 * 1024U);

        RollupRecord row = Roll();

        Assert.Equal(10U, row.SentKb);
        Assert.Equal(3U, row.StateSyncKb);
        Assert.Equal(2U, row.EventsKb);
        Assert.Equal(4U, row.ReplicationKb);
        Assert.Equal(5U, row.ReceivedKb);
    }

    [Fact]
    public void A_second_only_holds_that_second()
    {
        NetTraffic.Sent(Peer, 2048, NetKind.Other, accepted: true);
        Roll();

        Assert.Equal(0U, Roll().SentKb);
    }

    [Fact]
    public void Small_packets_add_up_over_seconds_and_nothing_is_lost_to_rounding()
    {
        uint total = 0U;

        for (int second = 0; second < 2048; second++)
        {
            NetTraffic.Sent(Peer, 1, NetKind.Other, accepted: true);
            total += Roll().SentKb;
        }

        Assert.Equal(2U, total);
    }

    [Fact]
    public void Traffic_during_a_pause_doesnt_land_in_one_second_after_it()
    {
        NetTraffic.Sent(Peer, 1024 * 1024, NetKind.Other, accepted: true);
        NetTraffic.Received(Peer, 1024U * 1024U);
        NetTraffic.NoteRpc(Peer);

        NetTraffic.Skip();
        RollupRecord row = Roll();

        Assert.Equal(0U, row.SentKb);
        Assert.Equal(0U, row.ReceivedKb);
        Assert.Equal(0U, row.RpcsIn);
    }

    [Fact]
    public void Past_the_table_a_peer_loses_its_row_and_the_totals_stay_right()
    {
        long bytes = 0L;

        for (ulong peer = 1UL; peer <= NetTraffic.PeerSlots + 200UL; peer++)
        {
            NetTraffic.Sent(peer, 1024, NetKind.Other, accepted: true);
            bytes += 1024L;
        }

        Assert.Equal(bytes / 1024L, Roll().SentKb);
    }

    [Fact]
    public void A_pass_gives_each_player_their_rates()
    {
        Pass((Peer, 0));

        NetTraffic.Sent(Peer, 3000, NetKind.StateSync, accepted: true);
        NetTraffic.Received(Peer, 1500U);

        Census.SetLastBeginForTests(Clock.Now() - (3L * Clock.Frequency));
        Pass((Peer, 700));

        CensusPlayer player = Assert.Single(Detail());

        Assert.InRange(player.SentBytesPerSecond, 950U, 1050U);
        Assert.InRange(player.ReceivedBytesPerSecond, 475U, 525U);
        Assert.Equal(700U, player.QueuedBytes);
    }

    [Fact]
    public void Someone_with_no_peer_still_gets_a_row()
    {
        Pass((0UL, 0));

        Assert.Equal(0U, Assert.Single(Detail()).SentBytesPerSecond);
    }

    [Fact]
    public void A_negative_queue_reads_as_nothing_waiting()
    {
        Pass((Peer, -1));

        Assert.Equal(0U, Assert.Single(Detail()).QueuedBytes);
    }

    [Fact]
    public void Concealed_grids_are_counted_and_stay_in_the_totals()
    {
        var world = default(Grids);
        Census.MakeDue();
        Census.Step(ref world, paused: false);

        CensusRecord row = default;
        Assert.True(Census.Rows.TryCopy(Census.Rows.Head - 1L, (byte*)&row));

        Assert.Equal(3U, row.Grids);
        Assert.Equal(303U, row.Blocks);
        Assert.Equal(2U, row.ConcealedGrids);
        Assert.Equal(203U, row.ConcealedBlocks);
    }

    [Fact]
    public void Network_figures_survive_the_trip_to_quasar()
    {
        // A simulated frame, because a paused server keeps no rows and an earlier test may have left it paused.
        TreeCounters.Calls[Scope.Components] = 1L;
        FrameEngine.OnFrameEnd(Clock.Now());

        long rollups = FrameEngine.Rollups.Head;
        long census = Census.Rows.Head;

        NetTraffic.Sent(Peer, 8 * 1024, NetKind.StateSync, accepted: true);
        NetTraffic.Sent(Peer, 4 * 1024, NetKind.Events, accepted: true);
        NetTraffic.Sent(Peer, 2 * 1024, NetKind.Replication, accepted: true);
        NetTraffic.Received(Peer, 3 * 1024U);
        NetTraffic.NoteRpc(Peer);
        FrameEngine.CommitSecondNow();

        var grids = default(Grids);
        Census.MakeDue();
        Census.Step(ref grids, paused: false);

        Pass((Peer, 700));

        int length;
        fixed (byte* output = _buffer)
            length = HistoryWriter.Write(rollups, census, 0L, output, _buffer.Length);

        HistoryBatch? batch = HistoryReader.Read(_buffer.Take(length).ToArray());
        Assert.NotNull(batch);

        HistoryRollup row = batch!.Rollups.Single();

        Assert.Equal(14U, row.SentKb);
        Assert.Equal(3U, row.ReceivedKb);
        Assert.Equal(8U, row.StateSyncKb);
        Assert.Equal(4U, row.EventsKb);
        Assert.Equal(2U, row.ReplicationKb);
        Assert.Equal(1U, row.RpcsIn);
        Assert.Equal(Scope.Count, row.ScopeUnits.Length);

        Assert.Equal(2U, batch.Census[0].ConcealedGrids);
        Assert.Equal(203U, batch.Census[0].ConcealedBlocks);

        HistoryPlayer player = Assert.Single(batch.Players);

        Assert.Equal(700U, player.QueuedBytes);
        Assert.True(player.SentBytesPerSecond > 0U);
    }

    private const long Rpc = 7000L;
    private const long OtherRpc = 7001L;

    private static RollupRow[] Sent()
    {
        var table = new RollupTable(1 << 12);
        NetTraffic.TakeSentRpcs(table);
        return table.Top(NetTraffic.RpcSlots).ToArray();
    }

    [Fact]
    public void Only_the_first_of_an_rpc_says_its_new()
    {
        Assert.True(NetTraffic.SentRpc(Rpc, 10L));
        Assert.False(NetTraffic.SentRpc(Rpc, 10L));
        Assert.True(NetTraffic.SentRpc(OtherRpc, 10L));
    }

    [Fact]
    public void An_rpcs_bytes_and_events_are_added_up()
    {
        NetTraffic.SentRpc(Rpc, 100L);
        NetTraffic.SentRpc(Rpc, 50L);

        RollupRow row = Assert.Single(Sent());

        Assert.Equal(Rpc, row.Key);
        Assert.Equal(150L, row.MainUnits);
        Assert.Equal(2L, row.Calls);
    }

    [Fact]
    public void A_second_total_only_holds_whats_new()
    {
        NetTraffic.SentRpc(Rpc, 100L);
        Sent();

        Assert.Empty(Sent());

        NetTraffic.SentRpc(Rpc, 30L);

        RollupRow row = Assert.Single(Sent());

        Assert.Equal(30L, row.MainUnits);
        Assert.Equal(1L, row.Calls);
    }

    [Fact]
    public void Starting_a_new_stretch_lets_go_of_what_came_before()
    {
        NetTraffic.SentRpc(Rpc, 100L);

        NetTraffic.TakeSentRpcs(null);

        Assert.Empty(Sent());
    }

    [Fact]
    public void A_total_says_how_long_it_covers()
    {
        NetTraffic.TakeSentRpcs(null);
        Thread.Sleep(20);

        double seconds = NetTraffic.TakeSentRpcs(null);

        Assert.InRange(seconds, 0.01d, 5d);
    }

    [Fact]
    public void Threads_sending_the_same_rpc_lose_nothing()
    {
        // The game raises events from any thread, so this is the one table with many writers.
        const int Threads = 4;
        const int Each = 50000;

        int created = 0;

        Thread[] workers = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
        {
            for (int i = 0; i < Each; i++)
            {
                if (NetTraffic.SentRpc(Rpc, 3L))
                    Interlocked.Increment(ref created);
            }
        })).ToArray();

        foreach (Thread worker in workers)
            worker.Start();

        foreach (Thread worker in workers)
            worker.Join();

        RollupRow row = Assert.Single(Sent());

        Assert.Equal(1, created);
        Assert.Equal(Threads * Each * 3L, row.MainUnits);
        Assert.Equal((long)(Threads * Each), row.Calls);
    }

    [Fact]
    public void Threads_racing_for_new_rpcs_each_get_one_slot()
    {
        const int Threads = 4;
        const int Keys = 400;

        int created = 0;

        Thread[] workers = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
        {
            for (long key = 1L; key <= Keys; key++)
            {
                if (NetTraffic.SentRpc(key, 1L))
                    Interlocked.Increment(ref created);
            }
        })).ToArray();

        foreach (Thread worker in workers)
            worker.Start();

        foreach (Thread worker in workers)
            worker.Join();

        RollupRow[] rows = Sent();

        Assert.Equal(Keys, created);
        Assert.Equal(Keys, rows.Length);
        Assert.All(rows, row => Assert.Equal((long)Threads, row.MainUnits));
    }

    [Fact]
    public void Past_the_table_a_new_rpc_is_dropped_and_nothing_breaks()
    {
        for (long key = 1L; key <= NetTraffic.RpcSlots + 50L; key++)
            NetTraffic.SentRpc(key, 1L);

        Assert.InRange(Sent().Length, 1, NetTraffic.RpcSlots);
    }

    [Fact]
    public void Counting_sent_rpcs_allocates_nothing()
    {
        var table = new RollupTable(1 << 12);
        NetTraffic.SentRpc(Rpc, 1L);
        NetTraffic.TakeSentRpcs(table);

        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < 10000; i++)
                NetTraffic.SentRpc(Rpc, 100L);

            NetTraffic.TakeSentRpcs(table);
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Counting_allocates_nothing()
    {
        NetTraffic.Sent(Peer, 1, NetKind.Other, accepted: true);

        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < 10000; i++)
            {
                NetTraffic.Sent(Peer, 100, NetKind.StateSync, accepted: true);
                NetTraffic.Sent(Peer, 100, NetKind.StateSync, accepted: false);
                NetTraffic.Received(Peer, 100U);
                NetTraffic.NoteRpc(Peer);
            }

            NetTraffic.TakeDelta(Peer);
            Roll();
        });

        Assert.Equal(0L, bytes);
    }
}
