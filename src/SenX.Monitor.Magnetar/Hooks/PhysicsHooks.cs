using System.Runtime.CompilerServices;
using Havok;
using Sandbox.Engine.Physics;
using SenX.Monitor.Core;
using VRage.Collections;
using VRageMath.Spatial;

namespace SenX.Monitor.Magnetar.Hooks;

/// <summary>
/// Lets some physics clusters step alone first, so they can be timed. The stepping itself is the
/// game's own code, and the parallel path skips those clusters through the same activity check it
/// already uses. Nothing runs here unless a measuring run asked for it: otherwise each hook is one
/// byte read and the game's original call.
/// </summary>
public static class PhysicsHooks
{
    private static MyPhysics? s_physics;
    private static MyPhysics? s_saved;
    private static float s_averageFrameTime;
    private static int s_slowFrames;
    private static int s_fastFrames;
    private static bool s_optimizeNextFrame;

    public static void StepParallel(MyPhysics physics)
    {
        if (Gates.Read(Site.PhysicsCluster) == Gates.Closed)
        {
            physics.StepWorldsParallel();
            return;
        }

        StepWithTurns(physics);
    }

    public static bool ClusterActive(MyPhysics physics, int clusterId, int characterBodies)
    {
        if (Gates.Read(Site.PhysicsCluster) != Gates.Closed && ClusterTurns.IsAlone(clusterId))
            return false;

        return physics.IsClusterActive(clusterId, characterBodies);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StepWithTurns(MyPhysics physics)
    {
        s_physics = physics;
        ClusterTurns.BeginStep();

        if (ClusterTurns.HasCredit)
        {
            SaveOptimizer(physics);
            var clusters = new ClusterList(physics);
            ClusterTurns.StepSomeAlone(ref clusters);
        }

        physics.StepWorldsParallel();
    }

    /// <summary>How many clusters the game is stepping right now. For reports, on the game thread.</summary>
    public static int CountActive()
    {
        MyPhysics? physics = s_physics;
        if (physics == null || MyPhysics.Clusters == null)
            return 0;

        var clusters = new ClusterList(physics);
        int active = 0;

        for (int i = 0; i < clusters.Count; i++)
        {
            if (clusters.IsActive(i))
                active++;
        }

        return active;
    }

    // The game times the whole step, and slow steps can make it turn off collision checks on grids.
    // A step that ran slower only because Monitor was timing it mustn't count towards that.
    private static void SaveOptimizer(MyPhysics physics)
    {
        s_averageFrameTime = physics.m_averageFrameTime;
        s_slowFrames = physics.m_slowFrames;
        s_fastFrames = physics.m_fastFrames;
        s_optimizeNextFrame = physics.m_optimizeNextFrame;
        s_saved = physics;
    }

    /// <summary>Undoes what a timed step did to the game's optimizer. Call once per frame, between physics steps.</summary>
    public static void RestoreOptimizer()
    {
        MyPhysics? physics = s_saved;
        if (physics == null)
            return;

        physics.m_averageFrameTime = s_averageFrameTime;
        physics.m_slowFrames = s_slowFrames;
        physics.m_fastFrames = s_fastFrames;
        physics.m_optimizeNextFrame = s_optimizeNextFrame;
        s_saved = null;
    }

    private readonly struct ClusterList : IClusterList
    {
        private readonly MyPhysics _physics;
        private readonly ListReader<MyClusterTree.MyCluster> _clusters;

        public ClusterList(MyPhysics physics)
        {
            _physics = physics;
            _clusters = MyPhysics.Clusters.GetClusters();
        }

        public int Count => _clusters.Count;

        public int Id(int index) => _clusters[index].ClusterId;

        // The same test the game makes in its parallel path, so sleeping clusters stay asleep.
        public bool IsActive(int index)
        {
            MyClusterTree.MyCluster cluster = _clusters[index];
            return cluster.UserData is HkWorld world
                   && _physics.IsClusterActive(cluster.ClusterId, world.CharacterRigidBodies.Count);
        }

        public void StepAlone(int index) => _physics.StepSingleWorld((HkWorld)_clusters[index].UserData);
    }
}
