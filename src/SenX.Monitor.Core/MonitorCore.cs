namespace SenX.Monitor.Core;

/// <summary>Sets up everything hot code depends on. Must run before any hook is patched in.</summary>
public static class MonitorCore
{
    /// <summary>
    /// Initializes the clock, gates and tree counters. Allocates only a few small tables, so it is
    /// safe during plugin startup. Detail memory is separate: see <see cref="ThreadBlocks.Initialize"/>.
    /// </summary>
    public static void Initialize()
    {
        Clock.Initialize();
        Recorder.Initialize();
        Gates.Initialize();
        TreeCounters.Initialize();
        DelegateIdentity.Initialize();
        GcProbe.Initialize();
        FrameEngine.Initialize();
        Census.Initialize();
        Sweep.Initialize();
        Markers.Initialize();
        NetTraffic.Initialize();
    }
}
