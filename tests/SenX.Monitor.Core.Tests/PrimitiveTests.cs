using System;
using System.Diagnostics;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class PrimitiveTests
{
    public PrimitiveTests() => TestSetup.Reset();

    [Fact]
    public void Log2_is_exact_around_every_power_of_two()
    {
        for (int shift = 0; shift < 64; shift++)
        {
            ulong power = 1UL << shift;
            Assert.Equal(shift, Bits.Log2(power));

            if (shift > 0)
                Assert.Equal(shift - 1, Bits.Log2(power - 1));

            if (shift > 1)
                Assert.Equal(shift, Bits.Log2(power + 1));
        }
    }

    [Fact]
    public void Clock_treats_negative_durations_as_zero()
    {
        Assert.Equal(0L, Clock.ToUnits(-5L));
        Assert.Equal(0L, Clock.ToUnits(0L));
    }

    [Fact]
    public void Clock_converts_one_second_to_ten_million_units()
    {
        long units = Clock.ToUnits(Stopwatch.Frequency);
        Assert.InRange(units, Clock.UnitsPerSecond - 1, Clock.UnitsPerSecond + 1);
    }

    [Fact]
    public void Native_memory_is_aligned_and_zeroed()
    {
        byte* memory = Native.AllocateZeroed(1000);

        Assert.Equal(0L, (long)memory % Native.CacheLine);
        for (int i = 0; i < 1000; i++)
            Assert.Equal(0, memory[i]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(1023)]
    public void Native_clear_covers_exactly_the_requested_bytes(int bytes)
    {
        byte* memory = Native.AllocateZeroed(bytes + 16);
        for (int i = 0; i < bytes + 16; i++)
            memory[i] = 0xAB;

        Native.Clear(memory, bytes);

        for (int i = 0; i < bytes; i++)
            Assert.Equal(0, memory[i]);
        for (int i = bytes; i < bytes + 16; i++)
            Assert.Equal(0xAB, memory[i]);
    }

    [Fact]
    public void Gates_start_closed_and_clamp_their_shift()
    {
        Assert.Equal(Gates.Closed, Gates.Read(5));

        Gates.Open(5, 3);
        Assert.Equal(3, Gates.Read(5));

        Gates.Open(5, 99);
        Assert.Equal(Gates.MaxShift, Gates.Read(5));

        Gates.Open(5, -1);
        Assert.Equal(0, Gates.Read(5));

        Gates.Close(5);
        Assert.Equal(Gates.Closed, Gates.Read(5));
    }

    [Fact]
    public void Slot_layouts_match_the_design()
    {
        Assert.Equal(96, sizeof(EntitySlot));
        Assert.Equal(32, sizeof(KeySlot));
        Assert.Equal(32, sizeof(SlowEntry));

        // OpenTable reads the key at offset zero.
        EntitySlot entity = default;
        Assert.Equal(0L, (byte*)&entity.Id - (byte*)&entity);

        KeySlot key = default;
        Assert.Equal(0L, (byte*)&key.Key - (byte*)&key);
    }

    [Fact]
    public void Default_layout_fits_the_memory_budget()
    {
        // 32 workers is a 16 core server. Merge space still has to fit under the 96 MB budget.
        long bytes = DetailLayout.Default(32).TotalBytes;
        Assert.True(bytes < 96L * 1024 * 1024, $"Layout needs {bytes / (1024 * 1024)} MB.");
    }
}
