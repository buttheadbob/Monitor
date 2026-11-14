using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Hooks;
using SenX.Monitor.Magnetar.Patching;
using SenX.Monitor.Magnetar.Reporting;
using VRage.Library.Threading;
using VRageMath;
using VRageMath.Spatial;

namespace SenX.Monitor.Verify;

/// <summary>
/// Proves cluster timing against the real thing with no server: the game's own MyPhysics code, with
/// Monitor's real patches and hooks, stepping real worlds in the real Havok library. What it can't
/// cover is a full game session, so entity callbacks during a step aren't exercised.
/// </summary>
internal static unsafe class PhysicsCheck
{
    private const int Frames = 300;
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly List<string> s_failures = new();
    private static HkJobThreadPool s_pool = null!;
    private static HkJobQueue s_queue = null!;
    private static MyPhysics s_physics = null!;
    private static MethodInfo s_stepInternal = null!;
    private static MethodInfo s_stepWorlds = null!;

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    private sealed class NoSection : ISharedCriticalSection
    {
        public SharedCriticalSection_UniqueLock EnterUnique() => new(this);

        public SharedCriticalSection_SharedLock EnterShared() => new(this);

        public void LeaveUnique_Internal()
        {
        }

        public void LeaveShared_Internal()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class World
    {
        public int ClusterId;
        public HkWorld Havok = null!;
        public HkRigidBody FreeFall = null!;
        public List<HkRigidBody> Boxes = new();
    }

    public static int Run(string ds64)
    {
        SetDllDirectory(ds64);
        StartHavok();

        double[] baseline = Baseline();
        ExpectStage("Patching with Harmony");
        PatchEngine.Apply();
        Check("every physics site is patched exactly as verified", PatchEngine.ClusterTurnProblem() == null);
        Console.WriteLine("  Cluster turns: " + (PatchEngine.ClusterTurnProblem() ?? "safe to use"));

        ThreadBlocks.Initialize(new DetailLayout { MainSlots = 1 << 12, WorkerSlots = 1 << 8, KeySlots = 1 << 8, SlowEntries = 1 << 6, WorkerBlocks = 4 });
        ThreadBlocks.BindMainThread();
        DetailController.Configure(new[] { Site.EntityBefore });
        DetailController.SetMode(DetailMode.Grouped);
        Capture.Inline = true;

        CheckAnotherPluginStopsIt();
        CheckClosedGateChangesNothing(baseline);
        CheckTurnsChangeNothing(baseline);
        CheckOptimizerIsLeftAlone();
        CheckFullRun();

        PatchEngine.Remove();
        Console.WriteLine();

        foreach (string failure in s_failures)
            Console.WriteLine("FAILED: " + failure);

        Console.WriteLine(s_failures.Count == 0 ? "All physics checks passed." : s_failures.Count + " physics checks failed.");
        return s_failures.Count == 0 ? 0 : 1;
    }

    private static void StartHavok()
    {
        HkBaseSystem.Init(16777216, message => { }, false, new NoSection());
        HkAccessControl.TakeOwnership();

        s_pool = new HkJobThreadPool();
        s_queue = new HkJobQueue(s_pool.ThreadCount + 1);

        Type type = typeof(MyPhysics);
        type.GetField("m_threadPool", Any)!.SetValue(null, s_pool);
        type.GetField("m_jobQueue", Any)!.SetValue(null, s_queue);

        // Only the stepping code is under test, so this skips a session-bound load.
        s_physics = (MyPhysics)FormatterServices.GetUninitializedObject(type);
        type.GetField("m_timings", Any)!.SetValue(s_physics, new List<VRage.MyTuple<HkWorld, VRage.Library.Utils.MyTimeSpan>>());
        type.GetMethod("InitStepOptimizer", Any)!.Invoke(s_physics, null);

        s_stepInternal = type.GetMethod("StepWorldsInternal", Any)!;
        s_stepWorlds = type.GetMethod("StepWorlds", Any)!;

        Console.WriteLine($"Real Havok running, {s_pool.ThreadCount} worker threads.");
    }

    private static void ExpectStage(string name) => Console.WriteLine(name + "...");

    private static List<World> MakeWorlds(params int[] boxes)
    {
        var worlds = new List<World>();
        var tree = new MyClusterTree(null, false);
        int next = 0;

        tree.OnClusterCreated = (Func<int, BoundingBoxD, object>)((id, box) =>
        {
            World world = MakeWorld(boxes[next++]);
            world.ClusterId = id;
            worlds.Add(world);
            return world.Havok;
        });

        MethodInfo create = typeof(MyClusterTree).GetMethod("CreateCluster", Any)!;
        for (int i = 0; i < boxes.Length; i++)
        {
            var bounds = new BoundingBoxD(new Vector3D(i * 100000d), new Vector3D((i * 100000d) + 50000d));
            create.Invoke(tree, new object[] { bounds });
        }

        MyPhysics.Clusters = tree;
        return worlds;
    }

    private static World MakeWorld(int boxes)
    {
        var world = new HkWorld(true, 8000f, float.MaxValue, true, 8);
        world.MarkForWrite();
        world.InitMultithreading(s_pool, s_queue);

        var made = new World { Havok = world };

        var floor = new HkRigidBodyCinfo
        {
            MotionType = HkMotionType.Fixed,
            Position = new Vector3(0f, -1f, 0f),
            Shape = new HkBoxShape(new Vector3(300f, 1f, 300f)),
            QualityType = HkCollidableQualityType.Fixed,
        };
        world.AddRigidBody(new HkRigidBody(floor));

        var box = new HkBoxShape(new Vector3(0.5f));

        for (int i = 0; i < boxes; i++)
        {
            HkRigidBody body = Body(box, new Vector3((i % 10) * 1.2f, 1f + ((i / 100) * 1.2f), ((i / 10) % 10) * 1.2f), 100f);
            body.EnableDeactivation = false;
            world.AddRigidBody(body);
            made.Boxes.Add(body);
        }

        // Far from everything, so its height counts exactly how many steps this world took.
        made.FreeFall = Body(box, new Vector3(2000f, 3000f, 2000f), 10f);
        made.FreeFall.EnableDeactivation = false;
        world.AddRigidBody(made.FreeFall);

        return made;
    }

    private static HkRigidBody Body(HkShape shape, Vector3 position, float mass)
    {
        var info = new HkRigidBodyCinfo
        {
            MotionType = HkMotionType.Box_Inertia,
            Position = position,
            Mass = mass,
            Shape = shape,
            QualityType = HkCollidableQualityType.Moving,
            LinearDamping = 0f,
        };

        info.CalculateBoxInertiaTensor(new Vector3(0.5f), mass);
        return new HkRigidBody(info);
    }

    private static void StepFrame() => s_stepInternal.Invoke(s_physics, new object?[] { null });

    private static double[] Heights(List<World> worlds) =>
        worlds.Select(world => (double)world.FreeFall.Position.Y).ToArray();

    private static void Dispose(List<World> worlds)
    {
        foreach (World world in worlds)
            world.Havok.Dispose();
    }

    private static double[] Baseline()
    {
        ExpectStage("Unpatched game code, for comparison");

        List<World> worlds = MakeWorlds(20, 20, 20, 20);
        for (int frame = 0; frame < Frames; frame++)
            StepFrame();

        double[] heights = Heights(worlds);
        Dispose(worlds);
        return heights;
    }

    // A prefix that returns a value can skip the method, so this one can't be trusted to leave stepping alone.
    private static bool OtherPluginPrefix() => true;

    private static void OtherPluginWatches(out long __state) => __state = 0L;

    private static void CheckAnotherPluginStopsIt()
    {
        ExpectStage("Another plugin patching the same methods");

        const string Owner = "some.other.plugin";
        var other = new Harmony(Owner);
        MethodInfo prefix = typeof(PhysicsCheck).GetMethod(nameof(OtherPluginPrefix), Any)!;

        foreach (string name in new[] { "StepWorldsParallel", "StepSingleWorld", "IsClusterActive", "StepWorldsInternal" })
        {
            other.Patch(typeof(MyPhysics).GetMethod(name, Any), new HarmonyMethod(prefix));
            string? problem = PatchEngine.ClusterTurnProblem();
            SenX.Monitor.Magnetar.Patching.OwnPatches.Remove(other);

            Console.WriteLine("  " + name + ": " + (problem ?? "not noticed"));
            Check("a patch on " + name + " turns cluster timing off", problem != null && problem.Contains(Owner));
        }

        Check("cluster timing comes back once it's gone", PatchEngine.ClusterTurnProblem() == null);

        // The way Quasar's agent times StepWorldsInternal on every managed server.
        MethodBase stepping = typeof(MyPhysics).GetMethod("StepWorldsInternal", Any)!;
        other.Patch(stepping, new HarmonyMethod(typeof(PhysicsCheck).GetMethod(nameof(OtherPluginWatches), Any)!));
        Check("a plugin that only watches leaves cluster timing on", PatchEngine.ClusterTurnProblem() == null);
        SenX.Monitor.Magnetar.Patching.OwnPatches.Remove(other);

        // A game update, or a hook that failed to apply, must turn it off the same way.
        Check("real results are accepted", PatchEngine.ClusterSitesVerified(PatchEngine.Results));
        Check("changed game code turns cluster timing off", !PatchEngine.ClusterSitesVerified(Altered(SiteState.Drifted, 0)));
        Check("an unavailable site turns cluster timing off", !PatchEngine.ClusterSitesVerified(Altered(SiteState.Unavailable, 0)));
        Check("a call site left unpatched turns cluster timing off", !PatchEngine.ClusterSitesVerified(Altered(SiteState.Ok, 1)));
        Check(
            "one hook without the other turns cluster timing off",
            !PatchEngine.ClusterSitesVerified(PatchEngine.Results.Where(result => result.Target.Method != "IsClusterActive")));
    }

    // Keeps the real results, but changes the last cluster site, so each case has exactly one thing wrong.
    private static List<SiteResult> Altered(SiteState state, int fewerPatched)
    {
        List<SiteResult> results = PatchEngine.Results.ToList();
        int index = results.FindLastIndex(result => result.Target.Scope == Site.PhysicsCluster);
        SiteResult site = results[index];

        results[index] = new SiteResult(site.Caller, site.Target, state, site.Found, site.Patched - fewerPatched, "altered by this check");
        return results;
    }

    private static void CheckClosedGateChangesNothing(double[] baseline)
    {
        ExpectStage("Patched, but not timing");

        List<World> worlds = MakeWorlds(20, 20, 20, 20);
        for (int frame = 0; frame < Frames; frame++)
            StepFrame();

        Compare("closed hooks change nothing", baseline, Heights(worlds));
        Dispose(worlds);
    }

    private static void CheckTurnsChangeNothing(double[] baseline)
    {
        ExpectStage("Patched and timing, with clusters taking turns");

        List<World> worlds = MakeWorlds(20, 20, 20, 20);
        ClusterTurns.Reset();
        Gates.Open(Site.PhysicsCluster, 0);

        for (int frame = 0; frame < Frames; frame++)
            StepFrame();

        Gates.Close(Site.PhysicsCluster);
        Compare("taking turns steps every world exactly once per frame", baseline, Heights(worlds));
        Dispose(worlds);
    }

    private static void Compare(string name, double[] expected, double[] actual)
    {
        double worst = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Max();
        Console.WriteLine($"  Largest difference from unpatched physics: {worst:0.000000} m");
        Check(name, worst < 0.0001d);
    }

    private static void CheckOptimizerIsLeftAlone()
    {
        ExpectStage("The game's physics optimizer");

        Type type = typeof(MyPhysics);
        FieldInfo average = type.GetField("m_averageFrameTime", Any)!;
        FieldInfo slow = type.GetField("m_slowFrames", Any)!;
        FieldInfo fast = type.GetField("m_fastFrames", Any)!;
        FieldInfo optimize = type.GetField("m_optimizeNextFrame", Any)!;

        // Heavy enough to pass the 10 ms the game treats as slow, or nothing here could ever trigger it.
        List<World> worlds = MakeWorlds(4000);
        Gates.Open(Site.PhysicsCluster, 0);

        int triggered = 0;
        int restored = 0;
        const int Attempts = 20;

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            // One slow frame from turning collision checks off. Resetting turns makes this a timed step.
            average.SetValue(s_physics, 0.001f);
            slow.SetValue(s_physics, 10);
            fast.SetValue(s_physics, 5);
            optimize.SetValue(s_physics, false);
            ClusterTurns.Reset();

            s_stepWorlds.Invoke(s_physics, null);

            if ((bool)optimize.GetValue(s_physics))
                triggered++;

            PhysicsHooks.RestoreOptimizer();

            if ((float)average.GetValue(s_physics) == 0.001f
                && (int)slow.GetValue(s_physics) == 10
                && (int)fast.GetValue(s_physics) == 5
                && !(bool)optimize.GetValue(s_physics))
            {
                restored++;
            }
        }

        Gates.Close(Site.PhysicsCluster);
        optimize.SetValue(s_physics, false);

        Console.WriteLine($"  A timed step tripped the optimizer {triggered} of {Attempts} times, and the state was put back {restored} of {Attempts} times.");
        Check("a timed step really would have tripped the optimizer", triggered > 0);
        Check("the optimizer's state always comes back exactly", restored == Attempts);

        Dispose(worlds);
    }

    private static void CheckFullRun()
    {
        ExpectStage("A full measuring run with worlds of different weights");

        List<World> worlds = MakeWorlds(10, 700, 100, 300);
        FrameRecord* record = Native.Allocate<FrameRecord>(1);
        CaptureResult? result = null;

        Capture.TryStart(600, null, delivered => result = delivered, clusters: true);

        for (int i = 0; i < 32 && Capture.State != CaptureState.Measuring; i++)
            Capture.OnFrame(record, true, false);

        for (int frame = 0; frame < 1800; frame++)
        {
            StepFrame();
            PhysicsHooks.RestoreOptimizer();
            Capture.OnFrame(record, true, false);
        }

        Capture.ExpireForTests();
        for (int i = 0; i < 32 && Capture.State != CaptureState.Idle; i++)
            Capture.OnFrame(record, true, false);

        Capture.Deliver();

        Check("the run finished", result != null);
        if (result == null)
            return;

        Check("every world was timed", result.Clusters.Count == worlds.Count);

        RollupRow[] rows = result.Clusters.Top(worlds.Count, row => row.TotalUnits / (double)row.Calls);
        int[] boxes = rows.Select(row => worlds.First(world => world.ClusterId == ClusterTurns.ClusterIdOf(row)).Boxes.Count).ToArray();

        foreach (RollupRow row in rows)
        {
            World world = worlds.First(candidate => candidate.ClusterId == ClusterTurns.ClusterIdOf(row));
            Console.WriteLine($"  {world.Boxes.Count,4} boxes: {row.TotalUnits / (double)row.Calls / 10000d:0.000} ms alone, {row.Calls} steps, peak {row.MaxUnits / 10000d:0.000} ms");
        }

        Check("clusters rank by their real weight", boxes.SequenceEqual(new[] { 700, 300, 100, 10 }));

        double perFrame = result.PerFrame(result.SiteTotal(Site.PhysicsCluster).MainUnits) / 10000d;
        double budget = ClusterTurns.FrameShare * 1000d / 60d;
        Console.WriteLine($"  Stepping alone took {perFrame:0.000} ms/f, against a budget of {budget:0.000} ms/f.");
        Check("stepping alone stays inside its budget", perFrame <= budget * 1.1d);

        List<string> report = LagReport.Build(LagView.Physics, CaptureArgs.Parse(new string[0], out _)!, result, null).ToString().Split('\n').Select(line => line.Trim()).ToList();
        foreach (string line in report)
            Console.WriteLine("  | " + line);

        Check("the report counts the clusters", report.Any(line => line.StartsWith("Timed 4 of 4 active clusters", StringComparison.Ordinal)));
        Check("the report lists each cluster", report.Count(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^\[\d\]")) == worlds.Count);
        Dispose(worlds);
    }

    private static void Check(string name, bool passed)
    {
        Console.WriteLine($"  {(passed ? "ok" : "FAILED")}: {name}");

        if (!passed)
            s_failures.Add(name);
    }
}
