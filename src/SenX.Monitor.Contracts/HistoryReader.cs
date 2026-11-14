using System;
using System.Collections.Generic;
using SenX.Monitor.Api;

namespace SenX.Monitor.Contracts;

/// <summary>One second of timings. Times are in 100 ns units.</summary>
public sealed class HistoryRollup
{
    public long Sequence { get; set; }

    public long UnitsSinceStart { get; set; }

    public uint Frames { get; set; }

    public uint SimulatedFrames { get; set; }

    public uint WindowUnits { get; set; }

    public uint FrameP50 { get; set; }

    public uint FrameP95 { get; set; }

    public uint FrameP99 { get; set; }

    public uint FrameMax { get; set; }

    public uint Gen0 { get; set; }

    public uint Gen1 { get; set; }

    public uint Gen2 { get; set; }

    public uint AllocatedKb { get; set; }

    /// <summary>True when the server was paused just before this row, so charts should show a gap.</summary>
    public bool AfterGap { get; set; }

    /// <summary>Sent in this second, of every kind.</summary>
    public uint SentKb { get; set; }

    public uint ReceivedKb { get; set; }

    public uint StateSyncKb { get; set; }

    public uint EventsKb { get; set; }

    public uint ReplicationKb { get; set; }

    /// <summary>RPCs received from clients in this second.</summary>
    public uint RpcsIn { get; set; }

    /// <summary>Time per tree scope in this second, indexed by scope id.</summary>
    public uint[] ScopeUnits { get; set; } = Array.Empty<uint>();

    /// <summary>Simulated frames against the 60 per second the game aims for.</summary>
    public double SimSpeed => WindowUnits == 0U ? 0d : SimulatedFrames / (WindowUnits / 10_000_000d * 60d);
}

/// <summary>One census pass.</summary>
public sealed class HistoryCensus
{
    public long Sequence { get; set; }

    public long UnitsSinceStart { get; set; }

    public uint Entities { get; set; }

    public uint Grids { get; set; }

    public uint Blocks { get; set; }

    public uint Pcu { get; set; }

    public uint Characters { get; set; }

    public uint FloatingObjects { get; set; }

    public uint Voxels { get; set; }

    public uint OnlinePlayers { get; set; }

    public uint Factions { get; set; }

    /// <summary>The mean of player pings, which is what the old Server Ping gauge showed.</summary>
    public double PingMean { get; set; }

    public uint PingWorst { get; set; }

    public uint PassUnits { get; set; }

    public uint WorkingSetMb { get; set; }

    public uint ManagedMb { get; set; }

    /// <summary>Grids a concealment plugin has stopped. These are in <see cref="Grids"/> too.</summary>
    public uint ConcealedGrids { get; set; }

    /// <summary>Blocks on those grids. These are in <see cref="Blocks"/> too.</summary>
    public uint ConcealedBlocks { get; set; }

    /// <summary>Grids that can't move. These are in <see cref="Grids"/> too.</summary>
    public uint StaticGrids { get; set; }

    /// <summary>These are in <see cref="Voxels"/> too.</summary>
    public uint Asteroids { get; set; }

    /// <summary>These are in <see cref="Voxels"/> too.</summary>
    public uint Planets { get; set; }

    /// <summary>The server's share of the whole machine's CPU, since the pass before.</summary>
    public double CpuPercent { get; set; }
}

public sealed class HistoryPlayer
{
    public long IdentityId { get; set; }

    public long FactionId { get; set; }

    public uint PingMs { get; set; }

    /// <summary>What the server sends them, averaged since the last pass.</summary>
    public uint SentBytesPerSecond { get; set; }

    /// <summary>What they send the server, averaged since the last pass.</summary>
    public uint ReceivedBytesPerSecond { get; set; }

    public uint RpcsPerSecond { get; set; }

    /// <summary>Waiting to go to them. Growing means they can't take what the server's sending.</summary>
    public uint QueuedBytes { get; set; }

    /// <summary>Packets the network refused since the last pass.</summary>
    public uint FailedSends { get; set; }
}

public sealed class HistoryFaction
{
    public long FactionId { get; set; }

    public uint Grids { get; set; }

    public uint Blocks { get; set; }

    public uint Pcu { get; set; }

    public uint OnlineMembers { get; set; }
}

/// <summary>What a <see cref="HistoryGroup"/> totals by. Numbers match the worker's.</summary>
public enum HistoryDimension : uint
{
    None = 0,
    Grids = 1,
    BlockTypes = 2,
    Definitions = 3,
    Owners = 4,
    EntityTypes = 5,
    Session = 6,
    Logic = 7,
    Callbacks = 8,
    Scripts = 9,

    /// <summary>Time by mod or plugin.</summary>
    Mods = 10,

    /// <summary>What's inside each mod. <see cref="HistoryGroup.Representative"/> is the mod.</summary>
    ModParts = 11,

    /// <summary>RPCs from clients, by the method they run.</summary>
    Rpcs = 12,

    /// <summary>RPCs the server sent. These rows hold <see cref="HistoryGroup.BytesPerSecond"/> and no time.</summary>
    RpcsSent = 13,

    /// <summary>
    /// Not a list of costs. Each row is one thing whose time moved between the game thread and worker threads,
    /// with its own key and figures. See <see cref="HistoryGroup.JumpedList"/>.
    /// </summary>
    ThreadJumps = 14,

    /// <summary>Not a list of its own on the worker. Factions in history are looked up by this.</summary>
    Factions = 100,
}

/// <summary>One line of a background total, for the "what is costing time" charts. Times are per frame.</summary>
public sealed class HistoryGroup
{
    public long Sequence { get; set; }

    public long UnitsSinceStart { get; set; }

    public HistoryDimension Dimension { get; set; }

    /// <summary>A grid or identity id, or a number standing for a type. See <see cref="Representative"/>.</summary>
    public long Key { get; set; }

    /// <summary>The heaviest entity in this row, which is how a type gets a name.</summary>
    public long Representative { get; set; }

    public uint MainUnits { get; set; }

    public uint WorkerUnits { get; set; }

    public float CallsPerFrame { get; set; }

    public uint MaxUnits { get; set; }

    /// <summary>For rows that count traffic. Zero for the rest.</summary>
    public uint BytesPerSecond { get; set; }

    /// <summary>For grids, owners and factions, the blocks the last census found. Zero for the rest.</summary>
    public uint Blocks { get; set; }

    /// <summary>For owners and factions, the grids the last census found. Zero for the rest.</summary>
    public uint Grids { get; set; }

    /// <summary>For a thread jump, the list the thing is in. The worker keeps it in <see cref="Blocks"/>, which jumps don't use.</summary>
    public HistoryDimension JumpedList => Dimension == HistoryDimension.ThreadJumps ? (HistoryDimension)Blocks : HistoryDimension.None;

    /// <summary>For a thread jump, where its time was. Kept in <see cref="Grids"/>, which jumps don't use.</summary>
    public MonitorThread JumpFrom => Dimension == HistoryDimension.ThreadJumps ? (MonitorThread)(Grids >> 8) : MonitorThread.Unknown;

    /// <summary>For a thread jump, where its time is now.</summary>
    public MonitorThread JumpTo => Dimension == HistoryDimension.ThreadJumps ? (MonitorThread)(Grids & 0xFFU) : MonitorThread.Unknown;

    /// <summary>For the one thread jump row with no list, how many more jumps its total found than it lists. Zero for the rest.</summary>
    public uint JumpsNotListed => Dimension == HistoryDimension.ThreadJumps && Blocks == 0U ? MaxUnits : 0U;
}

/// <summary>What a <see cref="HistoryMarker"/> says happened. Numbers match the worker's.</summary>
public enum HistoryMarkerKind : uint
{
    Saved = 1,
    SaveFailed = 2,
    LagTripped = 3,
    RunStarted = 4,
    RunFinished = 5,
    DetailChanged = 6,
    JoinHitch = 7,
}

/// <summary>A moment worth a line on the charts.</summary>
public sealed class HistoryMarker
{
    public long Sequence { get; set; }

    public long UnitsSinceStart { get; set; }

    public HistoryMarkerKind Kind { get; set; }

    /// <summary>For a lag trip, the report that holds its evidence.</summary>
    public uint Value { get; set; }
}

/// <summary>One decoded pull.</summary>
public sealed class HistoryBatch
{
    /// <summary>When the server started, as UTC ticks. If this changes, it restarted, and pulls start again from zero.</summary>
    public long StartedUtcTicks { get; set; }

    public long RollupFirst { get; set; }

    /// <summary>The sequence the next rollup will get on the server.</summary>
    public long RollupHead { get; set; }

    public long CensusFirst { get; set; }

    public long CensusHead { get; set; }

    /// <summary>The last pass whose players and factions have been sent. Pass it back on the next pull.</summary>
    public long DetailSequence { get; set; }

    /// <summary>True when this batch has fresh players and factions.</summary>
    public bool HasDetail { get; set; }

    /// <summary>Players or factions left out because there were too many.</summary>
    public int Dropped { get; set; }

    public List<HistoryRollup> Rollups { get; } = new List<HistoryRollup>();

    public List<HistoryCensus> Census { get; } = new List<HistoryCensus>();

    public long GroupFirst { get; set; }

    public long GroupHead { get; set; }

    public List<HistoryGroup> Groups { get; } = new List<HistoryGroup>();

    public long MarkerFirst { get; set; }

    public long MarkerHead { get; set; }

    public List<HistoryMarker> Markers { get; } = new List<HistoryMarker>();

    public List<HistoryPlayer> Players { get; } = new List<HistoryPlayer>();

    public List<HistoryFaction> Factions { get; } = new List<HistoryFaction>();

    /// <summary>Where the next pull should start.</summary>
    public long NextRollup => RollupFirst + Rollups.Count;

    /// <summary>Where the next pull should start.</summary>
    public long NextCensus => CensusFirst + Census.Count;

    /// <summary>Where the next pull should start.</summary>
    public long NextGroups => GroupFirst + Groups.Count;

    /// <summary>Where the next pull should start.</summary>
    public long NextMarkers => MarkerFirst + Markers.Count;
}

/// <summary>
/// Decodes the bytes the worker packs. Plain managed code, so the Quasar side needs no unsafe code
/// and no reference to Monitor's core. Rows use the column counts in the header, so a worker and a
/// Quasar plugin of slightly different versions can still read what they share.
/// </summary>
public static class HistoryReader
{
    private const uint Magic = 0x34484E4DU;

    /// <summary>Size of the fixed header. Offsets below follow the worker's header exactly.</summary>
    public const int HeaderBytes = 144;

    // Columns before the per-scope times in a rollup row.
    private const int RollupFixedColumns = 20;
    private const int MinCensusColumns = 14;

    // Far past anything real. A damaged header could otherwise ask for any amount of memory with no rows to pay for it.
    private const int MaxColumns = 1024;
    private const int CensusColumnsWithMemory = 16;
    private const int CensusColumnsWithConcealed = 18;
    private const int CensusColumnsWithWorldKinds = 22;
    private const int PlayerColumns = 10;
    private const int FactionColumns = 6;
    private const int GroupColumns = 14;
    private const int MarkerColumns = 4;
    private const uint AfterGapFlag = 1U;

    /// <summary>Decodes a batch. Returns null if it isn't a batch this version can read, or is damaged.</summary>
    public static HistoryBatch? Read(byte[] data)
    {
        if (data == null || data.Length < HeaderBytes || BitConverter.ToUInt32(data, 0) != Magic)
            return null;

        int rollupColumns = BitConverter.ToInt32(data, 4);
        int censusColumns = BitConverter.ToInt32(data, 8);
        int rollupCount = BitConverter.ToInt32(data, 40);
        int rollupBytes = BitConverter.ToInt32(data, 44);
        int censusCount = BitConverter.ToInt32(data, 64);
        int censusBytes = BitConverter.ToInt32(data, 68);
        int players = BitConverter.ToInt32(data, 80);
        int factions = BitConverter.ToInt32(data, 84);
        int detailBytes = BitConverter.ToInt32(data, 88);
        int groupCount = BitConverter.ToInt32(data, 112);
        int groupBytes = BitConverter.ToInt32(data, 116);
        int markerCount = BitConverter.ToInt32(data, 136);
        int markerBytes = BitConverter.ToInt32(data, 140);

        if (rollupColumns < RollupFixedColumns || censusColumns < MinCensusColumns || rollupColumns > MaxColumns || censusColumns > MaxColumns)
            return null;

        if (rollupCount < 0 || censusCount < 0 || groupCount < 0 || markerCount < 0 || players < 0 || factions < 0
            || rollupBytes < 0 || censusBytes < 0 || groupBytes < 0 || markerBytes < 0 || detailBytes < 0
            || (long)HeaderBytes + rollupBytes + censusBytes + groupBytes + markerBytes + detailBytes > data.Length)
        {
            return null;
        }

        var batch = new HistoryBatch
        {
            StartedUtcTicks = BitConverter.ToInt64(data, 16),
            RollupFirst = BitConverter.ToInt64(data, 24),
            RollupHead = BitConverter.ToInt64(data, 32),
            CensusFirst = BitConverter.ToInt64(data, 48),
            CensusHead = BitConverter.ToInt64(data, 56),
            DetailSequence = BitConverter.ToInt64(data, 72),
            HasDetail = detailBytes > 0,
            Dropped = BitConverter.ToInt32(data, 92),
            GroupFirst = BitConverter.ToInt64(data, 96),
            GroupHead = BitConverter.ToInt64(data, 104),
            MarkerFirst = BitConverter.ToInt64(data, 120),
            MarkerHead = BitConverter.ToInt64(data, 128),
        };

        int position = HeaderBytes;

        return ReadRollups(data, ref position, position + rollupBytes, rollupCount, rollupColumns, batch)
               && ReadCensus(data, ref position, position + censusBytes, censusCount, censusColumns, batch)
               && ReadGroups(data, ref position, position + groupBytes, groupCount, batch)
               && ReadMarkers(data, ref position, position + markerBytes, markerCount, batch)
               && ReadDetail(data, ref position, position + detailBytes, players, factions, batch)
            ? batch
            : null;
    }

    private static bool ReadRollups(byte[] data, ref int position, int end, int rows, int columns, HistoryBatch batch)
    {
        if (!Decode(data, ref position, end, rows, columns, out uint[] values))
            return false;

        for (int row = 0; row < rows; row++)
        {
            int at = row * columns;
            var scopes = new uint[columns - RollupFixedColumns];
            Array.Copy(values, at + RollupFixedColumns, scopes, 0, scopes.Length);

            batch.Rollups.Add(new HistoryRollup
            {
                Sequence = batch.RollupFirst + row,
                UnitsSinceStart = Long(values, at),
                Frames = values[at + 2],
                SimulatedFrames = values[at + 3],
                WindowUnits = values[at + 4],
                FrameP50 = values[at + 5],
                FrameP95 = values[at + 6],
                FrameP99 = values[at + 7],
                FrameMax = values[at + 8],
                Gen0 = values[at + 9],
                Gen1 = values[at + 10],
                Gen2 = values[at + 11],
                AllocatedKb = values[at + 12],
                AfterGap = (values[at + 13] & AfterGapFlag) != 0U,
                SentKb = values[at + 14],
                ReceivedKb = values[at + 15],
                StateSyncKb = values[at + 16],
                EventsKb = values[at + 17],
                ReplicationKb = values[at + 18],
                RpcsIn = values[at + 19],
                ScopeUnits = scopes,
            });
        }

        return true;
    }

    private static bool ReadCensus(byte[] data, ref int position, int end, int rows, int columns, HistoryBatch batch)
    {
        if (!Decode(data, ref position, end, rows, columns, out uint[] values))
            return false;

        for (int row = 0; row < rows; row++)
        {
            int at = row * columns;

            batch.Census.Add(new HistoryCensus
            {
                Sequence = batch.CensusFirst + row,
                UnitsSinceStart = Long(values, at),
                Entities = values[at + 2],
                Grids = values[at + 3],
                Blocks = values[at + 4],
                Pcu = values[at + 5],
                Characters = values[at + 6],
                FloatingObjects = values[at + 7],
                Voxels = values[at + 8],
                OnlinePlayers = values[at + 9],
                Factions = values[at + 10],
                PingMean = values[at + 11] / 10d,
                PingWorst = values[at + 12],
                PassUnits = values[at + 13],
                WorkingSetMb = columns >= CensusColumnsWithMemory ? values[at + 14] : 0U,
                ManagedMb = columns >= CensusColumnsWithMemory ? values[at + 15] : 0U,
                ConcealedGrids = columns >= CensusColumnsWithConcealed ? values[at + 16] : 0U,
                ConcealedBlocks = columns >= CensusColumnsWithConcealed ? values[at + 17] : 0U,
                StaticGrids = columns >= CensusColumnsWithWorldKinds ? values[at + 18] : 0U,
                Asteroids = columns >= CensusColumnsWithWorldKinds ? values[at + 19] : 0U,
                Planets = columns >= CensusColumnsWithWorldKinds ? values[at + 20] : 0U,
                CpuPercent = columns >= CensusColumnsWithWorldKinds ? values[at + 21] / 10d : 0d,
            });
        }

        return true;
    }

    private static bool ReadGroups(byte[] data, ref int position, int end, int rows, HistoryBatch batch)
    {
        if (!Decode(data, ref position, end, rows, GroupColumns, out uint[] values))
            return false;

        for (int row = 0; row < rows; row++)
        {
            int at = row * GroupColumns;

            batch.Groups.Add(new HistoryGroup
            {
                Sequence = batch.GroupFirst + row,
                UnitsSinceStart = Long(values, at),
                Key = Long(values, at + 2),
                Representative = Long(values, at + 4),
                Dimension = (HistoryDimension)values[at + 6],
                MainUnits = values[at + 7],
                WorkerUnits = values[at + 8],
                CallsPerFrame = values[at + 9] / 100f,
                MaxUnits = values[at + 10],
                BytesPerSecond = values[at + 11],
                Blocks = values[at + 12],
                Grids = values[at + 13],
            });
        }

        return true;
    }

    private static bool ReadMarkers(byte[] data, ref int position, int end, int rows, HistoryBatch batch)
    {
        if (!Decode(data, ref position, end, rows, MarkerColumns, out uint[] values))
            return false;

        for (int row = 0; row < rows; row++)
        {
            int at = row * MarkerColumns;

            batch.Markers.Add(new HistoryMarker
            {
                Sequence = batch.MarkerFirst + row,
                UnitsSinceStart = Long(values, at),
                Kind = (HistoryMarkerKind)values[at + 2],
                Value = values[at + 3],
            });
        }

        return true;
    }

    private static bool ReadDetail(byte[] data, ref int position, int end, int players, int factions, HistoryBatch batch)
    {
        if (!Decode(data, ref position, end, players, PlayerColumns, out uint[] playerValues)
            || !Decode(data, ref position, end, factions, FactionColumns, out uint[] factionValues))
        {
            return false;
        }

        for (int row = 0; row < players; row++)
        {
            int at = row * PlayerColumns;

            batch.Players.Add(new HistoryPlayer
            {
                IdentityId = Long(playerValues, at),
                FactionId = Long(playerValues, at + 2),
                PingMs = playerValues[at + 4],
                SentBytesPerSecond = playerValues[at + 5],
                ReceivedBytesPerSecond = playerValues[at + 6],
                RpcsPerSecond = playerValues[at + 7],
                QueuedBytes = playerValues[at + 8],
                FailedSends = playerValues[at + 9],
            });
        }

        for (int row = 0; row < factions; row++)
        {
            int at = row * FactionColumns;

            batch.Factions.Add(new HistoryFaction
            {
                FactionId = Long(factionValues, at),
                Grids = factionValues[at + 2],
                Blocks = factionValues[at + 3],
                Pcu = factionValues[at + 4],
                OnlineMembers = factionValues[at + 5],
            });
        }

        return true;
    }

    private static long Long(uint[] values, int at) => (long)(values[at] | ((ulong)values[at + 1] << 32));

    // Rows are column deltas in zigzag varints. Every value takes at least one byte, which bounds the allocation.
    private static bool Decode(byte[] input, ref int position, int end, int rows, int columns, out uint[] output)
    {
        if ((long)rows * columns > end - position)
        {
            output = Array.Empty<uint>();
            return false;
        }

        output = new uint[rows * columns];
        var previous = new uint[columns];

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                ulong zigzag = 0UL;
                int shift = 0;

                while (true)
                {
                    if (position >= end || shift >= 35)
                        return false;

                    byte value = input[position++];
                    zigzag |= (ulong)(value & 0x7F) << shift;
                    if (value < 0x80)
                        break;

                    shift += 7;
                }

                long delta = (long)(zigzag >> 1) ^ -(long)(zigzag & 1UL);
                uint decoded = (uint)(previous[column] + delta);
                previous[column] = decoded;
                output[(row * columns) + column] = decoded;
            }
        }

        return true;
    }
}
