namespace SenX.Monitor.Core;

/// <summary>
/// Ids of the always-on tree. Constants, so a patched call site carries a literal and never does
/// a lookup. Ids 1 to <see cref="Count"/> - 1 are tree scopes. Detail sites start at <see cref="Site"/>.
/// </summary>
public static class Scope
{
    public const int None = 0;

    public const int Frame = 1;
    public const int Update = 2;
    public const int InvokeQueue = 3;

    public const int Network = 4;
    public const int NetTransport = 5;
    public const int NetService = 6;
    public const int NetMonitor = 7;
    public const int NetRead = 8;
    public const int NetReport = 9;

    public const int Plugins = 10;
    public const int Session = 11;
    public const int ClientStates = 12;
    public const int TaskWait = 13;
    public const int RunCallbacks = 14;
    public const int ReplicationBefore = 15;
    public const int Components = 16;
    public const int Gps = 17;
    public const int MultiplayerTick = 18;
    public const int ReplicationSend = 19;
    public const int BlockLimits = 20;

    public const int Physics = 21;
    public const int PhysicsRaycasts = 22;
    public const int PhysicsStep = 23;
    public const int PhysicsBodies = 24;
    public const int PhysicsCharacters = 25;
    public const int PhysicsClusterSpace = 26;

    public const int EntitiesBefore = 27;
    public const int BeforeParallel = 28;
    public const int BeforeInvokeLater = 29;
    public const int BeforeSerial = 30;
    public const int Before10 = 31;
    public const int Before100 = 32;

    public const int EntitiesAfter = 33;
    public const int AfterParallel = 34;
    public const int AfterInvokeLater = 35;
    public const int AfterSerial = 36;
    public const int After10 = 37;
    public const int After100 = 38;
    public const int EntityCleanup = 39;

    public const int EntitiesSimulate = 40;
    public const int EntitiesOnce = 41;

    /// <summary>Monitor's own cost, so it is visible in the tree like everything else.</summary>
    public const int Monitor = 42;

    /// <summary>One past the last tree scope. Sizes per-frame records.</summary>
    public const int Count = 43;
}

/// <summary>Ids of the gated detail sites. They share the id space with the tree, above it.</summary>
public static class Site
{
    public const int EntityBefore = 64;
    public const int EntityBefore10 = 65;
    public const int EntityBefore100 = 66;
    public const int EntityAfter = 67;
    public const int EntityAfter10 = 68;
    public const int EntityAfter100 = 69;
    public const int EntitySimulate = 70;
    public const int EntityOnce = 71;
    public const int ParallelBefore = 72;
    public const int ParallelAfter = 73;
    public const int GameLogic = 74;
    public const int EntityLogic = 75;
    public const int SessionBefore = 76;
    public const int SessionSimulate = 77;
    public const int SessionAfter = 78;
    public const int GridCallback = 79;
    public const int InvokeLater = 80;
    public const int Script = 81;

    /// <summary>
    /// Physics clusters stepping alone. Not in any group, so only a run that asks for it opens this.
    /// It changes how physics is scheduled, so neither detail mode ever turns it on.
    /// </summary>
    public const int PhysicsCluster = 82;

    /// <summary>The parts of a composite game logic component, each timed on its own.</summary>
    public const int CompositeLogic = 83;

    /// <summary>Each plugin's own Update, called by the loader once a frame.</summary>
    public const int PluginUpdate = 84;

    /// <summary>An RPC from a client, from checking it to the end of its handler.</summary>
    public const int Rpc = 85;

    public const int First = EntityBefore;
    public const int Last = Rpc;
    public const int Count = Last - First + 1;
}

/// <summary>One node of the tree.</summary>
public readonly struct ScopeDefinition
{
    public ScopeDefinition(int id, int parent, string name)
    {
        Id = id;
        Parent = parent;
        Name = name;
    }

    public int Id { get; }

    public int Parent { get; }

    public string Name { get; }
}

/// <summary>
/// The declared shape of the tree. Self time comes from this: inclusive time less the children.
/// Because the shape is declared, measuring needs no runtime stack. The fingerprint check exists
/// to catch a game update that moves a call and makes this shape stale.
/// </summary>
public static class ScopeTable
{
    public static readonly ScopeDefinition[] All =
    {
        new(Scope.Frame, Scope.None, "Frame"),
        new(Scope.Update, Scope.Frame, "Update"),
        new(Scope.InvokeQueue, Scope.Update, "Invoke queue"),
        new(Scope.Network, Scope.Update, "Network"),
        new(Scope.NetTransport, Scope.Network, "Transport"),
        new(Scope.NetService, Scope.Network, "Game service"),
        new(Scope.NetMonitor, Scope.Network, "Monitor"),
        new(Scope.NetRead, Scope.Network, "Read"),
        new(Scope.NetReport, Scope.Network, "Report objects"),
        new(Scope.Plugins, Scope.Update, "Plugins"),
        new(Scope.Session, Scope.Update, "Session"),
        new(Scope.ClientStates, Scope.Session, "Client states"),
        new(Scope.TaskWait, Scope.Session, "Task wait"),
        new(Scope.RunCallbacks, Scope.Session, "Parallel callbacks"),
        new(Scope.ReplicationBefore, Scope.Session, "Replication before"),
        new(Scope.Components, Scope.Session, "Components"),
        new(Scope.Gps, Scope.Session, "GPS"),
        new(Scope.MultiplayerTick, Scope.Session, "Multiplayer tick"),
        new(Scope.ReplicationSend, Scope.MultiplayerTick, "Replication send"),
        new(Scope.BlockLimits, Scope.Session, "Block limits"),
        new(Scope.Physics, Scope.Components, "Physics"),
        new(Scope.PhysicsRaycasts, Scope.Physics, "Raycasts"),
        new(Scope.PhysicsStep, Scope.Physics, "Step"),
        new(Scope.PhysicsBodies, Scope.Physics, "Rigid bodies"),
        new(Scope.PhysicsCharacters, Scope.Physics, "Characters"),
        new(Scope.PhysicsClusterSpace, Scope.Physics, "Cluster space"),
        new(Scope.EntitiesBefore, Scope.Components, "Entities before"),
        new(Scope.BeforeParallel, Scope.EntitiesBefore, "Parallel"),
        new(Scope.BeforeInvokeLater, Scope.EntitiesBefore, "Invoke later"),
        new(Scope.BeforeSerial, Scope.EntitiesBefore, "Serial"),
        new(Scope.Before10, Scope.EntitiesBefore, "Every 10"),
        new(Scope.Before100, Scope.EntitiesBefore, "Every 100"),
        new(Scope.EntitiesAfter, Scope.Components, "Entities after"),
        new(Scope.AfterParallel, Scope.EntitiesAfter, "Parallel"),
        new(Scope.AfterInvokeLater, Scope.EntitiesAfter, "Invoke later"),
        new(Scope.AfterSerial, Scope.EntitiesAfter, "Serial"),
        new(Scope.After10, Scope.EntitiesAfter, "Every 10"),
        new(Scope.After100, Scope.EntitiesAfter, "Every 100"),
        new(Scope.EntityCleanup, Scope.EntitiesAfter, "Cleanup"),
        new(Scope.EntitiesSimulate, Scope.Components, "Entities simulate"),
        new(Scope.EntitiesOnce, Scope.Components, "Entities once"),
        new(Scope.Monitor, Scope.None, "Monitor"),
    };
}
