using System;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class StorageTests
{
    public StorageTests() => TestSetup.Reset();

    private static void Write(RecordRing ring, long value)
    {
        *(long*)ring.Reserve() = value;
        ring.Commit();
    }

    [Fact]
    public void Ring_rejects_a_capacity_that_is_not_a_power_of_two() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordRing(8, 100));

    [Fact]
    public void Ring_returns_what_was_written_in_order()
    {
        var ring = new RecordRing(8, 16);
        for (long i = 0; i < 10; i++)
            Write(ring, i * 3);

        long value;
        for (long i = 0; i < 10; i++)
        {
            Assert.True(ring.TryCopy(i, (byte*)&value));
            Assert.Equal(i * 3, value);
        }
    }

    [Fact]
    public void Ring_refuses_records_not_written_yet()
    {
        var ring = new RecordRing(8, 16);
        Write(ring, 1L);

        long value;
        Assert.False(ring.TryCopy(1L, (byte*)&value));
        Assert.False(ring.TryCopy(-1L, (byte*)&value));
    }

    [Fact]
    public void Ring_refuses_overwritten_records_rather_than_returning_wrong_ones()
    {
        var ring = new RecordRing(8, 16);
        for (long i = 0; i < 100; i++)
            Write(ring, i);

        long value;
        Assert.False(ring.TryCopy(10L, (byte*)&value));
        Assert.Equal(100L - 16L + 2L, ring.Oldest);
        Assert.True(ring.TryCopy(ring.Oldest, (byte*)&value));
        Assert.Equal(ring.Oldest, value);
    }

    [Fact]
    public void Histogram_buckets_rise_with_value_and_contain_it()
    {
        int previous = -1;
        for (long value = 0; value < 2_000_000; value = value * 11 / 10 + 1)
        {
            int bucket = LogHistogram.BucketFor(value);
            Assert.True(bucket >= previous);
            Assert.True(LogHistogram.LowerBound(bucket) <= value);
            Assert.True(bucket + 1 == LogHistogram.BucketCount || LogHistogram.LowerBound(bucket + 1) > value);
            previous = bucket;
        }
    }

    [Fact]
    public void Histogram_handles_extreme_values()
    {
        Assert.Equal(0, LogHistogram.BucketFor(-5L));
        Assert.Equal(LogHistogram.BucketCount - 1, LogHistogram.BucketFor(long.MaxValue));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.95)]
    [InlineData(0.99)]
    public void Histogram_percentiles_are_within_resolution(double quantile)
    {
        var histogram = new LogHistogram();
        for (long value = 1; value <= 100_000; value++)
            histogram.Record(value);

        double expected = quantile * 100_000;
        double actual = histogram.Percentile(quantile);
        Assert.InRange(actual, expected * 0.93, expected * 1.07);
    }

    [Fact]
    public void Histogram_is_empty_after_clear()
    {
        var histogram = new LogHistogram();
        histogram.Record(100L);
        histogram.Clear();

        Assert.Equal(0L, histogram.Count);
        Assert.Equal(0L, histogram.Percentile(0.5));
    }

    private static uint[] Series(int rows, int columns, int seed)
    {
        var random = new Random(seed);
        var data = new uint[rows * columns];
        var level = new double[columns];

        for (int c = 0; c < columns; c++)
            level[c] = random.Next(5, 4000);

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
                data[r * columns + c] = (uint)(level[c] * (0.9 + random.NextDouble() * 0.2));
        }

        return data;
    }

    [Fact]
    public void Codec_round_trips_a_time_series()
    {
        const int rows = 30;
        const int columns = 40;
        uint[] source = Series(rows, columns, 8);
        var decoded = new uint[source.Length];
        var encoded = new byte[source.Length * 5];
        var writePrevious = new uint[columns];
        var readPrevious = new uint[columns];

        fixed (uint* input = source, output = decoded, wp = writePrevious, rp = readPrevious)
        fixed (byte* bytes = encoded)
        {
            int length = DeltaCodec.Encode(input, rows, columns, wp, bytes, encoded.Length);
            Assert.True(length > 0);
            Assert.True(DeltaCodec.Decode(bytes, length, rows, columns, rp, output));
        }

        Assert.Equal(source, decoded);
    }

    [Fact]
    public void Codec_survives_the_largest_possible_swings()
    {
        uint[] source = { 0u, uint.MaxValue, 0u, uint.MaxValue, 1u, uint.MaxValue - 1 };
        var decoded = new uint[source.Length];
        var encoded = new byte[64];
        uint writePrevious = 0;
        uint readPrevious = 0;

        fixed (uint* input = source, output = decoded)
        fixed (byte* bytes = encoded)
        {
            int length = DeltaCodec.Encode(input, source.Length, 1, &writePrevious, bytes, encoded.Length);
            Assert.True(length > 0);
            Assert.True(DeltaCodec.Decode(bytes, length, source.Length, 1, &readPrevious, output));
        }

        Assert.Equal(source, decoded);
    }

    [Fact]
    public void Codec_batches_continue_from_the_previous_row()
    {
        const int columns = 8;
        uint[] source = Series(10, columns, 9);
        var decoded = new uint[source.Length];
        var encoded = new byte[source.Length * 5];
        var writePrevious = new uint[columns];
        var readPrevious = new uint[columns];

        fixed (uint* input = source, output = decoded, wp = writePrevious, rp = readPrevious)
        fixed (byte* bytes = encoded)
        {
            int first = DeltaCodec.Encode(input, 6, columns, wp, bytes, encoded.Length);
            int second = DeltaCodec.Encode(input + 6 * columns, 4, columns, wp, bytes + first, encoded.Length - first);

            Assert.True(DeltaCodec.Decode(bytes, first, 6, columns, rp, output));
            Assert.True(DeltaCodec.Decode(bytes + first, second, 4, columns, rp, output + 6 * columns));
        }

        Assert.Equal(source, decoded);
    }

    [Fact]
    public void Codec_reports_a_full_buffer_instead_of_overrunning()
    {
        uint[] source = Series(10, 8, 10);
        var encoded = new byte[8];
        var previous = new uint[8];

        fixed (uint* input = source, wp = previous)
        fixed (byte* bytes = encoded)
        {
            Assert.Equal(-1, DeltaCodec.Encode(input, 10, 8, wp, bytes, encoded.Length));
        }
    }

    [Fact]
    public void Codec_rejects_truncated_input()
    {
        uint[] source = Series(4, 8, 11);
        var encoded = new byte[256];
        var writePrevious = new uint[8];
        var readPrevious = new uint[8];
        var decoded = new uint[source.Length];

        fixed (uint* input = source, output = decoded, wp = writePrevious, rp = readPrevious)
        fixed (byte* bytes = encoded)
        {
            int length = DeltaCodec.Encode(input, 4, 8, wp, bytes, encoded.Length);
            Assert.False(DeltaCodec.Decode(bytes, length / 2, 4, 8, rp, output));
        }
    }

    [Fact]
    public void Codec_output_is_much_smaller_than_json()
    {
        const int rows = 30;
        const int columns = 40;
        uint[] source = Series(rows, columns, 12);
        var encoded = new byte[source.Length * 5];
        var previous = new uint[columns];
        int length;

        fixed (uint* input = source, wp = previous)
        fixed (byte* bytes = encoded)
        {
            length = DeltaCodec.Encode(input, rows, columns, wp, bytes, encoded.Length);
        }

        int json = string.Join(",", source).Length;
        Assert.True(length < json / 2, $"Encoded {length} bytes against {json} of JSON.");
    }
}
