using System.Runtime.InteropServices;
using System.Threading;

namespace SenX.Monitor.Core;

/// <summary>What a <see cref="GroupRecord"/> totals by.</summary>
public enum GroupDimension : uint
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

    /// <summary>Time by which mod or plugin the code belongs to.</summary>
    Mods = 10,

    /// <summary>The session components, game logic and callbacks inside each mod. The row's representative is its mod.</summary>
    ModParts = 11,

    /// <summary>RPCs from clients, by the method they run.</summary>
    Rpcs = 12,

    /// <summary>RPCs the server sent, ranked by bytes. These rows hold no time.</summary>
    RpcsSent = 13,

    /// <summary>
    /// Things whose time moved between the game thread and worker threads. Not a list of costs: each row is one
    /// jump, with the object's own key and figures, <see cref="GroupRecord.JumpedList"/> saying which list it's
    /// in, and <see cref="GroupRecord.JumpFrom"/> and <see cref="GroupRecord.JumpTo"/> saying where it went.
    /// </summary>
    ThreadJumps = 14,

    /// <summary>Numbered to match how Quasar already looks up faction names.</summary>
    Factions = 100,
}

/// <summary>One line of a background total. Times are per frame, in 100 ns units.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GroupRecord
{
    public long UnitsSinceStart;
    public long Key;

    /// <summary>The heaviest entity in this row, so Quasar can ask for a name.</summary>
    public long Representative;
    public GroupDimension Dimension;
    public uint MainUnits;
    public uint WorkerUnits;

    /// <summary>In hundredths, because RPCs and rare callbacks are far below one a frame.</summary>
    public uint CentiCallsPerFrame;
    public uint MaxUnits;

    /// <summary>For rows that count traffic. Zero for the rest.</summary>
    public uint BytesPerSecond;

    /// <summary>For grids, owners and factions, the blocks the last census found. Zero for the rest.</summary>
    public uint Blocks;

    /// <summary>For owners and factions, the grids the last census found. Zero for the rest.</summary>
    public uint Grids;

    /// <summary>For a thread jump, the list the object is in. Kept in <see cref="Blocks"/>, which jumps don't use.</summary>
    public readonly GroupDimension JumpedList => (GroupDimension)Blocks;

    /// <summary>For a thread jump, where its time was. Kept in <see cref="Grids"/>, which jumps don't use.</summary>
    public readonly ThreadUse JumpFrom => (ThreadUse)(Grids >> 8);

    /// <summary>For a thread jump, where its time is now.</summary>
    public readonly ThreadUse JumpTo => (ThreadUse)(Grids & 0xFFU);

    /// <summary>
    /// For the one thread jump row with no list, how many more jumps that total found past the ones it lists.
    /// Kept in <see cref="MaxUnits"/>. Zero for every other row.
    /// </summary>
    public readonly uint JumpsNotListed => Dimension == GroupDimension.ThreadJumps && Blocks == 0U ? MaxUnits : 0U;
}

/// <summary>
/// Totals what background detail measured, every 30 seconds, for the "what is costing time" charts.
/// Gates close for a few frames, the low priority thread totals and clears the tables, and detail
/// carries on. The tree never stops. Only the top rows of each list are kept, and clearing means
/// the tables can't slowly fill up on a server that runs for weeks.
///
/// In Grouped mode each site is measured only part of the time, so each phase is scaled up by how
/// many frames it was really measured for. Otherwise a group measured a quarter of the time would
/// read a quarter as high, and a grid's phases couldn't be added together.
/// </summary>
public static unsafe class Sweep
{
    public const int PeriodSeconds = 30;

    /// <summary>The top of each list. Admins and charts look at the costliest, and the rest is noise.</summary>
    public const int RowsPerDimension = 20;

    /// <summary>More than the other lists, because a server can run a lot of mods, and every one should get its line.</summary>
    public const int ModRows = 40;

    /// <summary>Enough for the top of each mod's own list.</summary>
    public const int ModPartRows = 80;

    /// <summary>Over an hour of sweeps, with every list full and the most thread jumps: 401 rows a sweep.</summary>
    public const int RingRows = 1 << 16;

    private const int MostRowsInTotal = (12 * RowsPerDimension) + ModRows + ModPartRows + ThreadJumps.MostPerSweep + 1;

    // A worker could still be finishing a call when a gate closes. Two frame ends let it finish.
    private const int SettleFrames = 2;

    private enum State
    {
        Idle,
        Settling,
        Rolling,
    }

    private static readonly System.Action s_total = Total;
    private static readonly System.Action<RollupRow> s_addToFaction = AddToFaction;
    private static readonly System.Action<RollupRow> s_addToMod = AddToMod;

    private static RecordRing? s_rows;
    private static GroupRecord* s_scratch;
    private static int s_scratchRows;
    private static System.Exception? s_fault;
    private static double* s_phaseScale;
    private static double* s_siteScale;
    private static State s_state;
    private static long s_next;
    private static long s_frames;
    private static long s_started;
    private static long s_rolledFrames;
    private static long s_wholeRows;
    private static int s_wait;
    private static bool s_totalsReady;

    public static RecordRing Rows => s_rows!;

    /// <summary>
    /// Where finished totals end in <see cref="Rows"/>. Past this is a total still being written, so anything
    /// reading whole totals stops here. Safe from any thread.
    /// </summary>
    public static long WholeRows => Volatile.Read(ref s_wholeRows);

    /// <summary>What stopped the last total, if anything did. That total is dropped whole. Asking clears it, so the worker logs it once.</summary>
    public static System.Exception? TakeFault() => Interlocked.Exchange(ref s_fault, null);

    /// <summary>
    /// Says whose code a key is, as a stable id, or zero for the game's own. The worker sets this,
    /// because only it can see mods. It's called on the low priority thread.
    /// </summary>
    public static System.Func<long, long>? OwnerOf;

    /// <summary>
    /// Every grid and owner from the latest total, not just the top rows. Null while new ones are being
    /// worked out, and after a run, whose tables can be filtered. Read on the game thread within one
    /// frame, because the state only changes at a frame's end.
    /// </summary>
    public static CaptureResult? LatestTotals => s_totalsReady && s_state == State.Idle && Capture.Idle ? Capture.SharedResult() : null;

    /// <summary>True while a sweep has the gates. A measuring run waits for it, which takes a few frames.</summary>
    public static bool Busy => s_state != State.Idle;

    internal static void Initialize()
    {
        if (s_rows != null)
            return;

        s_rows = new RecordRing(sizeof(GroupRecord), RingRows);
        s_scratch = Native.Allocate<GroupRecord>(MostRowsInTotal);
        s_phaseScale = Native.Allocate<double>(Phase.Count);
        s_siteScale = Native.Allocate<double>(Gates.Capacity);
    }

    /// <summary>Called at the end of every frame. Allocates nothing.</summary>
    internal static void OnFrame(long now, bool simulated, bool paused)
    {
        switch (s_state)
        {
            case State.Idle:
                if (simulated)
                    s_frames++;

                if (now < s_next)
                    return;

                // A run owns the tables, and a paused or switched-off server has nothing new.
                if (paused || !Capture.Idle || !DetailController.Running || !ThreadBlocks.Ready || s_frames == 0L)
                {
                    s_next = now + (PeriodSeconds * Clock.Frequency);
                    return;
                }

                // Under heavy lag a cycle of groups can outlast the period. Sweeping then would drop a
                // whole group from the charts, so this waits, and asks again next frame.
                if (!DetailController.EveryGroupMeasured)
                    return;

                s_next = now + (PeriodSeconds * Clock.Frequency);
                DetailController.Suspend();
                s_wait = SettleFrames;
                s_state = State.Settling;
                break;

            case State.Settling:
                if (--s_wait > 0)
                    return;

                s_rolledFrames = s_frames;
                s_started = now;
                s_totalsReady = false;
                DetailController.PeriodScales(s_rolledFrames, s_phaseScale, s_siteScale);
                s_state = State.Rolling;
                Background.Start(s_total);
                break;

            case State.Rolling:
                if (!Background.Done)
                    return;

                ThreadBlocks.BeginPeriod();
                DetailController.Resume();
                s_frames = 0L;
                s_state = State.Idle;
                s_totalsReady = Background.Fault == null;

                if (Background.Fault != null)
                    Volatile.Write(ref s_fault, Background.Fault);

                break;
        }
    }

    /// <summary>Starts counting again after a run, which leaves the tables clean and owns those frames.</summary>
    internal static void Restart()
    {
        s_frames = 0L;
        s_totalsReady = false;
        DetailController.MarkPeriod();
    }

    private static void Total()
    {
        CaptureResult result = Capture.SharedResult();
        result.Reset();
        result.Roll(null, s_phaseScale, s_siteScale);
        result.SimulatedFrames = s_rolledFrames;

        // Every owner, not just the top rows, or a faction of many small owners would read low.
        result.Owners.Visit(s_addToFaction);

        if (OwnerOf != null)
        {
            result.Session.Visit(s_addToMod);
            result.Logic.Visit(s_addToMod);
            result.Callbacks.Visit(s_addToMod);
            result.Plugins.Visit(s_addToMod);
        }

        result.TrafficSeconds = NetTraffic.TakeSentRpcs(result.RpcsSent);

        // Which threads each thing ran on, against the totals before. Here on this thread, never on the game's.
        ThreadJumps.Update(result);

        long since = FrameEngine.UnitsSinceStart(s_started);

        s_scratchRows = 0;

        Write(result, result.Grids, GroupDimension.Grids, since);
        Write(result, result.BlockTypes, GroupDimension.BlockTypes, since);
        Write(result, result.Definitions, GroupDimension.Definitions, since);
        Write(result, result.Owners, GroupDimension.Owners, since);
        Write(result, result.Factions, GroupDimension.Factions, since);
        Write(result, result.EntityTypes, GroupDimension.EntityTypes, since);
        Write(result, result.Session, GroupDimension.Session, since);
        Write(result, result.Logic, GroupDimension.Logic, since);
        Write(result, result.Callbacks, GroupDimension.Callbacks, since);
        Write(result, result.Scripts, GroupDimension.Scripts, since);
        Write(result, result.Rpcs, GroupDimension.Rpcs, since);
        Write(result, result.Mods, GroupDimension.Mods, since, ModRows);
        Write(result, result.ModParts, GroupDimension.ModParts, since, ModPartRows);
        Write(result, result.RpcsSent, GroupDimension.RpcsSent, since);
        WriteJumps(since);

        // All at once and last, so a fault partway through can't leave part of a total in the ring.
        for (int i = 0; i < s_scratchRows; i++)
        {
            *(GroupRecord*)s_rows!.Reserve() = s_scratch[i];
            s_rows.Commit();
        }

        Volatile.Write(ref s_wholeRows, s_rows!.Head);
    }

    private static void AddToMod(RollupRow row)
    {
        long owner = OwnerOf!(row.Key);

        if (owner == 0L)
            return;

        CaptureResult result = Capture.SharedResult();
        result.Mods.Add(owner, row.MainUnits, row.WorkerUnits, row.Calls, row.MaxUnits, owner);
        result.ModParts.Add(row.Key, row.MainUnits, row.WorkerUnits, row.Calls, row.MaxUnits, owner);
    }

    private static void AddToFaction(RollupRow owner) =>
        Capture.SharedResult().Factions.Add(Census.FactionOf(owner.Key), owner.MainUnits, owner.WorkerUnits, owner.Calls, owner.MaxUnits, owner.Representative);

    private static void Write(CaptureResult result, RollupTable table, GroupDimension dimension, long since, int most = RowsPerDimension)
    {
        RollupRow* rows = stackalloc RollupRow[most];
        int count = table.CopyTop(rows, most);
        bool sized = dimension == GroupDimension.Grids || dimension == GroupDimension.Owners || dimension == GroupDimension.Factions;

        for (int i = 0; i < count; i++)
        {
            RollupRow* row = rows + i;
            GroupRecord* record = s_scratch + s_scratchRows++;

            bool traffic = dimension == GroupDimension.RpcsSent;

            record->UnitsSinceStart = since;
            record->Key = row->Key;
            record->Representative = row->Representative;
            record->Dimension = dimension;
            record->MainUnits = traffic ? 0U : Narrow(result.PerFrame(row->MainUnits));
            record->WorkerUnits = Narrow(result.PerFrame(row->WorkerUnits));
            record->CentiCallsPerFrame = Narrow(result.PerFrame(row->Calls) * 100d);
            record->MaxUnits = row->MaxUnits;
            record->BytesPerSecond = traffic ? Narrow(result.BytesPerSecond(row->MainUnits)) : 0U;

            CensusCount counted = !sized ? default : dimension == GroupDimension.Owners ? Census.CountOfOwner(row->Key) : Census.CountOf(row->Key);
            record->Blocks = counted.Blocks;
            record->Grids = counted.Grids;
        }
    }

    private static void WriteJumps(long since)
    {
        for (int i = 0; i < ThreadJumps.Count; i++)
        {
            ThreadJump jump = ThreadJumps.At(i);
            GroupRecord* record = s_scratch + s_scratchRows++;

            *record = default;
            record->UnitsSinceStart = since;
            record->Key = jump.Key;
            record->Representative = jump.Representative;
            record->Dimension = GroupDimension.ThreadJumps;
            record->MainUnits = Narrow(jump.MainUnits);
            record->WorkerUnits = Narrow(jump.WorkerUnits);
            record->CentiCallsPerFrame = Narrow(jump.CentiCallsPerFrame);
            record->MaxUnits = jump.MaxUnits;
            record->Blocks = (uint)jump.Dimension;
            record->Grids = ((uint)jump.From << 8) | (uint)jump.To;
        }

        // Says how many weren't listed, so a cap is never silent.
        if (ThreadJumps.Total > ThreadJumps.Count)
        {
            GroupRecord* record = s_scratch + s_scratchRows++;

            *record = default;
            record->UnitsSinceStart = since;
            record->Key = RollupTable.NoKey;
            record->Dimension = GroupDimension.ThreadJumps;
            record->MaxUnits = (uint)(ThreadJumps.Total - ThreadJumps.Count);
        }
    }

    private static uint Narrow(double value) => value >= uint.MaxValue ? uint.MaxValue : (uint)value;

    /// <summary>Writes rows as one whole total, so tests of readers can put any row in the ring.</summary>
    internal static void WriteForTests(params GroupRecord[] rows)
    {
        foreach (GroupRecord row in rows)
        {
            *(GroupRecord*)s_rows!.Reserve() = row;
            s_rows.Commit();
        }

        Volatile.Write(ref s_wholeRows, s_rows!.Head);
    }

    /// <summary>Makes a sweep due on the next frame, so tests don't wait 30 seconds.</summary>
    internal static void MakeDue() => s_next = 0L;

    internal static void ResetForTests()
    {
        s_totalsReady = false;
        s_state = State.Idle;
        s_next = long.MaxValue;
        s_frames = 0L;
        OwnerOf = null;
        ThreadJumps.ResetForTests();
    }
}
