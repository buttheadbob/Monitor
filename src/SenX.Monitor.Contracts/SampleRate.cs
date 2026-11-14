using System;
using System.Collections.Generic;
using System.Linq;

namespace SenX.Monitor.Contracts;

/// <summary>
/// Which background totals are kept as samples: one every 2 minutes, and one a minute from when sim speed
/// drops below 0.7 until it's been back above that for 5 minutes. Lag is when the detail matters. A host
/// catching up on a backlog can keep a few extra, because it hears the seconds before the totals. It isn't
/// safe across threads, so each host keeps it behind its own lock or on one thread.
/// </summary>
public sealed class SampleRate
{
    public static readonly TimeSpan Normal = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan InLag = TimeSpan.FromMinutes(1);

    private const double LagSimSpeed = 0.7d;

    private static readonly TimeSpan s_recovery = TimeSpan.FromMinutes(5);

    // Totals are 30 seconds apart and never exactly, so a step ends a little early rather than slipping a whole total.
    private static readonly TimeSpan s_slack = TimeSpan.FromSeconds(10);

    private DateTime _skipped;

    /// <summary>The latest total kept.</summary>
    public DateTime Last { get; private set; }

    /// <summary>The one kept before that.</summary>
    public DateTime Before { get; private set; }

    /// <summary>When the lag rate ends, if nothing else goes wrong.</summary>
    public DateTime LagUntil { get; private set; }

    /// <summary>Picks up where it left off, so a restart of Quasar doesn't change what's kept.</summary>
    public void Restore(DateTime last, DateTime before, DateTime lagUntil)
    {
        Last = last;
        Before = before;
        LagUntil = lagUntil;
    }

    public void NoteSimSpeed(DateTime time, double simSpeed)
    {
        if (simSpeed < LagSimSpeed)
            LagUntil = time + s_recovery;
    }

    /// <summary>Whether a total from this time should be kept. Every row of one total shares its time, and the answer stays the same for all of them.</summary>
    public bool Keep(DateTime time)
    {
        if (time == Last)
            return true;

        if (time == _skipped)
            return false;

        if (time - Last < (time < LagUntil ? InLag : Normal) - s_slack)
        {
            _skipped = time;
            return false;
        }

        Before = Last;
        Last = time;
        return true;
    }

    /// <summary>
    /// How much time each total stands for, in seconds, for anything that adds totals up. Lag keeps them twice as
    /// often, and a short range can mix both rates, so counting each one the same would make those stretches
    /// count for more. A total stands for the gap to the next one, and a gap past the normal step is the server
    /// being away, which it doesn't stand for.
    /// </summary>
    public static Dictionary<DateTime, double> Weights(IEnumerable<DateTime> times)
    {
        List<DateTime> ordered = times.Distinct().OrderBy(time => time).ToList();
        var weights = new Dictionary<DateTime, double>(ordered.Count);

        for (int index = 0; index < ordered.Count; index++)
        {
            TimeSpan gap = index + 1 < ordered.Count
                ? ordered[index + 1] - ordered[index]
                : index > 0 ? ordered[index] - ordered[index - 1] : Normal;

            weights[ordered[index]] = Math.Min(gap.TotalSeconds, Normal.TotalSeconds);
        }

        return weights;
    }
}
