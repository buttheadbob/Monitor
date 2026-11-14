using System.Runtime.InteropServices;

namespace SenX.Monitor.Core;

/// <summary>One object whose <see cref="ThreadUse"/> changed and held.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ThreadJump
{
    public long Key;
    public long Representative;
    public GroupDimension Dimension;
    public ThreadUse From;
    public ThreadUse To;

    /// <summary>Its figures from the sweep that settled it, per frame, in 100 ns units.</summary>
    public long MainUnits;
    public long WorkerUnits;
    public long CentiCallsPerFrame;
    public uint MaxUnits;
}

/// <summary>
/// Notices when something moves between the game thread and worker threads, such as a grid whose update stops
/// running in parallel. Each background total says which threads every object's time went on, and a change
/// counts once the new answer holds for <see cref="SweepsToSettle"/> totals in a row, so a row near the line
/// can't flap. Runs on the low priority thread inside a sweep, so the game thread does nothing new, and a jump
/// never sets anything off. Allocates only once, the first time each list is seen.
/// </summary>
public static unsafe class ThreadJumps
{
    /// <summary>How many totals in a row a new answer has to hold for.</summary>
    public const int SweepsToSettle = 2;

    /// <summary>Under 1 µs a frame there's too little time to say which thread something is on.</summary>
    public const double LeastUnitsPerFrame = 10d;

    /// <summary>The most jumps kept from one total, costliest first. More only happens when the game changes how it schedules everything.</summary>
    public const int MostPerSweep = 40;

    /// <summary>The lists followed. Owners and factions are left out, because they're sums of grids.</summary>
    internal static readonly GroupDimension[] Followed =
    {
        GroupDimension.Grids,
        GroupDimension.BlockTypes,
        GroupDimension.Definitions,
        GroupDimension.EntityTypes,
        GroupDimension.Session,
        GroupDimension.Logic,
        GroupDimension.Callbacks,
        GroupDimension.Scripts,
        GroupDimension.Rpcs,
        GroupDimension.Mods,
        GroupDimension.ModParts,
    };

    private static readonly System.Action<RollupRow> s_follow = Follow;
    private static readonly Tracker?[] s_trackers = new Tracker?[Followed.Length];

    private static ThreadJump* s_found;
    private static int s_count;
    private static int s_total;

    // Set for each list while it's visited, because a visitor takes only the row.
    private static Tracker? s_tracker;
    private static CaptureResult? s_result;
    private static GroupDimension s_dimension;

    [StructLayout(LayoutKind.Sequential)]
    private struct Slot
    {
        public long Key;
        public ThreadUse Settled;
        public ThreadUse Candidate;
        public int Held;
    }

    private sealed class Tracker
    {
        public OpenTable<Slot> Last;
        public OpenTable<Slot> Next;
    }

    /// <summary>Jumps the latest total found, costliest first.</summary>
    internal static int Count => s_count;

    /// <summary>Every jump the latest total found, including ones past <see cref="MostPerSweep"/>.</summary>
    internal static int Total => s_total;

    internal static ThreadJump At(int index) => s_found[index];

    /// <summary>Compares a total with the ones before. Called by the sweep on the low priority thread.</summary>
    internal static void Update(CaptureResult result)
    {
        if (s_found == null)
            s_found = Native.Allocate<ThreadJump>(MostPerSweep);

        s_count = 0;
        s_total = 0;
        s_result = result;

        for (int i = 0; i < Followed.Length; i++)
        {
            RollupTable table = TableOf(result, Followed[i]);
            Tracker tracker = s_trackers[i] ??= NewTracker(table.Capacity);

            tracker.Next.Clear();
            s_tracker = tracker;
            s_dimension = Followed[i];
            table.Visit(s_follow);

            // Anything not in this total has gone, and is forgotten.
            (tracker.Last, tracker.Next) = (tracker.Next, tracker.Last);
        }

        s_tracker = null;
        s_result = null;
    }

    private static Tracker NewTracker(int capacity) => new()
    {
        Last = OpenTable<Slot>.Allocate(capacity),
        Next = OpenTable<Slot>.Allocate(capacity),
    };

    private static void Follow(RollupRow row)
    {
        Tracker tracker = s_tracker!;
        Slot* slot = tracker.Next.Claim(row.Key, out _);

        // A full table only means this one isn't followed this time.
        if (slot == null)
            return;

        Slot* last = tracker.Last.Find(row.Key);
        double main = s_result!.PerFrame(row.MainUnits);
        double worker = s_result.PerFrame(row.WorkerUnits);
        ThreadUse now = main + worker < LeastUnitsPerFrame ? ThreadUse.None : ThreadUses.Of(row.MainUnits, row.WorkerUnits);

        if (last == null)
        {
            slot->Settled = now;
            slot->Candidate = ThreadUse.None;
            slot->Held = 0;
            return;
        }

        *slot = *last;

        // Too little time to say breaks a run of totals, so a change still needs two in a row after it.
        if (now == ThreadUse.None)
        {
            slot->Candidate = ThreadUse.None;
            slot->Held = 0;
            return;
        }

        if (slot->Settled == ThreadUse.None)
        {
            slot->Settled = now;
            return;
        }

        if (now == slot->Settled)
        {
            slot->Candidate = ThreadUse.None;
            slot->Held = 0;
            return;
        }

        if (now == slot->Candidate)
        {
            slot->Held++;
        }
        else
        {
            slot->Candidate = now;
            slot->Held = 1;
        }

        if (slot->Held < SweepsToSettle)
            return;

        Found(row, slot->Settled, now, main, worker);
        slot->Settled = now;
        slot->Candidate = ThreadUse.None;
        slot->Held = 0;
    }

    private static void Found(in RollupRow row, ThreadUse from, ThreadUse to, double main, double worker)
    {
        s_total++;

        long cost = (long)(main + worker);
        if (s_count == MostPerSweep && cost <= s_found[s_count - 1].MainUnits + s_found[s_count - 1].WorkerUnits)
            return;

        int at = s_count < MostPerSweep ? s_count++ : s_count - 1;
        while (at > 0 && s_found[at - 1].MainUnits + s_found[at - 1].WorkerUnits < cost)
        {
            s_found[at] = s_found[at - 1];
            at--;
        }

        s_found[at] = new ThreadJump
        {
            Key = row.Key,
            Representative = row.Representative,
            Dimension = s_dimension,
            From = from,
            To = to,
            MainUnits = (long)main,
            WorkerUnits = (long)worker,
            CentiCallsPerFrame = (long)s_result!.PerFrame(row.Calls * 100L),
            MaxUnits = row.MaxUnits,
        };
    }

    private static RollupTable TableOf(CaptureResult result, GroupDimension dimension) => dimension switch
    {
        GroupDimension.Grids => result.Grids,
        GroupDimension.BlockTypes => result.BlockTypes,
        GroupDimension.Definitions => result.Definitions,
        GroupDimension.EntityTypes => result.EntityTypes,
        GroupDimension.Session => result.Session,
        GroupDimension.Logic => result.Logic,
        GroupDimension.Callbacks => result.Callbacks,
        GroupDimension.Scripts => result.Scripts,
        GroupDimension.Rpcs => result.Rpcs,
        GroupDimension.Mods => result.Mods,
        _ => result.ModParts,
    };

    internal static void ResetForTests()
    {
        for (int i = 0; i < s_trackers.Length; i++)
        {
            if (s_trackers[i] is { } tracker)
            {
                tracker.Last.Clear();
                tracker.Next.Clear();
            }
        }

        s_count = 0;
        s_total = 0;
    }
}
