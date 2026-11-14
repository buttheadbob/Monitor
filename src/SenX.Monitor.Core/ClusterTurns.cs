using System.Collections.Generic;

namespace SenX.Monitor.Core;

/// <summary>The physics clusters in a step. A struct, so the turn loop allocates nothing.</summary>
public interface IClusterList
{
    int Count { get; }

    /// <summary>True when the game would step this cluster this frame.</summary>
    bool IsActive(int index);

    int Id(int index);

    /// <summary>Steps this cluster now, with the game's own single-world step.</summary>
    void StepAlone(int index);
}

/// <summary>
/// Times physics clusters without disturbing them. All clusters normally step together on one job
/// queue, so nothing can be attributed to any one of them. Here, a few step alone first and the rest
/// step in parallel as usual, skipping those, so every cluster still steps exactly once per frame.
/// Turns rotate through the clusters and spend a small time budget, which spreads the cost across
/// the run instead of stepping everything one after another.
///
/// A cluster's time is what it takes with every physics thread to itself. That ranks clusters
/// fairly, but times add up to more than normal physics, where clusters overlap.
///
/// Game thread only, and only during a measuring run that asked for it.
/// </summary>
public static class ClusterTurns
{
    /// <summary>The most stepping alone may take on average, as a share of a 60 Hz frame.</summary>
    public const double FrameShare = 0.05;

    private const long RefillUnits = (long)(FrameShare * Clock.UnitsPerSecond / 60d);

    private static readonly HashSet<int> s_alone = new();
    private static long s_credit;
    private static int s_cursor;

    /// <summary>True while a measuring run is timing clusters.</summary>
    public static bool Open => Gates.Read(Site.PhysicsCluster) != Gates.Closed;

    internal static void Reset()
    {
        s_alone.Clear();
        s_credit = RefillUnits;
        s_cursor = 0;
    }

    /// <summary>
    /// Starts a physics step, adding this frame's budget. Debt from a heavy cluster has to be paid off
    /// first. Marks from the last step clear here, so one can't be left behind if a step ever throws.
    /// </summary>
    public static void BeginStep()
    {
        if (s_alone.Count != 0)
            s_alone.Clear();

        s_credit += RefillUnits;
        if (s_credit > RefillUnits)
            s_credit = RefillUnits;
    }

    /// <summary>True when the budget allows a cluster to step alone this frame.</summary>
    public static bool HasCredit => s_credit > 0L;

    /// <summary>Steps clusters alone until the budget runs out. No cluster is visited twice in a step.</summary>
    public static void StepSomeAlone<T>(ref T clusters) where T : struct, IClusterList
    {
        int count = clusters.Count;

        for (int tried = 0; tried < count && s_credit > 0L; tried++)
        {
            if (s_cursor >= count)
                s_cursor = 0;

            int index = s_cursor++;
            if (!clusters.IsActive(index))
                continue;

            int id = clusters.Id(index);
            long started = Clock.Now();
            clusters.StepAlone(index);
            long elapsed = Clock.Now() - started;

            s_alone.Add(id);
            s_credit -= Clock.ToUnits(elapsed);
            Recorder.AddKey(Site.PhysicsCluster, SiteKeys.Pack(id + 1L, Site.PhysicsCluster), elapsed, 0);
        }
    }

    /// <summary>True when this cluster already stepped this frame, so the parallel path must skip it.</summary>
    public static bool IsAlone(int clusterId) => s_alone.Count != 0 && s_alone.Contains(clusterId);

    /// <summary>The cluster id a row in <see cref="CaptureResult.Clusters"/> belongs to.</summary>
    public static int ClusterIdOf(in RollupRow row) => (int)(row.Key - 1L);
}
