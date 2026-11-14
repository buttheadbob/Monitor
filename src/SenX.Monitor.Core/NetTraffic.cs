using System.Runtime.InteropServices;
using System.Threading;

namespace SenX.Monitor.Core;

/// <summary>What a sent packet was for.</summary>
public enum NetKind
{
    Other = 0,

    /// <summary>Where things are: positions, velocities and the like, sent again and again.</summary>
    StateSync = 1,

    /// <summary>RPCs, including ones passed on from one client to the others.</summary>
    Events = 2,

    /// <summary>Things coming into a player's view and leaving it. Large when grids stream in.</summary>
    Replication = 3,
}

/// <summary>One peer's running totals. Must keep the peer first, because this is also a table slot.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PeerTraffic
{
    public long Peer;
    public long SentBytes;
    public long ReceivedBytes;
    public long FailedSends;
    public long Rpcs;

    // What the last census pass saw, so a pass can work out what's new.
    public long SeenSent;
    public long SeenReceived;
    public long SeenFailed;
    public long SeenRpcs;
}

/// <summary>One RPC's running totals. Must keep the key first, because this is also a table slot.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RpcTraffic
{
    public long Key;
    public long Bytes;
    public long Events;
    public long SeenBytes;
    public long SeenEvents;
}

/// <summary>What one peer did since the last time it was asked about.</summary>
public struct PeerDelta
{
    public long SentBytes;
    public long ReceivedBytes;
    public long FailedSends;
    public long Rpcs;
}

/// <summary>
/// Counts bytes in and out by peer, where packets enter and leave the game, so the figures are counted
/// and not estimated. Sent means handed to Steam or EOS, which may still be holding it: that's what a
/// player's queue is. Runs all the time, because it only costs a table lookup per packet on the network
/// thread, nothing on the game thread, and sim speed can look fine while players are rubberbanding.
///
/// The network thread is the only writer of bytes and of the table's shape. The game thread is the only
/// writer of RPC counts and the seen fields, and it only ever finds slots. A peer's slot is never
/// removed, so nothing is emptied under a reader. Should a very long uptime see more peers than fit,
/// later ones only lose their own row, and the totals stay right.
/// </summary>
public static unsafe class NetTraffic
{
    /// <summary>Different peers seen between restarts, not players online.</summary>
    public const int PeerSlots = 1 << 11;

    /// <summary>Different RPCs sent between restarts. The game has more than this, and uses far fewer.</summary>
    public const int RpcSlots = 1 << 11;

    private const int Kinds = 4;
    private const int KilobyteShift = 10;

    private static OpenTable<PeerTraffic> s_peers;
    private static OpenTable<RpcTraffic> s_sentRpcs;
    private static long* s_sent;
    private static long* s_rolledSent;
    private static long s_received;
    private static long s_failed;
    private static long s_rpcs;
    private static long s_rolledReceived;
    private static long s_rolledRpcs;
    private static long s_sentRpcsTakenAt;
    private static bool s_ready;

    /// <summary>Failed sends since the server started. The game only writes these to its log.</summary>
    public static long FailedSends => Volatile.Read(ref s_failed);

    internal static void Initialize()
    {
        if (s_ready)
            return;

        s_peers = OpenTable<PeerTraffic>.Allocate(PeerSlots);
        s_sentRpcs = OpenTable<RpcTraffic>.Allocate(RpcSlots);
        s_sent = Native.Allocate<long>(Kinds);
        s_rolledSent = Native.Allocate<long>(Kinds);
        s_ready = true;
    }

    /// <summary>A packet handed to the network for one peer. Network thread only.</summary>
    public static void Sent(ulong peer, int bytes, NetKind kind, bool accepted)
    {
        PeerTraffic* slot = Slot(peer);

        if (!accepted)
        {
            s_failed++;

            if (slot != null)
                slot->FailedSends++;

            return;
        }

        s_sent[(int)kind] += bytes;

        if (slot != null)
            slot->SentBytes += bytes;
    }

    /// <summary>A packet that arrived from one peer. Network thread only.</summary>
    public static void Received(ulong peer, uint bytes)
    {
        s_received += bytes;

        PeerTraffic* slot = Slot(peer);

        if (slot != null)
            slot->ReceivedBytes += bytes;
    }

    /// <summary>An RPC from one peer. Game thread only. It only finds a slot, because a peer that sent one has already been received from.</summary>
    public static void NoteRpc(ulong peer)
    {
        s_rpcs++;

        PeerTraffic* slot = peer == 0UL ? null : s_peers.Find((long)peer);

        if (slot != null)
            slot->Rpcs++;
    }

    /// <summary>
    /// An RPC going out, as bytes across all who'll get it. Any thread, because the game can raise events
    /// from any thread. Returns true the first time an RPC is seen, so the caller can note its name.
    /// </summary>
    public static bool SentRpc(long key, long bytes)
    {
        RpcTraffic* slot = s_sentRpcs.ClaimShared(key, out bool created);

        if (slot == null)
            return false;

        Interlocked.Add(ref slot->Bytes, bytes);
        Interlocked.Increment(ref slot->Events);
        return created;
    }

    /// <summary>
    /// Moves what's new for each RPC into a table, and returns the seconds it covers, for a rate. Bytes go
    /// in the main column, because that's what a table ranks by. With no table, this only starts a new
    /// stretch. Allocates nothing. One caller at a time: runs and sweeps never overlap.
    /// </summary>
    internal static double TakeSentRpcs(RollupTable? into)
    {
        long now = Clock.Now();
        double seconds = s_sentRpcsTakenAt == 0L ? Sweep.PeriodSeconds : (now - s_sentRpcsTakenAt) / (double)Clock.Frequency;
        s_sentRpcsTakenAt = now;

        for (int i = 0; i < RpcSlots; i++)
        {
            RpcTraffic* slot = s_sentRpcs.At(i);
            long key = Volatile.Read(ref slot->Key);

            if (key == 0L)
                continue;

            long bytes = Volatile.Read(ref slot->Bytes);
            long events = Volatile.Read(ref slot->Events);
            long newBytes = bytes - slot->SeenBytes;
            long newEvents = events - slot->SeenEvents;

            slot->SeenBytes = bytes;
            slot->SeenEvents = events;

            if (into != null && newBytes > 0L)
                into.Add(key, newBytes, 0L, newEvents, 0U, key);
        }

        return seconds;
    }

    /// <summary>What a peer did since the last time this was called for them. Game thread only.</summary>
    public static PeerDelta TakeDelta(ulong peer)
    {
        PeerTraffic* slot = peer == 0UL ? null : s_peers.Find((long)peer);

        if (slot == null)
            return default;

        long sent = Volatile.Read(ref slot->SentBytes);
        long received = Volatile.Read(ref slot->ReceivedBytes);
        long failed = Volatile.Read(ref slot->FailedSends);
        long rpcs = slot->Rpcs;

        var delta = new PeerDelta
        {
            SentBytes = sent - slot->SeenSent,
            ReceivedBytes = received - slot->SeenReceived,
            FailedSends = failed - slot->SeenFailed,
            Rpcs = rpcs - slot->SeenRpcs,
        };

        slot->SeenSent = sent;
        slot->SeenReceived = received;
        slot->SeenFailed = failed;
        slot->SeenRpcs = rpcs;
        return delta;
    }

    /// <summary>Fills in one second of history. Allocates nothing, and is safe while the network thread counts.</summary>
    internal static void Roll(RollupRecord* row)
    {
        long sent = 0L;

        for (int kind = 0; kind < Kinds; kind++)
        {
            uint kilobytes = KilobytesSince(ref s_sent[kind], ref s_rolledSent[kind]);
            sent += kilobytes;

            switch ((NetKind)kind)
            {
                case NetKind.StateSync:
                    row->StateSyncKb = kilobytes;
                    break;
                case NetKind.Events:
                    row->EventsKb = kilobytes;
                    break;
                case NetKind.Replication:
                    row->ReplicationKb = kilobytes;
                    break;
            }
        }

        row->SentKb = (uint)sent;
        row->ReceivedKb = KilobytesSince(ref s_received, ref s_rolledReceived);

        long rpcs = s_rpcs;
        row->RpcsIn = (uint)(rpcs - s_rolledRpcs);
        s_rolledRpcs = rpcs;
    }

    /// <summary>Lets go of what built up while paused, or the next row would hold all of it in one second.</summary>
    internal static void Skip()
    {
        for (int kind = 0; kind < Kinds; kind++)
            s_rolledSent[kind] = Volatile.Read(ref s_sent[kind]);

        s_rolledReceived = Volatile.Read(ref s_received);
        s_rolledRpcs = s_rpcs;
    }

    // From running totals, so rounding never adds up to a drift over a long uptime.
    private static uint KilobytesSince(ref long total, ref long rolled)
    {
        long now = Volatile.Read(ref total);
        long kilobytes = (now >> KilobyteShift) - (rolled >> KilobyteShift);
        rolled = now;
        return (uint)kilobytes;
    }

    private static PeerTraffic* Slot(ulong peer) => peer == 0UL ? null : s_peers.Claim((long)peer, out bool _);

    internal static void ResetForTests()
    {
        s_peers.Clear();
        s_sentRpcs.Clear();
        s_sentRpcsTakenAt = 0L;

        for (int kind = 0; kind < Kinds; kind++)
        {
            s_sent[kind] = 0L;
            s_rolledSent[kind] = 0L;
        }

        s_received = 0L;
        s_failed = 0L;
        s_rpcs = 0L;
        s_rolledReceived = 0L;
        s_rolledRpcs = 0L;
    }
}
