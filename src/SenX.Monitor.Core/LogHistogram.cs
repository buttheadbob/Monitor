using System;

namespace SenX.Monitor.Core;

/// <summary>
/// Fixed log-bucket histogram in native memory. Eight sub-buckets per power of two gives about
/// 6% resolution across nanoseconds to days. Percentiles need no sorting and no allocation.
/// One writer. Readers see counts that may be mid-update, which is harmless for statistics.
/// </summary>
public sealed unsafe class LogHistogram
{
    private const int SubBits = 3;
    private const int SubBuckets = 1 << SubBits;
    private const int MaxExponent = 47;

    /// <summary>Total buckets, enough for 2^47 units.</summary>
    public const int BucketCount = (MaxExponent - SubBits + 2) * SubBuckets;

    private readonly uint* _buckets = Native.Allocate<uint>(BucketCount);
    private long _count;

    public long Count => _count;

    public static int BucketFor(long value)
    {
        if (value < SubBuckets)
            return value < 0 ? 0 : (int)value;

        int exponent = Bits.Log2((ulong)value);
        if (exponent > MaxExponent)
            return BucketCount - 1;

        int sub = (int)(value >> (exponent - SubBits)) & (SubBuckets - 1);
        return (exponent - SubBits + 1) * SubBuckets + sub;
    }

    /// <summary>Smallest value that lands in a bucket.</summary>
    public static long LowerBound(int bucket)
    {
        if (bucket < SubBuckets)
            return bucket;

        int exponent = bucket / SubBuckets - 1 + SubBits;
        long sub = bucket & (SubBuckets - 1);
        return (SubBuckets + sub) << (exponent - SubBits);
    }

    public void Record(long value)
    {
        _buckets[BucketFor(value)]++;
        _count++;
    }

    /// <summary>The value at quantile <paramref name="q"/> (0 to 1), as the midpoint of its bucket.</summary>
    public long Percentile(double q)
    {
        long total = 0L;
        for (int i = 0; i < BucketCount; i++)
            total += _buckets[i];

        if (total == 0L)
            return 0L;

        long target = (long)Math.Ceiling(q * total);
        if (target < 1L)
            target = 1L;

        long seen = 0L;
        for (int i = 0; i < BucketCount; i++)
        {
            seen += _buckets[i];
            if (seen >= target)
            {
                long low = LowerBound(i);
                long high = i + 1 < BucketCount ? LowerBound(i + 1) : low;
                return low + (high - low) / 2L;
            }
        }

        return LowerBound(BucketCount - 1);
    }

    public void Clear()
    {
        Native.Clear(_buckets, BucketCount * sizeof(uint));
        _count = 0L;
    }
}
