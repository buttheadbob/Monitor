using System.Collections.Generic;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class CensusTests
{
    public CensusTests() => TestSetup.Reset();

    // A pretend world. Each entity is a grid of 10 blocks and 5 PCU unless Kinds says otherwise.
    private struct FakeWorld : ICensusSource
    {
        public static int Size = 8;
        public static int Visited;
        public static int Snapshots;
        public static long SpinTicksPerVisit;
        public static List<(long Identity, long Faction, int Ping)> Players = new();
        public static int FactionCount;

        public int Snapshot()
        {
            Snapshots++;
            return Size;
        }

        public void Visit(int index)
        {
            Visited++;

            if (SpinTicksPerVisit > 0L)
            {
                long until = Clock.Now() + SpinTicksPerVisit;
                while (Clock.Now() < until)
                {
                }
            }

            switch (index % 4)
            {
                case 0:
                    Census.AddGrid(100L, 10, 5);
                    break;
                case 1:
                    Census.AddGrid(Census.NoFaction, 20, 7);
                    break;
                case 2:
                    Census.AddCharacter();
                    break;
                default:
                    Census.AddVoxel();
                    Census.AddFloatingObject();
                    break;
            }
        }

        public void Finish()
        {
            foreach ((long identity, long faction, int ping) in Players)
                Census.AddPlayer(identity, faction, ping);

            Census.SetFactionCount(FactionCount);
        }

        public static void Reset()
        {
            Size = 8;
            Visited = 0;
            Snapshots = 0;
            SpinTicksPerVisit = 0L;
            Players = new List<(long, long, int)>();
            FactionCount = 0;
        }
    }

    // Two grids of one owner in one faction, one of them static, with an asteroid and a planet.
    private struct SizedWorld : ICensusSource
    {
        public static long CpuUnits;

        public int Snapshot() => 4;

        public void Visit(int index)
        {
            switch (index)
            {
                case 0:
                    Census.AddGrid(700L, 10, 5, gridId: 1001L, ownerId: 500L, isStatic: true);
                    break;
                case 1:
                    Census.AddGrid(700L, 30, 5, gridId: 1002L, ownerId: 500L);
                    break;
                case 2:
                    Census.AddAsteroid();
                    break;
                default:
                    Census.AddPlanet();
                    break;
            }
        }

        public void Finish() => Census.SetCpuTime(CpuUnits);
    }

    // Whose grid is whose, by the kinds of owner a row can have.
    private struct OwnedWorld : ICensusSource
    {
        public static bool WithSecondGrid;

        public int Snapshot() => WithSecondGrid ? 2 : 1;

        public void Visit(int index)
        {
            if (index == 0)
                Census.AddGrid(Census.NoFaction, 10, 5, gridId: 1001L, ownerId: 0L);
            else
                Census.AddGrid(Census.NoFaction, 30, 5, gridId: 1002L, ownerId: 500L);
        }

        public void Finish()
        {
        }
    }

    private static void RunOwnedPass()
    {
        var world = default(OwnedWorld);
        Census.MakeDue();
        Census.Step(ref world, paused: false);

        for (int i = 0; i < 100_000 && Census.Walking; i++)
            Census.Step(ref world, paused: false);
    }

    private static CensusRecord RunSizedPass()
    {
        var world = default(SizedWorld);
        Census.MakeDue();
        Census.Step(ref world, paused: false);

        for (int i = 0; i < 100_000 && Census.Walking; i++)
            Census.Step(ref world, paused: false);

        return Latest();
    }

    private static CensusRecord RunPass()
    {
        var world = default(FakeWorld);
        Census.MakeDue();
        Census.Step(ref world, paused: false);

        for (int i = 0; i < 100_000 && Census.Walking; i++)
            Census.Step(ref world, paused: false);

        Assert.False(Census.Walking);
        return Latest();
    }

    private static CensusRecord Latest()
    {
        CensusRecord row = default;
        Assert.True(Census.Rows.TryCopy(Census.Rows.Head - 1L, (byte*)&row));
        return row;
    }

    private static (CensusPlayer[] Players, CensusFaction[] Factions, int Dropped) Detail()
    {
        var players = new CensusPlayer[Census.MaxPlayers];
        var factions = new CensusFaction[Census.MaxFactions];

        fixed (CensusPlayer* p = players)
        fixed (CensusFaction* f = factions)
        {
            Assert.True(Census.TryCopyDetail(p, f, out int playerCount, out int factionCount, out int dropped, out _));
            System.Array.Resize(ref players, playerCount);
            System.Array.Resize(ref factions, factionCount);
            return (players, factions, dropped);
        }
    }

    [Fact]
    public void A_pass_says_what_kind_of_world_it_is()
    {
        CensusRecord row = RunSizedPass();

        Assert.Equal(2U, row.Grids);
        Assert.Equal(1U, row.StaticGrids);
        Assert.Equal(2U, row.Voxels);
        Assert.Equal(1U, row.Asteroids);
        Assert.Equal(1U, row.Planets);
    }

    [Fact]
    public void Grids_owners_and_factions_can_be_sized_from_any_thread()
    {
        RunSizedPass();

        Assert.Equal(30U, Census.CountOf(1002L).Blocks);
        Assert.Equal(0U, Census.CountOf(1002L).Grids);

        Assert.Equal(40U, Census.CountOf(500L).Blocks);
        Assert.Equal(2U, Census.CountOf(500L).Grids);

        Assert.Equal(40U, Census.CountOf(700L).Blocks);
        Assert.Equal(2U, Census.CountOf(700L).Grids);

        Assert.Equal(0U, Census.CountOf(12345L).Blocks);
    }

    [Fact]
    public void Sizes_are_from_the_last_pass_and_dont_pile_up()
    {
        RunSizedPass();
        RunSizedPass();

        Assert.Equal(2U, Census.CountOf(500L).Grids);
    }

    [Fact]
    public void Cpu_needs_two_passes_and_is_a_share_of_the_machine()
    {
        SizedWorld.CpuUnits = 1000L;
        Assert.Equal(0U, RunSizedPass().CpuTenths);

        SizedWorld.CpuUnits = 1000L + (long.MaxValue / 1024L);
        Assert.Equal(1000U, RunSizedPass().CpuTenths);
    }

    [Fact]
    public void Nobody_and_no_faction_are_counted_apart()
    {
        OwnedWorld.WithSecondGrid = true;
        RunOwnedPass();

        CensusCount nobody = Census.CountOfOwner(RollupTable.NoKey);
        Assert.Equal(1U, nobody.Grids);
        Assert.Equal(10U, nobody.Blocks);

        CensusCount noFaction = Census.CountOf(Census.NoFaction);
        Assert.Equal(2U, noFaction.Grids);
        Assert.Equal(40U, noFaction.Blocks);
    }

    [Fact]
    public void A_grid_thats_gone_is_forgotten_once_both_tables_have_turned_over()
    {
        OwnedWorld.WithSecondGrid = true;
        RunOwnedPass();

        OwnedWorld.WithSecondGrid = false;
        RunOwnedPass();
        RunOwnedPass();

        Assert.Equal(0U, Census.CountOf(1002L).Blocks);
        Assert.Equal(10U, Census.CountOf(1001L).Blocks);
    }

    [Fact]
    public void A_pass_counts_what_is_in_the_world()
    {
        FakeWorld.Reset();
        FakeWorld.FactionCount = 3;

        CensusRecord row = RunPass();

        Assert.Equal(8U, row.Entities);
        Assert.Equal(4U, row.Grids);
        Assert.Equal(60U, row.Blocks);
        Assert.Equal(24U, row.Pcu);
        Assert.Equal(2U, row.Characters);
        Assert.Equal(2U, row.Voxels);
        Assert.Equal(2U, row.FloatingObjects);
        Assert.Equal(3U, row.Factions);
    }

    [Fact]
    public void Blocks_are_totalled_per_faction()
    {
        FakeWorld.Reset();
        RunPass();

        CensusFaction[] factions = Detail().Factions;

        Assert.Equal(2, factions.Length);
        Assert.Contains(factions, f => f.FactionId == 100L && f.Grids == 2U && f.Blocks == 20U && f.Pcu == 10U);
        Assert.Contains(factions, f => f.FactionId == Census.NoFaction && f.Grids == 2U && f.Blocks == 40U && f.Pcu == 14U);
    }

    [Fact]
    public void Server_ping_is_the_mean_of_player_pings()
    {
        // The old gauge showed 21.33 for three pings, so this can't be a whole number.
        FakeWorld.Reset();
        FakeWorld.Players = new List<(long, long, int)> { (1L, 100L, 20), (2L, 100L, 21), (3L, 200L, 23) };

        CensusRecord row = RunPass();

        Assert.Equal(3U, row.OnlinePlayers);
        Assert.Equal(213U, row.PingMeanTenths);
        Assert.Equal(23U, row.PingWorst);
    }

    [Fact]
    public void A_player_with_no_ping_yet_doesnt_pull_the_mean_down()
    {
        FakeWorld.Reset();
        FakeWorld.Players = new List<(long, long, int)> { (1L, 100L, 40), (2L, 100L, -1) };

        CensusRecord row = RunPass();

        Assert.Equal(2U, row.OnlinePlayers);
        Assert.Equal(400U, row.PingMeanTenths);
    }

    [Fact]
    public void No_players_means_no_ping_rather_than_a_fault()
    {
        FakeWorld.Reset();

        Assert.Equal(0U, RunPass().PingMeanTenths);
    }

    [Fact]
    public void Players_are_listed_with_their_factions()
    {
        FakeWorld.Reset();
        FakeWorld.Players = new List<(long, long, int)> { (1L, 100L, 20), (2L, 100L, 30), (3L, 200L, 40) };

        RunPass();
        (CensusPlayer[] players, CensusFaction[] factions, _) = Detail();

        Assert.Equal(3, players.Length);
        Assert.Contains(players, p => p.IdentityId == 3L && p.FactionId == 200L && p.PingMs == 40U);
        Assert.Contains(factions, f => f.FactionId == 100L && f.OnlineMembers == 2U);
        Assert.Contains(factions, f => f.FactionId == 200L && f.OnlineMembers == 1U);
    }

    [Fact]
    public void A_pass_only_runs_when_due()
    {
        FakeWorld.Reset();
        RunPass();

        var world = default(FakeWorld);
        for (int i = 0; i < 100; i++)
            Census.Step(ref world, paused: false);

        Assert.Equal(1, FakeWorld.Snapshots);
    }

    [Fact]
    public void A_paused_server_is_not_counted()
    {
        FakeWorld.Reset();
        var world = default(FakeWorld);
        Census.MakeDue();

        Census.Step(ref world, paused: true);

        Assert.Equal(0, FakeWorld.Snapshots);
        Assert.False(Census.Walking);
    }

    [Fact]
    public void A_big_world_is_walked_in_slices_across_frames()
    {
        FakeWorld.Reset();
        FakeWorld.Size = 2000;
        FakeWorld.SpinTicksPerVisit = Clock.Frequency / 1_000_000L;   // 1 µs each, so 2 ms in all.

        var world = default(FakeWorld);
        Census.MakeDue();

        long worstFrame = 0L;
        int frames = 0;

        while (frames == 0 || Census.Walking)
        {
            long started = Clock.Now();
            Census.Step(ref world, paused: false);
            worstFrame = System.Math.Max(worstFrame, Clock.Now() - started);
            frames++;
        }

        Assert.True(frames > 10, "A 2 ms pass took " + frames + " frames.");
        Assert.Equal(2000, FakeWorld.Visited);
        Assert.Equal(1, FakeWorld.Snapshots);
    }

    [Fact]
    public void A_pass_records_its_own_cost()
    {
        FakeWorld.Reset();
        FakeWorld.Size = 200;
        FakeWorld.SpinTicksPerVisit = Clock.Frequency / 1_000_000L;

        CensusRecord row = RunPass();

        Assert.InRange(row.PassUnits, 1000U, 500_000U);
    }

    [Fact]
    public void Too_many_players_are_counted_but_not_listed()
    {
        FakeWorld.Reset();
        for (int i = 1; i <= Census.MaxPlayers + 10; i++)
            FakeWorld.Players.Add((i, 100L, 30));

        CensusRecord row = RunPass();
        (CensusPlayer[] players, _, int dropped) = Detail();

        Assert.Equal((uint)(Census.MaxPlayers + 10), row.OnlinePlayers);
        Assert.Equal(Census.MaxPlayers, players.Length);
        Assert.Equal(10, dropped);
    }

    [Fact]
    public void Each_pass_starts_from_nothing()
    {
        FakeWorld.Reset();
        FakeWorld.Players = new List<(long, long, int)> { (1L, 100L, 20) };
        RunPass();

        FakeWorld.Players = new List<(long, long, int)>();
        CensusRecord row = RunPass();

        Assert.Equal(4U, row.Grids);
        Assert.Equal(0U, row.OnlinePlayers);
        Assert.Empty(Detail().Players);
    }

    [Fact]
    public void A_pass_allocates_nothing()
    {
        FakeWorld.Reset();
        FakeWorld.Size = 400;
        RunPass();

        long bytes = Allocation.Measure(() =>
        {
            var world = default(FakeWorld);
            for (int i = 0; i < 200; i++)
            {
                Census.MakeDue();
                Census.Step(ref world, paused: false);
            }
        });

        Assert.Equal(0L, bytes);
    }
}
