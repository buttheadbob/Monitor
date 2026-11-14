using System;
using System.Runtime.InteropServices;

using System.Threading;

namespace SenX.Monitor.Core;

[Flags]
public enum FrameFlags : ushort
{
    None = 0,

    /// <summary>The simulation ran this frame. Absent while the server is paused.</summary>
    Simulated = 1,
    Gen0 = 2,
    Gen1 = 4,
    Gen2 = 8,
}

/// <summary>One frame in the flight recorder. Times are in 100 ns units.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FrameRecord
{
    public ulong Frame;
    public uint PeriodUnits;
    public uint BusyUnits;
    public uint AllocatedKb;
    public FrameFlags Flags;
    private readonly ushort _padding;

    public fixed uint ScopeUnits[Scope.Count];
}

/// <summary>Marks on a second of history.</summary>
[Flags]
public enum RollupFlags : uint
{
    None = 0,

    /// <summary>The server was paused just before this row, so charts should show a gap, not a line.</summary>
    AfterGap = 1,
}

/// <summary>One second of history. This is what Quasar collects.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct RollupRecord
{
    public long UnitsSinceStart;
    public uint Frames;
    public uint SimulatedFrames;
    public uint WindowUnits;
    public uint FrameP50;
    public uint FrameP95;
    public uint FrameP99;
    public uint FrameMax;
    public uint Gen0;
    public uint Gen1;
    public uint Gen2;
    public uint AllocatedKb;
    public RollupFlags Flags;

    /// <summary>Sent in this second, of every kind.</summary>
    public uint SentKb;
    public uint ReceivedKb;
    public uint StateSyncKb;
    public uint EventsKb;
    public uint ReplicationKb;

    /// <summary>RPCs received from clients in this second.</summary>
    public uint RpcsIn;

    public fixed uint ScopeUnits[Scope.Count];

    /// <summary>Simulated frames against the 60 per second the game aims for.</summary>
    public readonly double SimSpeed =>
        WindowUnits == 0 ? 0d : SimulatedFrames / (WindowUnits / (double)Clock.UnitsPerSecond * 60d);
}

/// <summary>
/// Runs once per frame at the end of the frame hook, on the game thread. It commits the tree into
/// the flight recorder and, once a second, into the rollup ring. It only copies numbers, so it
/// never allocates, and its own cost shows up in the tree as <see cref="Scope.Monitor"/>.
/// </summary>
public static unsafe class FrameEngine
{
    /// <summary>A little over 4.5 minutes at 60 frames per second.</summary>
    public const int FlightFrames = 1 << 14;

    /// <summary>A little over an hour at one row per second.</summary>
    public const int RollupRows = 1 << 12;

    // This many frames without simulation means the server is really paused, not just between frames.
    private const int PausedAfterFrames = 30;

    private static RecordRing? s_flight;
    private static RecordRing? s_rollups;
    private static LogHistogram? s_histogram;

    private static long s_firstStart;
    private static long s_lastStart;
    private static long s_windowStart;
    private static long s_windowAllocated;
    private static uint s_frames;
    private static uint s_simulated;
    private static uint s_max;
    private static int s_idleFrames;
    private static int s_gen0;
    private static int s_gen1;
    private static int s_gen2;
    private static int s_windowGen0;
    private static int s_windowGen1;
    private static int s_windowGen2;
    private static long s_lastAllocated;
    private static ulong s_frame;
    private static bool s_afterGap;

    private static uint* s_windowUnits;

    /// <summary>Called once a second with the row just written. Off the per-frame path.</summary>
    public static Action? SecondElapsed;

    public static RecordRing Flight => s_flight!;

    public static RecordRing Rollups => s_rollups!;

    public static ulong Frame => s_frame;

    /// <summary>How long the last frame took, in 100 ns units.</summary>
    public static uint LastFrameUnits { get; private set; }

    /// <summary>True when frames are running but the simulation is not.</summary>
    public static bool Paused => s_idleFrames >= PausedAfterFrames;

    public static void Initialize()
    {
        if (s_flight != null)
            return;

        s_flight = new RecordRing(sizeof(FrameRecord), FlightFrames);
        s_rollups = new RecordRing(sizeof(RollupRecord), RollupRows);
        s_histogram = new LogHistogram();
        s_windowUnits = Native.Allocate<uint>(Scope.Count);
        NetTraffic.Initialize();
    }

    /// <summary>Starts the clocks. Call when the game is ready, so loading never counts as a frame.</summary>
    public static void Start()
    {
        s_stopped = false;

        // Hooks run during world load too, and none of that belongs in the first frame.
        for (int scope = 1; scope < Scope.Count; scope++)
        {
            TreeCounters.RawTicks[scope] = 0L;
            TreeCounters.Calls[scope] = 0L;
        }

        long now = Clock.Now();
        s_firstStart = now;
        s_lastStart = now;
        s_windowStart = now;
        s_gen0 = GC.CollectionCount(0);
        s_gen1 = GC.CollectionCount(1);
        s_gen2 = GC.CollectionCount(2);
        s_lastAllocated = GcProbe.AllocatedOnThread();
    }

    /// <summary>Time since the start, for rows other than rollups.</summary>
    internal static long UnitsSinceStart(long now) => Clock.ToUnits(now - s_firstStart);

    private static bool s_stopped;
    private static Exception? s_fault;

    /// <summary>True once <see cref="Start"/> has run.</summary>
    public static bool Started => s_firstStart != 0L;

    /// <summary>What stopped the frame hook, once. Whoever takes it shuts Monitor down off the frame hook.</summary>
    public static Exception? TakeFault() => Interlocked.Exchange(ref s_fault, null);

    /// <summary>
    /// Ends a frame. <paramref name="frameStart"/> is the raw clock when the frame began. This is called from inside
    /// the game's own frame, where nothing would catch an exception and the server would die, so a failure closes
    /// every gate, stops this and leaves a note. The game carries on as if Monitor weren't there.
    /// </summary>
    public static void OnFrameEnd(long frameStart)
    {
        if (s_firstStart == 0L || s_stopped)
            return;

        try
        {
            EndFrame(frameStart);
        }
        catch (Exception exception)
        {
            s_stopped = true;
            s_fault = exception;
            Gates.CloseAll();
        }
    }

    private static void EndFrame(long frameStart)
    {
        long now = Clock.Now();
        uint busy = Narrow(Clock.ToUnits(now - frameStart));
        uint period = Narrow(Clock.ToUnits(frameStart - s_lastStart));
        s_lastStart = frameStart;
        LastFrameUnits = busy;

        bool simulated = TreeCounters.Calls[Scope.Components] != 0L;
        TreeCounters.Add(Scope.Frame, now - frameStart);

        FrameRecord* record = (FrameRecord*)s_flight!.Reserve();
        record->Frame = s_frame;
        record->PeriodUnits = period;
        record->BusyUnits = busy;

        for (int scope = 1; scope < Scope.Count; scope++)
        {
            uint units = Narrow(Clock.ToUnits(TreeCounters.RawTicks[scope]));
            record->ScopeUnits[scope] = units;
            s_windowUnits[scope] += units;
            TreeCounters.RawTicks[scope] = 0L;
            TreeCounters.Calls[scope] = 0L;
        }

        FrameFlags flags = simulated ? FrameFlags.Simulated : FrameFlags.None;
        flags |= ReadCollections();

        long allocated = GcProbe.AllocatedOnThread();
        long delta = allocated - s_lastAllocated;
        s_lastAllocated = allocated;
        s_windowAllocated += delta;

        record->AllocatedKb = Narrow(delta >> 10);
        record->Flags = flags;
        s_flight.Commit();

        s_frames++;
        if (simulated)
        {
            s_simulated++;
            s_idleFrames = 0;
            s_histogram!.Record(busy);
            if (busy > s_max)
                s_max = busy;
        }
        else
        {
            s_idleFrames++;
        }

        s_frame++;
        Recorder.Frame = s_frame;
        Sampler.NextFrame();

        if (now - s_windowStart >= Clock.Frequency)
            CommitSecond(now);

        Sweep.OnFrame(now, simulated, Paused);

        if (Capture.Active)
            Capture.OnFrame(record, simulated, Paused);

        DetailController.OnFrame(simulated, Paused, busy);

        TreeCounters.Add(Scope.Monitor, Clock.Now() - now);
    }

    private static FrameFlags ReadCollections()
    {
        FrameFlags flags = FrameFlags.None;

        int gen0 = GC.CollectionCount(0);
        if (gen0 != s_gen0)
        {
            s_windowGen0 += gen0 - s_gen0;
            s_gen0 = gen0;
            flags |= FrameFlags.Gen0;
        }

        int gen1 = GC.CollectionCount(1);
        if (gen1 != s_gen1)
        {
            s_windowGen1 += gen1 - s_gen1;
            s_gen1 = gen1;
            flags |= FrameFlags.Gen1;
        }

        int gen2 = GC.CollectionCount(2);
        if (gen2 != s_gen2)
        {
            s_windowGen2 += gen2 - s_gen2;
            s_gen2 = gen2;
            flags |= FrameFlags.Gen2;
        }

        return flags;
    }

    private static void CommitSecond(long now)
    {
        // A paused server can sit for hours, and empty rows would push real history out of the ring.
        if (Paused)
        {
            s_afterGap = true;
            ClearWindow(now);
            NetTraffic.Skip();
            SecondElapsed?.Invoke();
            return;
        }

        RollupRecord* row = (RollupRecord*)s_rollups!.Reserve();
        row->UnitsSinceStart = Clock.ToUnits(now - s_firstStart);
        row->Frames = s_frames;
        row->SimulatedFrames = s_simulated;
        row->WindowUnits = Narrow(Clock.ToUnits(now - s_windowStart));
        row->FrameP50 = Narrow(s_histogram!.Percentile(0.50));
        row->FrameP95 = Narrow(s_histogram.Percentile(0.95));
        row->FrameP99 = Narrow(s_histogram.Percentile(0.99));
        row->FrameMax = s_max;
        row->Gen0 = (uint)s_windowGen0;
        row->Gen1 = (uint)s_windowGen1;
        row->Gen2 = (uint)s_windowGen2;
        row->AllocatedKb = Narrow(s_windowAllocated >> 10);
        row->Flags = s_afterGap ? RollupFlags.AfterGap : RollupFlags.None;
        s_afterGap = false;
        NetTraffic.Roll(row);

        for (int scope = 1; scope < Scope.Count; scope++)
            row->ScopeUnits[scope] = s_windowUnits[scope];

        s_rollups.Commit();

        ClearWindow(now);
        LagWatch.OnSecond(row, now);
        SecondElapsed?.Invoke();
    }

    private static void ClearWindow(long now)
    {
        for (int scope = 1; scope < Scope.Count; scope++)
            s_windowUnits[scope] = 0U;

        s_histogram!.Clear();
        s_frames = 0U;
        s_simulated = 0U;
        s_max = 0U;
        s_windowGen0 = 0;
        s_windowGen1 = 0;
        s_windowGen2 = 0;
        s_windowAllocated = 0L;
        s_windowStart = now;
    }

    // Lets tests cross a second boundary without waiting a real second.
    internal static void CommitSecondNow() => CommitSecond(Clock.Now());

    /// <summary>The most recent rollup row. Returns false until the first second has passed.</summary>
    public static bool TryReadLatest(out RollupRecord row)
    {
        RollupRecord copy = default;
        long sequence = s_rollups!.Head - 1L;
        bool ok = s_rollups.TryCopy(sequence, (byte*)&copy);
        row = copy;
        return ok;
    }

    private static uint Narrow(long value) => value > uint.MaxValue ? uint.MaxValue : value < 0L ? 0U : (uint)value;
}
