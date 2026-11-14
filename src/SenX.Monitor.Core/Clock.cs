using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SenX.Monitor.Core;

/// <summary>
/// Monitor's only time source. Raw values come from <see cref="Stopwatch"/>, the best
/// portable high resolution clock. Reported durations are normalized to 100 ns units, because
/// Stopwatch runs at 10 MHz on Windows and 1 GHz on Linux. Running sums stay in raw ticks and are
/// converted when read: converting each call first would drop part of a unit every time.
/// </summary>
public static class Clock
{
    /// <summary>Stored time units per second (100 ns units).</summary>
    public const long UnitsPerSecond = 10_000_000;

    // Units per raw tick. Zero means raw ticks are already units.
    private static double s_unitsPerTick;

    /// <summary>Raw clock ticks per second.</summary>
    public static long Frequency => Stopwatch.Frequency;

    internal static void Initialize() => UseFrequency(Stopwatch.Frequency);

    // Separate from Initialize so tests can check a Linux clock's behaviour on Windows, and the reverse.
    internal static void UseFrequency(long frequency) =>
        s_unitsPerTick = frequency == UnitsPerSecond ? 0d : (double)UnitsPerSecond / frequency;

    /// <summary>Reads the clock. Roughly 20 ns, so call it only for work that is really timed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>
    /// Converts a raw tick duration to 100 ns units, rounding to the nearest. Negative durations
    /// become zero. A double holds any duration here exactly enough, and can't overflow.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToUnits(long rawTicks)
    {
        if (rawTicks <= 0)
            return 0;

        double scale = s_unitsPerTick;
        return scale == 0d ? rawTicks : (long)((rawTicks * scale) + 0.5d);
    }
}
