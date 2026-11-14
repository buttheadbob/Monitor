using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SenX.Monitor.Core;

/// <summary>One census pass: facts about the world, not timings. Quasar charts these over time.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CensusRecord
{
    public long UnitsSinceStart;
    public uint Entities;
    public uint Grids;
    public uint Blocks;
    public uint Pcu;
    public uint Characters;
    public uint FloatingObjects;
    public uint Voxels;
    public uint OnlinePlayers;
    public uint Factions;

    /// <summary>Mean of player pings in tenths of a millisecond, because a mean rarely lands on a whole number.</summary>
    public uint PingMeanTenths;
    public uint PingWorst;

    /// <summary>The game-thread time this pass took, so its cost is visible.</summary>
    public uint PassUnits;

    public uint WorkingSetMb;
    public uint ManagedMb;

    /// <summary>Grids hidden by a concealment plugin, which are also counted in <see cref="Grids"/>.</summary>
    public uint ConcealedGrids;

    /// <summary>Blocks on those grids, which are also counted in <see cref="Blocks"/>.</summary>
    public uint ConcealedBlocks;

    /// <summary>Grids that can't move, which are also counted in <see cref="Grids"/>.</summary>
    public uint StaticGrids;

    /// <summary>These are also counted in <see cref="Voxels"/>.</summary>
    public uint Asteroids;

    /// <summary>These are also counted in <see cref="Voxels"/>.</summary>
    public uint Planets;

    /// <summary>The server's share of the whole machine's CPU since the last pass, in tenths of a percent.</summary>
    public uint CpuTenths;
}

/// <summary>A player online at the last pass. Ids only: Quasar looks up names.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CensusPlayer
{
    public long IdentityId;
    public long FactionId;
    public uint PingMs;

    /// <summary>Averages since the last pass, counted where packets enter and leave the game.</summary>
    public uint SentBytesPerSecond;
    public uint ReceivedBytesPerSecond;
    public uint RpcsPerSecond;

    /// <summary>Waiting to go to this player. Growing means they can't take what the server's sending.</summary>
    public uint QueuedBytes;

    /// <summary>Packets the network refused since the last pass.</summary>
    public uint FailedSends;
}

/// <summary>A faction at the last pass. Must keep the id first, because this is also a table slot.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CensusFaction
{
    public long FactionId;
    public uint Grids;
    public uint Blocks;
    public uint Pcu;
    public uint OnlineMembers;
}

/// <summary>What the last pass counted for a grid, an owner or a faction. Must keep the key first, because this is a table slot.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CensusCount
{
    public long Key;

    /// <summary>For an owner or a faction, the grids that are mostly theirs. Zero for a grid.</summary>
    public uint Grids;

    /// <summary>The blocks on a grid, or on all of an owner's or faction's grids.</summary>
    public uint Blocks;
}

/// <summary>Who belongs to which faction, for totals made off the game thread. Must keep the owner first.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct OwnerFaction
{
    public long OwnerId;
    public long FactionId;
    public long Pass;
}

/// <summary>Where a census gets its facts from. A struct, so walking allocates nothing.</summary>
public interface ICensusSource
{
    /// <summary>Takes a snapshot of the world's top-level entities, and returns how many.</summary>
    int Snapshot();

    /// <summary>Looks at one entity from the snapshot, and reports it to <see cref="Census"/>.</summary>
    void Visit(int index);

    /// <summary>Reports what isn't per entity: players, pings and the faction count.</summary>
    void Finish();
}

/// <summary>
/// Counts what's in the world every 30 seconds, for charts of blocks, PCU, players and ping. Grids
/// carry their own totals, so a pass walks grids and not blocks. It still walks in small timed slices
/// across frames, so a huge world can't cause a hitch. Game thread only, except reading results.
/// </summary>
public static unsafe class Census
{
    public const int PeriodSeconds = 30;
    public const int MaxPlayers = 256;
    public const int MaxFactions = 512;

    /// <summary>A little over eight hours of passes.</summary>
    public const int RingRows = 1 << 10;

    /// <summary>The most a pass spends in one frame, in 100 ns units: 30 µs.</summary>
    internal const long SliceUnits = 300L;

    /// <summary>Stands in for "no faction", because a zero key marks an empty table slot.</summary>
    public const long NoFaction = -1L;

    /// <summary>Stands in for "nobody" in the counts, where it can't be mistaken for <see cref="NoFaction"/>.</summary>
    public const long NoOwner = -2L;

    private const int FactionSlots = 1 << 12;
    private const int OwnerSlots = 1 << 14;
    private const int CountSlots = 1 << 15;
    private const int CountSlotsClearedPerFrame = 1 << 10;
    private const int VisitsPerClockRead = 32;

    private struct Detail
    {
        public int Players;
        public int Factions;
        public int Dropped;
        public CensusPlayer* PlayerRows;
        public CensusFaction* FactionRows;
        public OpenTable<CensusCount> Counts;
    }

    private static RecordRing? s_rows;
    private static Detail* s_details;
    private static OpenTable<CensusFaction> s_tally;
    private static OpenTable<OwnerFaction> s_owners;
    private static int s_active;
    private static long s_nextPass;
    private static bool s_walking;
    private static int s_count;
    private static int s_cursor;
    private static long s_walkTicks;
    private static long s_pingTotal;
    private static uint s_pingCount;
    private static int s_dropped;
    private static long s_sequence;
    private static long s_lastBegin;
    private static double s_passSeconds = PeriodSeconds;
    private static long s_lastCpuClock;
    private static long s_lastCpuUnits;
    private static int s_countsCleared = CountSlots;
    private static CensusRecord s_row;

    public static RecordRing Rows => s_rows!;

    /// <summary>Counts passes. Changes when <see cref="TryCopyDetail"/> has something new.</summary>
    public static long DetailSequence => Volatile.Read(ref s_sequence);

    internal static void Initialize()
    {
        if (s_rows != null)
            return;

        s_rows = new RecordRing(sizeof(CensusRecord), RingRows);
        s_tally = OpenTable<CensusFaction>.Allocate(FactionSlots);
        s_owners = OpenTable<OwnerFaction>.Allocate(OwnerSlots);
        s_details = Native.Allocate<Detail>(2);

        for (int i = 0; i < 2; i++)
        {
            s_details[i].PlayerRows = Native.Allocate<CensusPlayer>(MaxPlayers);
            s_details[i].FactionRows = Native.Allocate<CensusFaction>(MaxFactions);
            s_details[i].Counts = OpenTable<CensusCount>.Allocate(CountSlots);
        }
    }

    /// <summary>Called once per frame. Starts a pass when one is due, or carries on with the current one.</summary>
    public static void Step<T>(ref T source, bool paused) where T : struct, ICensusSource
    {
        long now = Clock.Now();

        if (!s_walking)
        {
            ClearCounts(CountSlotsClearedPerFrame);

            // A paused server isn't changing, so there's nothing to count.
            if (paused || now < s_nextPass)
                return;

            Begin();
            s_count = source.Snapshot();
        }

        long deadline = now + (SliceUnits * Clock.Frequency / Clock.UnitsPerSecond);

        while (s_cursor < s_count)
        {
            source.Visit(s_cursor++);
            s_row.Entities++;

            if ((s_cursor & (VisitsPerClockRead - 1)) == 0 && Clock.Now() >= deadline)
            {
                s_walkTicks += Clock.Now() - now;
                return;
            }
        }

        source.Finish();

        long end = Clock.Now();
        s_walkTicks += end - now;
        Commit(end);
    }

    /// <summary>
    /// A grid in the world. Pass <see cref="NoFaction"/> for one with no faction. <paramref name="ownerId"/> is who
    /// mostly owns it, or zero for nobody. A row in a total can then say how big it is, without touching the game.
    /// </summary>
    public static void AddGrid(long factionId, int blocks, int pcu, bool concealed = false, long gridId = 0L, long ownerId = 0L, bool isStatic = false)
    {
        s_row.Grids++;
        s_row.Blocks += (uint)blocks;
        s_row.Pcu += (uint)pcu;

        if (concealed)
        {
            s_row.ConcealedGrids++;
            s_row.ConcealedBlocks += (uint)blocks;
        }

        if (isStatic)
            s_row.StaticGrids++;

        Count(gridId, 0U, blocks);
        Count(ownerId == 0L ? NoOwner : ownerId, 1U, blocks);
        Count(factionId == 0L ? NoFaction : factionId, 1U, blocks);

        CensusFaction* faction = Tally(factionId);
        if (faction == null)
            return;

        faction->Grids++;
        faction->Blocks += (uint)blocks;
        faction->Pcu += (uint)pcu;
    }

    public static void AddCharacter() => s_row.Characters++;

    public static void AddFloatingObject() => s_row.FloatingObjects++;

    public static void AddVoxel() => s_row.Voxels++;

    public static void AddAsteroid()
    {
        s_row.Voxels++;
        s_row.Asteroids++;
    }

    public static void AddPlanet()
    {
        s_row.Voxels++;
        s_row.Planets++;
    }

    /// <summary>
    /// A player online. Pass a negative ping when the game has none for them yet. <paramref name="peer"/>
    /// is who they are to the network, which is how their traffic is found.
    /// </summary>
    public static void AddPlayer(long identityId, long factionId, int pingMs, ulong peer = 0UL, int queuedBytes = 0)
    {
        s_row.OnlinePlayers++;

        CensusFaction* faction = Tally(factionId);
        if (faction != null)
            faction->OnlineMembers++;

        Detail* detail = s_details + (1 - s_active);
        uint ping = pingMs > 0 ? (uint)pingMs : 0U;

        if (pingMs >= 0)
        {
            s_pingTotal += ping;
            s_pingCount++;
            if (ping > s_row.PingWorst)
                s_row.PingWorst = ping;
        }

        if (detail->Players >= MaxPlayers)
        {
            s_dropped++;
            return;
        }

        CensusPlayer* row = detail->PlayerRows + detail->Players++;
        row->IdentityId = identityId;
        row->FactionId = factionId;
        row->PingMs = ping;

        PeerDelta traffic = NetTraffic.TakeDelta(peer);
        row->SentBytesPerSecond = PerSecond(traffic.SentBytes);
        row->ReceivedBytesPerSecond = PerSecond(traffic.ReceivedBytes);
        row->RpcsPerSecond = PerSecond(traffic.Rpcs);
        row->FailedSends = (uint)traffic.FailedSends;
        row->QueuedBytes = queuedBytes > 0 ? (uint)queuedBytes : 0U;
    }

    private static uint PerSecond(long count) => (uint)Math.Round(count / s_passSeconds);

    public static void SetFactionCount(int count) => s_row.Factions = (uint)count;

    /// <summary>What the server's using, in bytes. A slow climb over days is a leak, which no frame time shows.</summary>
    public static void SetMemory(long workingSet, long managed)
    {
        s_row.WorkingSetMb = (uint)(workingSet >> 20);
        s_row.ManagedMb = (uint)(managed >> 20);
    }

    /// <summary>The CPU time the server has used since it started, in 100 ns units. Each pass turns the change into a share of the machine.</summary>
    public static void SetCpuTime(long units)
    {
        long now = Clock.Now();

        if (s_lastCpuClock != 0L && now > s_lastCpuClock)
        {
            double seconds = (now - s_lastCpuClock) / (double)Clock.Frequency;
            double share = (units - s_lastCpuUnits) / (seconds * Clock.UnitsPerSecond * Environment.ProcessorCount);
            s_row.CpuTenths = (uint)Math.Round(Math.Min(Math.Max(share, 0d), 1d) * 1000d);
        }

        s_lastCpuClock = now;
        s_lastCpuUnits = units;
    }

    /// <summary>Notes a faction member during a pass. A table that's full just leaves them factionless.</summary>
    public static void NoteMember(long ownerId, long factionId)
    {
        OwnerFaction* slot = s_owners.Claim(ownerId, out bool _);
        if (slot == null)
            return;

        slot->FactionId = factionId;
        slot->Pass = Volatile.Read(ref s_sequence);
    }

    /// <summary>
    /// The faction an owner belonged to at the last pass. Safe from any thread. Someone not seen in the
    /// last two passes has left, so entries go stale by themselves and the table never needs clearing,
    /// which could otherwise empty it under a reader.
    /// </summary>
    public static long FactionOf(long ownerId)
    {
        OwnerFaction* slot = s_owners.Find(ownerId);
        return slot != null && slot->Pass + 1L >= Volatile.Read(ref s_sequence) ? slot->FactionId : NoFaction;
    }

    /// <summary>
    /// What the last pass counted for a grid, owner or faction. Safe from any thread. Anything not counted reads as
    /// zero, and so can a read still going when the next pass finishes and this table starts clearing. Its memory is
    /// never freed, so that's the worst that can happen.
    /// </summary>
    public static CensusCount CountOf(long key)
    {
        if (s_details == null)
            return default;

        CensusCount* slot = s_details[Volatile.Read(ref s_active)].Counts.Find(key);
        return slot == null ? default : *slot;
    }

    /// <summary>The same for an owner, by the key a total's row has, where nobody is <see cref="RollupTable.NoKey"/>.</summary>
    public static CensusCount CountOfOwner(long owner) => CountOf(owner == RollupTable.NoKey ? NoOwner : owner);

    /// <summary>Copies the last pass's players and factions. Safe from any thread. Returns false until a pass has finished.</summary>
    public static bool TryCopyDetail(CensusPlayer* players, CensusFaction* factions, out int playerCount, out int factionCount, out int dropped, out long sequence)
    {
        playerCount = 0;
        factionCount = 0;
        dropped = 0;

        do
        {
            sequence = Volatile.Read(ref s_sequence);
            if (sequence == 0L)
                return false;

            Detail* detail = s_details + Volatile.Read(ref s_active);
            playerCount = detail->Players;
            factionCount = detail->Factions;
            dropped = detail->Dropped;

            Buffer.MemoryCopy(detail->PlayerRows, players, MaxPlayers * sizeof(CensusPlayer), playerCount * sizeof(CensusPlayer));
            Buffer.MemoryCopy(detail->FactionRows, factions, MaxFactions * sizeof(CensusFaction), factionCount * sizeof(CensusFaction));
            Thread.MemoryBarrier();
        }
        while (sequence != Volatile.Read(ref s_sequence));

        return true;
    }

    private static void Begin()
    {
        long now = Clock.Now();
        s_passSeconds = s_lastBegin == 0L ? PeriodSeconds : Math.Max((now - s_lastBegin) / (double)Clock.Frequency, 1d);
        s_lastBegin = now;
        s_walking = true;
        s_cursor = 0;
        s_count = 0;
        s_walkTicks = 0L;
        s_pingTotal = 0L;
        s_pingCount = 0U;
        s_dropped = 0;
        s_row = default;
        s_tally.Clear();

        Detail* spare = s_details + (1 - s_active);
        spare->Players = 0;
        spare->Factions = 0;
        ClearCounts(CountSlots);
    }

    // The table is too big to clear inside a pass's slice, so this happens a little at a time between passes.
    private static void ClearCounts(int slots)
    {
        if (s_countsCleared >= CountSlots)
            return;

        int count = Math.Min(slots, CountSlots - s_countsCleared);
        s_details[1 - s_active].Counts.ClearSlots(s_countsCleared, count);
        s_countsCleared += count;
    }

    // A full table just leaves these reading zero.
    private static void Count(long key, uint grids, int blocks)
    {
        if (key == 0L)
            return;

        CensusCount* slot = s_details[1 - s_active].Counts.Claim(key, out bool _);
        if (slot == null)
            return;

        slot->Grids += grids;
        slot->Blocks += (uint)blocks;
    }

    private static CensusFaction* Tally(long factionId)
    {
        CensusFaction* slot = s_tally.Claim(factionId == 0L ? NoFaction : factionId, out bool _);
        if (slot == null)
            s_dropped++;

        return slot;
    }

    private static void Commit(long now)
    {
        int spare = 1 - s_active;
        Detail* detail = s_details + spare;

        for (int i = 0; i < FactionSlots; i++)
        {
            CensusFaction* slot = s_tally.At(i);
            if (slot->FactionId == 0L)
                continue;

            if (detail->Factions >= MaxFactions)
            {
                s_dropped++;
                continue;
            }

            detail->FactionRows[detail->Factions++] = *slot;
        }

        detail->Dropped = s_dropped;

        s_row.UnitsSinceStart = FrameEngine.UnitsSinceStart(now);
        s_row.PassUnits = (uint)Clock.ToUnits(s_walkTicks);
        s_row.PingMeanTenths = s_pingCount == 0U ? 0U : (uint)((s_pingTotal * 10L) / s_pingCount);

        *(CensusRecord*)s_rows!.Reserve() = s_row;
        s_rows.Commit();

        // Readers see the finished copy, and the old one becomes next pass's spare.
        Volatile.Write(ref s_active, spare);
        Interlocked.Increment(ref s_sequence);
        s_countsCleared = 0;

        s_walking = false;
        s_nextPass = now + (PeriodSeconds * Clock.Frequency);
    }

    /// <summary>Makes a pass due on the next step, so tests don't wait 30 seconds.</summary>
    internal static void MakeDue() => s_nextPass = 0L;

    internal static bool Walking => s_walking;

    /// <summary>Lets tests have the last pass start a known time ago, so a rate has a known divisor.</summary>
    internal static void SetLastBeginForTests(long ticks) => s_lastBegin = ticks;

    internal static void ResetForTests()
    {
        s_lastBegin = 0L;
        s_walking = false;
        s_nextPass = 0L;
        s_sequence = 0L;
        s_lastCpuClock = 0L;
        s_countsCleared = 0;
    }
}
