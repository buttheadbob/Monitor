using System.Runtime.CompilerServices;

namespace SenX.Monitor.Core;

/// <summary>
/// Writes a timed call into the calling thread's block. Runs only on the timed path, never for
/// closed gates or unsampled calls. No locks, no allocation, no managed references.
/// </summary>
public static unsafe class Recorder
{
    /// <summary>A single call at or above this goes to the slow-call ring. Default 1 ms.</summary>
    public static long SlowThresholdUnits;

    /// <summary>The current frame number, stamped on slow calls. Set once per frame.</summary>
    public static ulong Frame;

    // Set here rather than with an initializer, so hot code carries no class-init check.
    internal static void Initialize() => SlowThresholdUnits = Clock.UnitsPerSecond / 1000;

    /// <summary>
    /// Records a call against an entity. Returns a slot only when it was just created, so the
    /// caller can fill in grid, owner and definition. That read happens once per entity, not per call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntitySlot* AddEntity(int site, int phase, long id, long rawTicks, int shift)
    {
        ThreadBlock* block = ThreadBlocks.Current();
        if (block == null)
        {
            ThreadBlocks.CountUnattached();
            return null;
        }

        // Sums keep raw ticks, so no part of a unit is lost on any call. Units are for this call's own peak.
        long units = Clock.ToUnits(rawTicks);
        long scaled = (rawTicks > 0L ? rawTicks : 0L) << shift;

        block->SiteTicks[site] += scaled;
        block->SiteSamples[site]++;

        if (units >= SlowThresholdUnits)
            PushSlow(block, site, id, units);

        EntitySlot* slot = block->Entities.Claim(id, out bool created);
        if (slot == null)
        {
            block->OverflowTicks[site] += scaled;
            return null;
        }

        slot->PhaseTicks[phase] += scaled;
        slot->Samples++;

        uint narrow = units > uint.MaxValue ? uint.MaxValue : (uint)units;
        if (narrow > slot->MaxUnits)
            slot->MaxUnits = narrow;

        return created ? slot : null;
    }

    /// <summary>
    /// Records a call against a non-entity key: a delegate code pointer or a type id.
    /// Returns true when the key was just created, so the caller can note what it means.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AddKey(int site, long key, long rawTicks, int shift)
    {
        ThreadBlock* block = ThreadBlocks.Current();
        if (block == null)
        {
            ThreadBlocks.CountUnattached();
            return false;
        }

        long units = Clock.ToUnits(rawTicks);
        long scaled = (rawTicks > 0L ? rawTicks : 0L) << shift;

        block->SiteTicks[site] += scaled;
        block->SiteSamples[site]++;

        if (units >= SlowThresholdUnits)
            PushSlow(block, site, key, units);

        KeySlot* slot = block->Keys.Claim(key, out bool created);
        if (slot == null)
        {
            block->OverflowTicks[site] += scaled;
            return false;
        }

        slot->ScaledTicks += scaled;
        slot->Scope = site;
        slot->Samples++;

        uint narrow = units > uint.MaxValue ? uint.MaxValue : (uint)units;
        if (narrow > slot->MaxUnits)
            slot->MaxUnits = narrow;

        return created;
    }

    private static void PushSlow(ThreadBlock* block, int site, long id, long units)
    {
        SlowEntry* entry = block->Slow + (block->SlowHead++ & block->SlowMask);
        entry->Frame = Frame;
        entry->Id = id;
        entry->Units = units;
        entry->Site = site;
    }
}
