namespace SenX.Monitor.Api;

/// <summary>A chart <see cref="IMonitorTools.ChartImageAsync"/> can draw.</summary>
public enum MonitorChart
{
    /// <summary>Sim speed, with lines at full speed and at 0.7.</summary>
    SimSpeed,

    /// <summary>The time left in a frame, on average and in the worst 1% of frames.</summary>
    AvailableTime,

    /// <summary>Frame time at p50, p95 and p99, with the frame budget.</summary>
    FrameTime,

    /// <summary>What's using the frame, stacked up against the frame budget, with sim speed on a second scale.</summary>
    FrameParts,

    /// <summary>Time spent updating against time left over.</summary>
    GameLoop,

    /// <summary>Garbage collections each second.</summary>
    GarbageCollections,

    /// <summary>What the main thread allocated.</summary>
    Allocation,

    /// <summary>Working set and managed memory.</summary>
    Memory,

    /// <summary>Players online.</summary>
    Players,

    /// <summary>Total blocks.</summary>
    Blocks,

    /// <summary>Total PCU.</summary>
    Pcu,

    /// <summary>Mean and worst player ping.</summary>
    ServerPing,

    /// <summary>Grids, characters and floating objects.</summary>
    Objects,

    /// <summary>What's sent and received.</summary>
    Traffic,

    /// <summary>What's being sent, stacked by kind.</summary>
    TrafficKinds,

    /// <summary>RPCs from clients each second.</summary>
    RpcRate,

    /// <summary>The costliest grids over time. This and the other costs come from background totals, so they're for trends.</summary>
    GridCosts,

    /// <summary>The costliest owners over time.</summary>
    OwnerCosts,

    /// <summary>The costliest block types over time.</summary>
    BlockTypeCosts,

    /// <summary>The costliest mods and plugins over time.</summary>
    ModCosts,

    /// <summary>The costliest scripts over time.</summary>
    ScriptCosts,

    /// <summary>The costliest session components over time.</summary>
    SessionCosts,

    /// <summary>The costliest game logic over time.</summary>
    GameLogicCosts,

    /// <summary>The costliest grid callbacks over time.</summary>
    CallbackCosts,
}
