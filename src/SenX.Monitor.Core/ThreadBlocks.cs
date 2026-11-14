using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SenX.Monitor.Core;

/// <summary>Sizes for detail memory. Everything is allocated once from this.</summary>
public struct DetailLayout
{
    public int MainSlots;
    public int WorkerSlots;
    public int KeySlots;
    public int SlowEntries;
    public int WorkerBlocks;

    public static DetailLayout Default(int workerThreads) => new()
    {
        MainSlots = 1 << 18,
        WorkerSlots = 1 << 14,
        KeySlots = 1 << 12,
        SlowEntries = 1 << 10,
        WorkerBlocks = workerThreads + 8,
    };

    public readonly unsafe long TotalBytes
    {
        get
        {
            long siteBytes = 3L * Gates.Capacity * sizeof(long);
            long shared = siteBytes + (long)KeySlots * sizeof(KeySlot) + (long)SlowEntries * sizeof(SlowEntry);
            long main = shared + (long)MainSlots * sizeof(EntitySlot);
            long worker = shared + (long)WorkerSlots * sizeof(EntitySlot);
            return main + worker * WorkerBlocks;
        }
    }
}

/// <summary>
/// Everything one thread records. Written only by its owner, so nothing here needs Interlocked.
/// </summary>
public unsafe struct ThreadBlock
{
    public int Generation;
    public int Index;
    public OpenTable<EntitySlot> Entities;
    public OpenTable<KeySlot> Keys;

    /// <summary>Scaled time per site, in raw clock ticks.</summary>
    public long* SiteTicks;
    public long* SiteSamples;

    /// <summary>Scaled raw ticks that found no table slot. Keeps totals right and feeds the coverage figure.</summary>
    public long* OverflowTicks;

    public SlowEntry* Slow;
    public uint SlowMask;
    public uint SlowHead;
}

/// <summary>
/// The fixed pool of per-thread blocks. A thread reaches its block through a TLS pointer on the
/// timed path only. Blocks are bound per period through a generation, so threads that have gone
/// away hold nothing, and a thread pool bigger than expected drops to a counter instead of growing.
/// </summary>
public static unsafe class ThreadBlocks
{
    [ThreadStatic] private static ThreadBlock* t_block;
    [ThreadStatic] private static bool t_isMain;

    private static ThreadBlock** s_blocks;
    private static int s_count;
    private static int s_next;
    private static int s_generation;
    private static long s_unattached;

    /// <summary>True once detail memory exists.</summary>
    public static bool Ready => s_blocks != null;

    public static int Count => s_count;

    public static int Generation => s_generation;

    /// <summary>Records lost because more threads recorded than there are blocks.</summary>
    public static long Unattached => Interlocked.Read(ref s_unattached);

    /// <summary>
    /// Allocates and clears all detail memory. Run this on a background thread at game ready:
    /// it touches every page, and doing that during lag would hurt the game.
    /// </summary>
    public static void Initialize(in DetailLayout layout)
    {
        if (s_blocks != null)
            return;

        int count = layout.WorkerBlocks + 1;
        ThreadBlock** blocks = (ThreadBlock**)Native.AllocateZeroed(count * sizeof(ThreadBlock*));

        for (int i = 0; i < count; i++)
        {
            ThreadBlock* block = Native.Allocate<ThreadBlock>(1);
            block->Index = i;
            block->Entities = OpenTable<EntitySlot>.Allocate(i == 0 ? layout.MainSlots : layout.WorkerSlots);
            block->Keys = OpenTable<KeySlot>.Allocate(layout.KeySlots);
            block->SiteTicks = Native.Allocate<long>(Gates.Capacity);
            block->SiteSamples = Native.Allocate<long>(Gates.Capacity);
            block->OverflowTicks = Native.Allocate<long>(Gates.Capacity);
            block->Slow = Native.Allocate<SlowEntry>(layout.SlowEntries);
            block->SlowMask = (uint)layout.SlowEntries - 1;
            blocks[i] = block;
        }

        s_count = count;
        s_next = 1;
        s_blocks = blocks;
    }

    /// <summary>Gives the calling thread block 0, the one with the large table. Call on the game thread.</summary>
    public static void BindMainThread()
    {
        ThreadBlock* block = s_blocks[0];
        block->Generation = s_generation;
        t_block = block;
        t_isMain = true;
    }

    /// <summary>The calling thread's block, or null if detail memory is missing or the pool is exhausted.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ThreadBlock* Current()
    {
        ThreadBlock* block = t_block;
        return block != null && block->Generation == s_generation ? block : Claim();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ThreadBlock* Claim()
    {
        if (s_blocks == null)
            return null;

        if (t_isMain)
        {
            ThreadBlock* main = s_blocks[0];
            main->Generation = s_generation;
            t_block = main;
            return main;
        }

        if (Volatile.Read(ref s_next) >= s_count)
        {
            t_block = null;
            return null;
        }

        int index = Interlocked.Increment(ref s_next) - 1;
        if (index >= s_count)
        {
            t_block = null;
            return null;
        }

        ThreadBlock* block = s_blocks[index];
        block->Generation = s_generation;
        t_block = block;
        return block;
    }

    public static ThreadBlock* At(int index) => s_blocks[index];

    internal static void CountUnattached() => Interlocked.Increment(ref s_unattached);

    /// <summary>
    /// Starts a new period. Call only after gates have been closed for two frame boundaries and
    /// <see cref="ClearAll"/> has finished, so stragglers can't write into the new period.
    /// </summary>
    public static void BeginPeriod()
    {
        Volatile.Write(ref s_next, 1);
        Interlocked.Increment(ref s_generation);
    }

    /// <summary>Clears every block. Run on the background thread so a later capture finds clean memory.</summary>
    public static void ClearAll()
    {
        for (int i = 0; i < s_count; i++)
        {
            ThreadBlock* block = s_blocks[i];
            block->Entities.Clear();
            block->Keys.Clear();
            Native.Clear(block->SiteTicks, Gates.Capacity * sizeof(long));
            Native.Clear(block->SiteSamples, Gates.Capacity * sizeof(long));
            Native.Clear(block->OverflowTicks, Gates.Capacity * sizeof(long));
            Native.Clear(block->Slow, (long)(block->SlowMask + 1) * sizeof(SlowEntry));
            block->SlowHead = 0;
        }
    }
}
