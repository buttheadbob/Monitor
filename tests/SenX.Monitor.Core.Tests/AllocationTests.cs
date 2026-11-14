using System;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

/// <summary>
/// The Phase 0 gate in test form: hot paths allocate nothing, on either runtime.
/// Each test does enough work that a per-call allocation would be impossible to miss.
/// </summary>
public unsafe class AllocationTests
{
    private const int Loops = 100_000;
    private const int Site = 21;

    public AllocationTests() => TestSetup.Reset();

    [Fact]
    public void The_allocation_probe_can_see_allocation()
    {
        // Guards against the rest of this file passing because the probe reads nothing.
        object? keep = null;
        long bytes = Allocation.Measure(() => keep = new byte[4096]);

        Assert.True(bytes >= 4096, $"Measured {bytes} bytes.");
        GC.KeepAlive(keep);
    }

    [Fact]
    public void Gate_and_sampler_allocate_nothing()
    {
        long sink = 0L;

        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < Loops; i++)
            {
                if (Gates.Read(Site) == Gates.Closed && Sampler.Hit(i, Site, 3))
                    sink++;
            }
        });

        Assert.Equal(0L, bytes);
        GC.KeepAlive(sink);
    }

    [Fact]
    public void Clock_allocates_nothing()
    {
        long sink = 0L;

        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < Loops; i++)
                sink += Clock.ToUnits(Clock.Now());
        });

        Assert.Equal(0L, bytes);
        GC.KeepAlive(sink);
    }

    [Fact]
    public void Recording_existing_and_new_slots_allocates_nothing()
    {
        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < Loops; i++)
                Recorder.AddEntity(Site, Phase.Before, 1L + i % 8000, 100L, 2);
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Recording_keys_allocates_nothing()
    {
        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < Loops; i++)
                Recorder.AddKey(Site, 1L + i % 100, 100L, 1);
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Slow_ring_allocates_nothing()
    {
        long slow = Recorder.SlowThresholdUnits * 2;

        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < Loops; i++)
                Recorder.AddEntity(Site, Phase.After, 5L, slow, 0);
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Tree_counters_allocate_nothing()
    {
        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < Loops; i++)
                TreeCounters.Add(Site, 100L);
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Delegate_identity_allocates_nothing()
    {
        Action callback = () => { };
        long sink = 0L;

        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < Loops; i++)
                sink += DelegateIdentity.Key(callback);
        });

        Assert.Equal(0L, bytes);
        GC.KeepAlive(sink);
    }

    [Fact]
    public void Sampler_advance_allocates_nothing()
    {
        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < Loops; i++)
                Sampler.NextFrame();
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Rings_allocate_nothing()
    {
        var ring = new RecordRing(160, 1024);
        byte* copy = stackalloc byte[160];

        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < Loops; i++)
            {
                *(long*)ring.Reserve() = i;
                ring.Commit();
                ring.TryCopy(ring.Head - 1, copy);
            }
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Histogram_allocates_nothing()
    {
        var histogram = new LogHistogram();
        long sink = 0L;

        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < Loops; i++)
                histogram.Record(i);

            sink += histogram.Percentile(0.99);
        });

        Assert.Equal(0L, bytes);
        GC.KeepAlive(sink);
    }

    [Fact]
    public void Codec_allocates_nothing()
    {
        const int rows = 30;
        const int columns = 40;
        uint* source = stackalloc uint[rows * columns];
        uint* previous = stackalloc uint[columns];
        uint* result = stackalloc uint[rows * columns];
        byte* encoded = stackalloc byte[rows * columns * 5];

        for (int i = 0; i < rows * columns; i++)
            source[i] = (uint)(i * 37 % 4001);

        uint* src = source;
        uint* prev = previous;
        uint* dest = result;
        byte* enc = encoded;

        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < 2_000; i++)
            {
                Native.Clear(prev, columns * sizeof(uint));
                int length = DeltaCodec.Encode(src, rows, columns, prev, enc, rows * columns * 5);

                Native.Clear(prev, columns * sizeof(uint));
                DeltaCodec.Decode(enc, length, rows, columns, prev, dest);
            }
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Worker_claim_allocates_nothing()
    {
        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < 2_000; i++)
            {
                ThreadBlocks.BeginPeriod();
                Recorder.AddEntity(Site, Phase.Before, 1L, 10L, 0);
            }
        });

        Assert.Equal(0L, bytes);
    }
}
