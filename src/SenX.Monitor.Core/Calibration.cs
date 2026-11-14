namespace SenX.Monitor.Core;

/// <summary>
/// Measures what timing costs on this machine, so no figure is assumed. Clock cost differs a lot
/// across hardware, operating systems and virtual machines.
/// </summary>
public static class Calibration
{
    private const int Rounds = 4096;

    // A timed call costs two clock reads plus the record. The record is about a third of the
    // clock pair in Phase 0's benchmarks, and is left as a ratio so it scales with the machine.
    private const double RecordFactor = 1.35;

    /// <summary>Above this, a clock pair is slow enough that the tree should shed to coarse scopes.</summary>
    public const double SlowClockPairNanos = 200d;

    /// <summary>Two clock reads, in nanoseconds. Starts at a safe overestimate until measured.</summary>
    public static double ClockPairNanos { get; private set; } = 60d;

    /// <summary>One fully timed call, in nanoseconds. Starts at a safe overestimate until measured.</summary>
    public static double TimedCallNanos { get; private set; } = 80d;

    /// <summary>
    /// What the clock adds to every measurement: the time between two reads with nothing between
    /// them. It would otherwise count as game time, so it is taken back out of reported figures.
    /// </summary>
    public static double ReadBiasNanos { get; private set; }

    /// <summary><see cref="ReadBiasNanos"/> in stored 100 ns units.</summary>
    public static double ReadBiasUnits => ReadBiasNanos / 100d;

    /// <summary>
    /// Takes the clock's own overhead back out of measured time, so Monitor never reports its own
    /// cost as game time. Never goes below zero.
    /// </summary>
    public static double WithoutOverhead(long units, long calls)
    {
        double corrected = units - (calls * ReadBiasUnits);
        return corrected > 0d ? corrected : 0d;
    }

    /// <summary>True when this machine's clock is too slow for the full tree.</summary>
    public static bool SlowClock => ClockPairNanos > SlowClockPairNanos;

    /// <summary>
    /// Runs a short measurement, about 100 µs. On .NET 10 this must wait until after warm-up,
    /// because tiered compilation would otherwise measure unoptimized code.
    /// </summary>
    public static void Run()
    {
        long start = Clock.Now();
        long gaps = 0L;

        // One tick is far longer than a single gap on Windows, but the average over many is sound.
        for (int i = 0; i < Rounds; i++)
        {
            long first = Clock.Now();
            long second = Clock.Now();
            gaps += second - first;
        }

        long elapsed = Clock.Now() - start;

        ClockPairNanos = Clock.ToUnits(elapsed) * 100d / Rounds;
        ReadBiasNanos = Clock.ToUnits(gaps) * 100d / Rounds;
        TimedCallNanos = ClockPairNanos * RecordFactor;
    }

    // Lets tests work from known figures rather than whatever this machine measures.
    internal static void Set(double readBiasNanos, double timedCallNanos)
    {
        ReadBiasNanos = readBiasNanos;
        TimedCallNanos = timedCallNanos;
    }
}
