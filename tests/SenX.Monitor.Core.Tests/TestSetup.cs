using System;
using SenX.Monitor.Core;
using Xunit;

// Core is built on process-wide statics, so tests run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SenX.Monitor.Core.Tests;

internal static class TestSetup
{
    public const int MainSlots = 1 << 12;
    public const int WorkerSlots = 1 << 8;
    public const int WorkerBlocks = 4;

    private static readonly object s_gate = new();

    /// <summary>Sets up small detail tables once, then gives each test clean memory and the main block.</summary>
    public static void Reset()
    {
        lock (s_gate)
        {
            MonitorCore.Initialize();
            ThreadBlocks.Initialize(new DetailLayout
            {
                MainSlots = MainSlots,
                WorkerSlots = WorkerSlots,
                KeySlots = 1 << 6,
                SlowEntries = 1 << 4,
                WorkerBlocks = WorkerBlocks,
            });

            ThreadBlocks.ClearAll();

            // A server sets these together each frame, and some tests set this one by hand. Left behind, it
            // would date what a later test records far enough back that a run takes it for an old call.
            Recorder.Frame = FrameEngine.Frame;
            Capture.ResetForTests();
            Census.ResetForTests();
            Sweep.ResetForTests();
            LagWatch.ResetForTests();
            NetTraffic.ResetForTests();
            DetailController.Configure(new int[0]);
            Gates.CloseAll();
            ThreadBlocks.ClearAll();
            ThreadBlocks.BeginPeriod();
            ThreadBlocks.BindMainThread();
        }
    }
}

/// <summary>Measures managed allocation on the current thread, after a warm-up so JIT work doesn't count.</summary>
internal static class Allocation
{
    public static long Measure(Action work)
    {
        Assert.True(GcProbe.CanReadThreadAllocation, "This runtime can't report thread allocation, so the test proves nothing.");

        work();
        work();

        // The fewest bytes of three tries. Code that allocates does so every time, but the runtime can allocate once
        // on this thread for itself, such as late JIT work on a busy machine, and that mustn't fail a test.
        long fewest = long.MaxValue;

        for (int attempt = 0; attempt < 3 && fewest > 0L; attempt++)
        {
            long before = GcProbe.AllocatedOnThread();
            work();
            fewest = Math.Min(fewest, GcProbe.AllocatedOnThread() - before);
        }

        return fewest;
    }
}
