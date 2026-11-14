using System;
using System.Runtime.InteropServices;

namespace SenX.Monitor.Core;

/// <summary>
/// Packs history for Quasar: rollup rows, census rows and the latest players and factions. Quasar
/// pulls these on its own schedule and says where it got to, so it can be away for up to an hour and
/// miss nothing. Reads only the rings, so it works from any thread and never touches the game.
/// A batch is complete in itself, and stays small enough to pass through every JSON layer without
/// reaching the large object heap. What doesn't fit comes in the next pull.
/// </summary>
public static unsafe class HistoryWriter
{
    /// <summary>The largest batch. As base64 inside JSON it stays under the large object heap threshold.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>"MNH4" in little-endian order. Changes if the format does.</summary>
    public const uint Magic = 0x34484E4DU;

    // Counted by hand, because a rollup's size includes padding at the end that isn't a column.
    private const int RollupColumns = 20 + Scope.Count;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Header
    {
        public uint Magic;
        public int RollupColumns;
        public int CensusColumns;
        public int Reserved;
        public long StartedUtcTicks;
        public long RollupFirst;
        public long RollupHead;
        public int RollupCount;
        public int RollupBytes;
        public long CensusFirst;
        public long CensusHead;
        public int CensusCount;
        public int CensusBytes;
        public long DetailSequence;
        public int Players;
        public int Factions;
        public int DetailBytes;
        public int Dropped;
        public long GroupFirst;
        public long GroupHead;
        public int GroupCount;
        public int GroupBytes;
        public long MarkerFirst;
        public long MarkerHead;
        public int MarkerCount;
        public int MarkerBytes;
    }

    /// <summary>Size of the fixed header at the start of every batch.</summary>
    public static int HeaderBytes => sizeof(Header);

    /// <summary>When this server started, as UTC ticks. A new value tells Quasar that sequence numbers restarted.</summary>
    public static long StartedUtcTicks { get; set; }

    /// <summary>
    /// Writes one batch, starting at the given sequence numbers. Rows older than the ring holds are
    /// skipped, and the batch's first sequence shows the gap. Returns the bytes written.
    /// </summary>
    public static int Write(long fromRollup, long fromCensus, long fromDetail, byte* output, int capacity, long fromGroups = 0L, long fromMarkers = 0L)
    {
        if (capacity < sizeof(Header))
            throw new ArgumentOutOfRangeException(nameof(capacity));

        var header = default(Header);
        header.Magic = Magic;
        header.RollupColumns = RollupColumns;
        header.CensusColumns = sizeof(CensusRecord) / sizeof(uint);
        header.StartedUtcTicks = StartedUtcTicks;

        int used = sizeof(Header);

        // Timings first, because everything else can wait a pull if a batch fills up.
        header.RollupBytes = WriteRing(FrameEngine.Rollups, header.RollupColumns, fromRollup, output + used, capacity - used, out header.RollupFirst, out header.RollupHead, out header.RollupCount);
        used += header.RollupBytes;

        header.CensusBytes = WriteRing(Census.Rows, header.CensusColumns, fromCensus, output + used, capacity - used, out header.CensusFirst, out header.CensusHead, out header.CensusCount);
        used += header.CensusBytes;

        // Only whole totals, so a batch that reaches the head has everything of the last one. Thread jumps ride here
        // as rows of list 14, and an older reader that doesn't know that list stores them as rows nobody asks for.
        // The format is otherwise the same, so the magic stays.
        header.GroupBytes = WriteRing(Sweep.Rows, sizeof(GroupRecord) / sizeof(uint), fromGroups, output + used, capacity - used, out header.GroupFirst, out header.GroupHead, out header.GroupCount, Sweep.WholeRows);
        used += header.GroupBytes;

        header.MarkerBytes = WriteRing(Markers.Rows, sizeof(MarkerRecord) / sizeof(uint), fromMarkers, output + used, capacity - used, out header.MarkerFirst, out header.MarkerHead, out header.MarkerCount);
        used += header.MarkerBytes;

        used += WriteDetail(fromDetail, output + used, capacity - used, ref header);

        *(Header*)output = header;
        return used;
    }

    private static int WriteRing(RecordRing ring, int columns, long from, byte* output, int capacity, out long first, out long head, out int count, long upTo = long.MaxValue)
    {
        uint* previous = stackalloc uint[columns];
        Clear(previous, columns);

        byte* row = stackalloc byte[ring.RecordBytes];

        head = Math.Min(ring.Head, upTo);
        first = Math.Max(Math.Max(from, ring.Oldest), 0L);
        count = 0;

        int used = 0;

        for (long sequence = first; sequence < head; sequence++)
        {
            if (!ring.TryCopy(sequence, row))
                break;

            int bytes = DeltaCodec.Encode((uint*)row, 1, columns, previous, output + used, capacity - used);
            if (bytes < 0)
                break;

            used += bytes;
            count++;
        }

        return used;
    }

    private static int WriteDetail(long from, byte* output, int capacity, ref Header header)
    {
        // Nothing new since the last pull, so don't send it again.
        if (Census.DetailSequence <= from)
        {
            header.DetailSequence = from;
            return 0;
        }

        int playerColumns = sizeof(CensusPlayer) / sizeof(uint);
        int factionColumns = sizeof(CensusFaction) / sizeof(uint);
        CensusPlayer* players = stackalloc CensusPlayer[Census.MaxPlayers];
        CensusFaction* factions = stackalloc CensusFaction[Census.MaxFactions];

        if (!Census.TryCopyDetail(players, factions, out int playerCount, out int factionCount, out int dropped, out long sequence))
        {
            header.DetailSequence = from;
            return 0;
        }

        uint* previous = stackalloc uint[Math.Max(playerColumns, factionColumns)];
        Clear(previous, playerColumns);

        int playerBytes = DeltaCodec.Encode((uint*)players, playerCount, playerColumns, previous, output, capacity);
        Clear(previous, factionColumns);

        int factionBytes = playerBytes < 0
            ? -1
            : DeltaCodec.Encode((uint*)factions, factionCount, factionColumns, previous, output + playerBytes, capacity - playerBytes);

        if (factionBytes < 0)
        {
            // It doesn't fit behind this batch's rows. Leaving the sequence alone brings it next pull.
            header.DetailSequence = from;
            return 0;
        }

        header.DetailSequence = sequence;
        header.Players = playerCount;
        header.Factions = factionCount;
        header.Dropped = dropped;
        header.DetailBytes = playerBytes + factionBytes;
        return header.DetailBytes;
    }

    private static void Clear(uint* values, int count)
    {
        for (int i = 0; i < count; i++)
            values[i] = 0U;
    }
}
