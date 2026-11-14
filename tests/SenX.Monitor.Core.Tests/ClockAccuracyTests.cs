using System;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

/// <summary>
/// Windows hides unit conversion, because its clock already ticks in 100 ns. These run the conversion
/// as other clocks would, so a mistake that only hurts Linux can't pass on a Windows machine.
/// </summary>
public unsafe class ClockAccuracyTests : IDisposable
{
    private const long Linux = 1_000_000_000L;
    private const long OldQpc = 3_579_545L;
    private const int Site = 21;

    public ClockAccuracyTests() => TestSetup.Reset();

    public void Dispose() => Clock.Initialize();

    [Fact]
    public void Linux_ticks_convert_exactly()
    {
        Clock.UseFrequency(Linux);

        Assert.Equal(100L, Clock.ToUnits(10_000L));
        Assert.Equal(1L, Clock.ToUnits(100L));
    }

    [Theory]
    [InlineData(149L, 1L)]
    [InlineData(150L, 2L)]
    [InlineData(151L, 2L)]
    public void Conversion_rounds_to_the_nearest_unit(long ticks, long units)
    {
        Clock.UseFrequency(Linux);

        Assert.Equal(units, Clock.ToUnits(ticks));
    }

    [Fact]
    public void A_server_up_for_days_does_not_overflow()
    {
        Clock.UseFrequency(Linux);
        long thirtyDays = 30L * 86_400L * Linux;

        Assert.Equal(30L * 86_400L * Clock.UnitsPerSecond, Clock.ToUnits(thirtyDays));
    }

    [Fact]
    public void A_clock_slower_than_units_converts_too()
    {
        Clock.UseFrequency(OldQpc);

        Assert.InRange(Clock.ToUnits(OldQpc), Clock.UnitsPerSecond - 1L, Clock.UnitsPerSecond + 1L);
    }

    [Fact]
    public void Short_calls_are_not_rounded_away_one_at_a_time()
    {
        // 1000 calls of 350 ns is 350 µs. Converting each call first would report 300 µs.
        Clock.UseFrequency(Linux);

        for (int i = 0; i < 1000; i++)
            Recorder.AddKey(Site, 7L, 350L, 0);

        Assert.Equal(350_000L, ThreadBlocks.Current()->SiteTicks[Site]);
        Assert.Equal(3500L, Clock.ToUnits(ThreadBlocks.Current()->SiteTicks[Site]));
    }

    [Fact]
    public void Calls_shorter_than_a_unit_still_add_up()
    {
        Clock.UseFrequency(Linux);

        for (int i = 0; i < 1000; i++)
            Recorder.AddEntity(Site, Phase.Before, 7L, 30L, 0);

        Assert.Equal(300L, Clock.ToUnits(ThreadBlocks.Current()->SiteTicks[Site]));
    }

    [Fact]
    public void A_negative_duration_adds_nothing()
    {
        Recorder.AddKey(Site, 7L, -500L, 0);

        Assert.Equal(0L, ThreadBlocks.Current()->SiteTicks[Site]);
    }
}
