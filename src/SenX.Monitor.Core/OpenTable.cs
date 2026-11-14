using System.Runtime.CompilerServices;
using System.Threading;

namespace SenX.Monitor.Core;

/// <summary>
/// Fixed-size open-addressed table in native memory, written by a single thread, unless a caller uses
/// <see cref="ClaimShared"/>. It never grows and never blocks: when probing is exhausted the caller gets null and
/// puts the time in an overflow bucket, so totals stay right.
/// </summary>
/// <typeparam name="TSlot">Slot type. Its first field must be a <see cref="long"/> key, where 0 means empty.</typeparam>
public unsafe struct OpenTable<TSlot> where TSlot : unmanaged
{
    private const int MaxProbes = 16;
    private const ulong HashMultiplier = 0x9E3779B97F4A7C15UL;

    private TSlot* _slots;
    private uint _mask;
    private int _shift;

    public static OpenTable<TSlot> Allocate(int capacity)
    {
        if (!Bits.IsPowerOfTwo(capacity))
            throw new System.ArgumentOutOfRangeException(nameof(capacity), "Capacity must be a power of two.");

        return new OpenTable<TSlot>
        {
            _slots = Native.Allocate<TSlot>(capacity),
            _mask = (uint)capacity - 1,
            _shift = 64 - Bits.Log2((ulong)capacity),
        };
    }

    public readonly int Capacity => (int)_mask + 1;

    public readonly long Bytes => (long)Capacity * sizeof(TSlot);

    /// <summary>
    /// Finds or creates the slot for <paramref name="key"/>. Returns null when probing is exhausted.
    /// A zero key is not expected; it lands on an empty slot and is simply never marked as created.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly TSlot* Claim(long key, out bool created)
    {
        uint index = (uint)(((ulong)key * HashMultiplier) >> _shift);

        for (uint probe = 0; probe < MaxProbes; probe++)
        {
            TSlot* slot = _slots + ((index + probe) & _mask);
            long existing = *(long*)slot;

            if (existing == key)
            {
                created = false;
                return slot;
            }

            if (existing == 0L)
            {
                *(long*)slot = key;
                created = true;
                return slot;
            }
        }

        created = false;
        return null;
    }

    /// <summary>
    /// <see cref="Claim"/> for a table that many threads write. A slot's key is only ever set once, so a
    /// slot that's been claimed stays the same, and what's in it is up to the caller to add up safely.
    /// </summary>
    public readonly TSlot* ClaimShared(long key, out bool created)
    {
        uint index = (uint)(((ulong)key * HashMultiplier) >> _shift);

        for (uint probe = 0; probe < MaxProbes; probe++)
        {
            TSlot* slot = _slots + ((index + probe) & _mask);
            long existing = Volatile.Read(ref *(long*)slot);

            if (existing == 0L)
            {
                existing = Interlocked.CompareExchange(ref *(long*)slot, key, 0L);

                if (existing == 0L)
                {
                    created = true;
                    return slot;
                }
            }

            if (existing == key)
            {
                created = false;
                return slot;
            }
        }

        created = false;
        return null;
    }

    /// <summary>Finds a key without creating it. Returns null when it isn't there.</summary>
    public readonly TSlot* Find(long key)
    {
        uint index = (uint)(((ulong)key * HashMultiplier) >> _shift);

        for (uint probe = 0; probe < MaxProbes; probe++)
        {
            TSlot* slot = _slots + ((index + probe) & _mask);
            long existing = *(long*)slot;

            if (existing == key)
                return slot;

            if (existing == 0L)
                return null;
        }

        return null;
    }

    public readonly TSlot* At(int index) => _slots + index;

    public readonly void Clear() => Native.Clear(_slots, Bytes);

    /// <summary>Clears part of the table, for one too big to clear in a frame.</summary>
    public readonly void ClearSlots(int first, int count) => Native.Clear(_slots + first, (long)count * sizeof(TSlot));
}
