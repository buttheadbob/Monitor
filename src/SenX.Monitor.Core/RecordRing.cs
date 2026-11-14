using System;
using System.Threading;

namespace SenX.Monitor.Core;

/// <summary>
/// Fixed ring of fixed-size records, with one writer and any number of readers. Backs the flight
/// recorder and the per-second rollups. Sequence numbers let a reader catch up after a gap, and a
/// reader that falls too far behind gets a clean refusal rather than a torn record.
/// </summary>
public sealed unsafe class RecordRing
{
    // Records this close to being overwritten are refused, so a copy can't race the writer.
    private const long SafetyMargin = 2;

    private readonly byte* _data;
    private readonly int _recordBytes;
    private readonly long _capacity;
    private readonly long _mask;
    private long _head;

    public RecordRing(int recordBytes, int capacity)
    {
        if (!Bits.IsPowerOfTwo(capacity))
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be a power of two.");
        if (recordBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(recordBytes));

        _recordBytes = recordBytes;
        _capacity = capacity;
        _mask = capacity - 1L;
        _data = Native.AllocateZeroed(recordBytes * (long)capacity);
    }

    public int RecordBytes => _recordBytes;

    public long Capacity => _capacity;

    /// <summary>Sequence number the next record will get.</summary>
    public long Head => Volatile.Read(ref _head);

    /// <summary>Oldest sequence number still safe to read.</summary>
    public long Oldest
    {
        get
        {
            long oldest = Head - _capacity + SafetyMargin;
            return oldest < 0L ? 0L : oldest;
        }
    }

    /// <summary>The record to fill next. Writer only.</summary>
    public byte* Reserve() => _data + (_head & _mask) * _recordBytes;

    /// <summary>Publishes the reserved record. Writer only.</summary>
    public void Commit() => Volatile.Write(ref _head, _head + 1L);

    /// <summary>Copies one record. Returns false if it isn't written yet or has been overwritten.</summary>
    public bool TryCopy(long sequence, byte* destination)
    {
        if (sequence < 0L || sequence >= Head || sequence < Oldest)
            return false;

        byte* source = _data + (sequence & _mask) * _recordBytes;
        Buffer.MemoryCopy(source, destination, _recordBytes, _recordBytes);
        Thread.MemoryBarrier();

        // The writer may have lapped us during the copy.
        return sequence >= Oldest;
    }
}
