using System;

namespace SenX.Monitor.Core;

/// <summary>One call slower than the threshold, and which thread ran it.</summary>
public struct SlowCall
{
    public ulong Frame;

    /// <summary>An entity id, or a key where <see cref="SlowestCalls.IsKeyed"/> says so. A parallel site can hold either.</summary>
    public long Id;

    public long Units;
    public int Site;
    public bool Main;
}

/// <summary>
/// The slowest single calls from every thread's slow ring. An average says what's expensive, and
/// this says what spiked. A fixed array, so reading the rings allocates nothing.
/// </summary>
public sealed unsafe class SlowestCalls
{
    public const int Capacity = 64;

    private readonly SlowCall[] _rows = new SlowCall[Capacity];

    public int Count { get; private set; }

    public SlowCall this[int index] => _rows[index];

    /// <summary>True when a site's calls carry a key rather than an entity id.</summary>
    public static bool IsKeyed(int site)
    {
        switch (site)
        {
            case Site.ParallelBefore:
            case Site.ParallelAfter:
            case Site.GameLogic:
            case Site.EntityLogic:
            case Site.CompositeLogic:
            case Site.SessionBefore:
            case Site.SessionSimulate:
            case Site.SessionAfter:
            case Site.GridCallback:
            case Site.InvokeLater:
            case Site.PhysicsCluster:
            case Site.PluginUpdate:
                return true;
            default:
                return false;
        }
    }

    public void Clear() => Count = 0;

    /// <summary>Adds what the rings hold from this frame on. Rings can outlive a period, so old calls need leaving out.</summary>
    public void Collect(ulong fromFrame)
    {
        int blocks = ThreadBlocks.Count;

        for (int b = 0; b < blocks; b++)
        {
            ThreadBlock* block = ThreadBlocks.At(b);
            uint size = block->SlowMask + 1U;
            uint used = Math.Min(block->SlowHead, size);

            for (uint i = 0U; i < used; i++)
            {
                SlowEntry* entry = block->Slow + i;

                if (entry->Frame >= fromFrame)
                    Keep(entry, b == 0);
            }
        }
    }

    // Slowest first. There are few of these, so a plain insert is fine.
    private void Keep(SlowEntry* entry, bool main)
    {
        if (Count == Capacity && entry->Units <= _rows[Capacity - 1].Units)
            return;

        int index = Math.Min(Count, Capacity - 1);

        while (index > 0 && _rows[index - 1].Units < entry->Units)
        {
            _rows[index] = _rows[index - 1];
            index--;
        }

        _rows[index] = new SlowCall { Frame = entry->Frame, Id = entry->Id, Units = entry->Units, Site = entry->Site, Main = main };

        if (Count < Capacity)
            Count++;
    }
}
