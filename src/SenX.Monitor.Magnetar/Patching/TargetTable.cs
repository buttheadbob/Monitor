using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox.Game.World;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Hooks;
using VRage;
using VRage.Game.Entity;
using VRage.Network;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>
/// Everything Monitor measures, in one table. Names are strings on purpose: a game update that
/// renames something should mark one scope unavailable, not stop the plugin from building.
/// </summary>
internal static class TargetTable
{
    private const string Game = "Sandbox.Engine.Platform.Game";
    private const string SandboxGame = "Sandbox.MySandboxGame";
    private const string Session = "Sandbox.Game.World.MySession";
    private const string Multiplayer = "Sandbox.Engine.Multiplayer.MyMultiplayerBase";
    private const string Physics = "Sandbox.Engine.Physics.MyPhysics";
    private const string Entities = "Sandbox.Game.Entities.MyEntities";
    private const string Orchestrator = "Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator";
    private const string OrchestratorInterface = "Sandbox.Game.Entities.IMyUpdateOrchestrator";
    private const string Replication = "VRage.Network.MyReplicationLayer";
    private const string ReplicationServer = "VRage.Network.MyReplicationServer";
    private const string Peer2Peer = "VRage.GameServices.IMyPeer2Peer";
    private const string Parallelizable = "Sandbox.Game.Entities.IMyParallelUpdateable";
    private const string Entity = "VRage.Game.Entity.MyEntity";
    private const string Logic = "VRage.Game.Entity.EntityComponents.Interfaces.IMyGameLogicComponent";
    private const string GameLogic = "VRage.Game.Entity.MyGameLogic";
    private const string SessionComponent = "VRage.Game.Components.MySessionComponentBase";
    private const string Grid = "Sandbox.Game.Entities.MyCubeGrid";
    private const string ProgrammableBlock = "Sandbox.Game.Entities.Blocks.MyProgrammableBlock";
    private const string CompositeLogic = "Sandbox.Game.Entities.MyCompositeGameLogicComponent";

    public static List<CallerSpec> Build()
    {
        var table = new List<CallerSpec>();
        Assembly sandbox = typeof(MySession).Assembly;
        Assembly vrageGame = typeof(MyEntity).Assembly;
        Assembly vrage = typeof(MyReplicationLayer).Assembly;

        void Add(Assembly assembly, string type, string method, int parameters, params CallTarget[] targets)
        {
            string id = type + "::" + method + "/" + parameters;
            table.Add(new CallerSpec(id, Resolve(assembly, type, method, parameters), targets));
        }

        Add(sandbox, Game, "RunSingleFrame", 0, Frame(Game, "UpdateInternal"));
        Add(sandbox, Game, "UpdateInternal", 0, Tree(Game, "Update", 0, Scope.Update));

        Add(sandbox, SandboxGame, "Update", 0,
            Tree(SandboxGame, "ProcessInvoke", 1, Scope.InvokeQueue),
            Tree("Sandbox.Engine.Multiplayer.MyTransportLayer", "Tick", 0, Scope.NetTransport),
            Tree("Sandbox.Engine.Networking.MyGameService", "Update", 0, Scope.NetService),
            Tree("Sandbox.Engine.Networking.MyNetworkMonitor", "Update", 0, Scope.NetMonitor),
            Tree("Sandbox.Engine.Networking.MyNetworkReader", "Process", 0, Scope.NetRead),
            Tree(Multiplayer, "ReportReplicatedObjects", 0, Scope.NetReport),
            Tree(Session, "Update", 1, Scope.Session),
            Tree("VRage.Plugins.IPlugin", "Update", 0, Scope.Plugins));

        Add(sandbox, Session, "Update", 1,
            Tree(Replication, "UpdateClientStateGroups", 0, Scope.ClientStates),
            Tree("ParallelTasks.IWorkScheduler", "WaitForTasksToFinish", 1, Scope.TaskWait),
            Tree("ParallelTasks.Parallel", "RunCallbacks", 0, Scope.RunCallbacks),
            Tree(Replication, "UpdateBefore", 0, Scope.ReplicationBefore),
            Tree(Session, "UpdateComponents", 0, Scope.Components),
            Tree("Sandbox.Game.Multiplayer.MyGpsCollection", "Update", 0, Scope.Gps),
            Tree(Multiplayer, "Tick", 0, Scope.MultiplayerTick),
            Tree("Sandbox.Game.Multiplayer.MyPlayerCollection", "SendDirtyBlockLimits", 0, Scope.BlockLimits));

        Add(sandbox, Multiplayer, "Tick", 0, Tree(Replication, "SendUpdate", 0, Scope.ReplicationSend));

        // Every packet, one recipient at a time, in the game's own code on both Legacy and Interim.
        Add(sandbox, "Sandbox.Engine.Networking.MyNetworkWriter", "SendAll", 0,
            Counter(Peer2Peer, "SendPacket", 5, Hook(typeof(NetHooks), nameof(NetHooks.SendPacket))));
        Add(sandbox, "Sandbox.Engine.Networking.MyReceiveQueue", "ReceiveOne", 1,
            Counter(Peer2Peer, "ReadPacket", 4, Hook(typeof(NetHooks), nameof(NetHooks.ReadPacket))));
        Add(vrage, ReplicationServer, "OnEvent", 6,
            Swap(Replication, "Invoke", 6, 1, Site.Rpc, Hook(typeof(RpcHook), nameof(RpcHook.Invoke))));

        // Every event going out passes one of these two, which are all that know who it's going to.
        MethodBase? dispatch = Resolve(vrage, ReplicationServer, "DispatchEvent", 5);
        FieldInfo? recipients = typeof(MyReplicationServer).GetField("m_recipients", BindingFlags.NonPublic | BindingFlags.Static);

        Add(vrage, ReplicationServer, "DispatchEvent", 5,
            EventTally("List`1", dispatch, recipients?.FieldType == typeof(List<EndpointId>), new IlStep(OpCodes.Ldsfld, recipients)),
            EventTally("MyClient", dispatch, true, new IlStep(OpCodes.Ldnull)));

        Add(sandbox, Physics, "Simulate", 0, Tree(Physics, "SimulateInternal", 0, Scope.Physics));
        // Timing a cluster takes both of these, and only ever together: see PatchEngine.ClusterTurnProblem.
        Add(sandbox, Physics, "StepWorldsInternal", 1,
            Swap(Physics, "StepWorldsParallel", 0, 1, Site.PhysicsCluster, Hook(typeof(PhysicsHooks), nameof(PhysicsHooks.StepParallel))));
        Add(sandbox, Physics, "StepWorldsParallel", 0,
            Swap(Physics, "IsClusterActive", 2, 2, Site.PhysicsCluster, Hook(typeof(PhysicsHooks), nameof(PhysicsHooks.ClusterActive))));

        Add(sandbox, Physics, "SimulateInternal", 0,
            Tree(Physics, "ExecuteParallelRayCasts", 0, Scope.PhysicsRaycasts),
            Tree(Physics, "StepWorlds", 0, Scope.PhysicsStep),
            Tree(Physics, "UpdateActiveRigidBodies", 0, Scope.PhysicsBodies),
            Tree(Physics, "UpdateCharacters", 0, Scope.PhysicsCharacters),
            Tree(Physics, "EnsureClusterSpace", 0, Scope.PhysicsClusterSpace));

        Add(sandbox, Entities, "UpdateBeforeSimulation", 0, Tree(OrchestratorInterface, "DispatchBeforeSimulation", 0, Scope.EntitiesBefore));
        Add(sandbox, Entities, "UpdateAfterSimulation", 0,
            Tree(OrchestratorInterface, "DispatchAfterSimulation", 0, Scope.EntitiesAfter),
            Tree(Entities, "DeleteRememberedEntities", 0, Scope.EntityCleanup));
        Add(sandbox, Entities, "Simulate", 0, Tree(OrchestratorInterface, "DispatchSimulate", 0, Scope.EntitiesSimulate));
        Add(sandbox, Entities, "UpdateOnceBeforeFrame", 0, Tree(OrchestratorInterface, "DispatchOnceBeforeFrame", 0, Scope.EntitiesOnce));

        Add(sandbox, Orchestrator, "DispatchBeforeSimulation", 0,
            Tree(Orchestrator, "PerformParallelUpdate", 1, Scope.BeforeParallel),
            Tree(Orchestrator, "ProcessInvokeLater", 0, Scope.BeforeInvokeLater),
            Tree(Orchestrator, "UpdateBeforeSimulation", 0, Scope.BeforeSerial),
            Tree(Orchestrator, "UpdateBeforeSimulation10", 0, Scope.Before10),
            Tree(Orchestrator, "UpdateBeforeSimulation100", 0, Scope.Before100));

        Add(sandbox, Orchestrator, "DispatchAfterSimulation", 0,
            Tree(Orchestrator, "PerformParallelUpdate", 1, Scope.AfterParallel),
            Tree(Orchestrator, "ProcessInvokeLater", 0, Scope.AfterInvokeLater),
            Tree(Orchestrator, "UpdateAfterSimulation", 0, Scope.AfterSerial),
            Tree(Orchestrator, "UpdateAfterSimulation10", 0, Scope.After10),
            Tree(Orchestrator, "UpdateAfterSimulation100", 0, Scope.After100));

        Add(sandbox, Orchestrator, "UpdateBeforeSimulation", 0, EntityCall<BeforeCall>("UpdateBeforeSimulation", 1, Site.EntityBefore));
        Add(sandbox, Orchestrator, "UpdateBeforeSimulation10", 0, EntityCall<Before10Call>("UpdateBeforeSimulation10", 2, Site.EntityBefore10));
        Add(sandbox, Orchestrator, "UpdateBeforeSimulation100", 0, EntityCall<Before100Call>("UpdateBeforeSimulation100", 2, Site.EntityBefore100));
        Add(sandbox, Orchestrator, "UpdateAfterSimulation", 0, EntityCall<AfterCall>("UpdateAfterSimulation", 2, Site.EntityAfter));
        Add(sandbox, Orchestrator, "UpdateAfterSimulation10", 0, EntityCall<After10Call>("UpdateAfterSimulation10", 2, Site.EntityAfter10));
        Add(sandbox, Orchestrator, "UpdateAfterSimulation100", 0, EntityCall<After100Call>("UpdateAfterSimulation100", 2, Site.EntityAfter100));
        Add(sandbox, Orchestrator, "DispatchSimulate", 0, EntityCall<SimulateCall>("Simulate", 1, Site.EntitySimulate));
        Add(sandbox, Orchestrator, "DispatchOnceBeforeFrame", 0, EntityCall<OnceCall>("UpdateOnceBeforeFrame", 1, Site.EntityOnce));

        Add(sandbox, Orchestrator, "ParallelUpdateHandlerBeforeSimulation", 1,
            Swap(Parallelizable, "UpdateBeforeSimulationParallel", 0, 1, Site.ParallelBefore, Generic(typeof(ParallelHooks), nameof(ParallelHooks.Run), typeof(ParallelBeforeCall))));
        Add(sandbox, Orchestrator, "ParallelUpdateHandlerAfterSimulation", 1,
            Swap(Parallelizable, "UpdateAfterSimulationParallel", 0, 1, Site.ParallelAfter, Generic(typeof(ParallelHooks), nameof(ParallelHooks.Run), typeof(ParallelAfterCall))));

        Add(sandbox, Orchestrator, "ProcessInvokeLater", 0,
            Swap("System.Action", "Invoke", 0, 1, Site.InvokeLater, Hook(typeof(InvokeLaterHook), nameof(InvokeLaterHook.Invoke))));

        Add(sandbox, Session, "UpdateComponents", 0,
            Swap(SessionComponent, "UpdateBeforeSimulation", 0, 1, Site.SessionBefore, Generic(typeof(SessionHooks), nameof(SessionHooks.Run), typeof(SessionBeforeCall))),
            Swap(SessionComponent, "Simulate", 0, 1, Site.SessionSimulate, Generic(typeof(SessionHooks), nameof(SessionHooks.Run), typeof(SessionSimulateCall))),
            Swap(SessionComponent, "UpdateAfterSimulation", 0, 1, Site.SessionAfter, Generic(typeof(SessionHooks), nameof(SessionHooks.Run), typeof(SessionAfterCall))));

        Add(sandbox, Grid, "Dispatch", 2,
            Swap(Grid, "Invoke", 2, 1, Site.GridCallback, Hook(typeof(GridCallbackHook), nameof(GridCallbackHook.Invoke))));

        Add(sandbox, ProgrammableBlock, "ExecuteCode", 3,
            Swap(ProgrammableBlock, "RunSandboxedProgramAction", 2, 1, Site.Script, Hook(typeof(ScriptHook), nameof(ScriptHook.Run))));

        // Explicit interface methods, so their names carry the interface's name.
        Add(sandbox, CompositeLogic, Logic + ".UpdateBeforeSimulation", 1, LogicCall<LogicBefore, CompositeLogicSite>("UpdateBeforeSimulation"));
        Add(sandbox, CompositeLogic, Logic + ".UpdateBeforeSimulation10", 1, LogicCall<LogicBefore10, CompositeLogicSite>("UpdateBeforeSimulation10"));
        Add(sandbox, CompositeLogic, Logic + ".UpdateBeforeSimulation100", 1, LogicCall<LogicBefore100, CompositeLogicSite>("UpdateBeforeSimulation100"));
        Add(sandbox, CompositeLogic, Logic + ".UpdateAfterSimulation", 1, LogicCall<LogicAfter, CompositeLogicSite>("UpdateAfterSimulation"));
        Add(sandbox, CompositeLogic, Logic + ".UpdateAfterSimulation10", 1, LogicCall<LogicAfter10, CompositeLogicSite>("UpdateAfterSimulation10"));
        Add(sandbox, CompositeLogic, Logic + ".UpdateAfterSimulation100", 1, LogicCall<LogicAfter100, CompositeLogicSite>("UpdateAfterSimulation100"));
        Add(sandbox, CompositeLogic, Logic + ".UpdateOnceBeforeFrame", 1, LogicCall<LogicOnce, CompositeLogicSite>("UpdateOnceBeforeFrame"));

        Add(vrageGame, Entity, "UpdateBeforeSimulation", 0, LogicCall<LogicBefore, EntityLogicSite>("UpdateBeforeSimulation"));
        Add(vrageGame, Entity, "UpdateBeforeSimulation10", 0, LogicCall<LogicBefore10, EntityLogicSite>("UpdateBeforeSimulation10"));
        Add(vrageGame, Entity, "UpdateBeforeSimulation100", 0, LogicCall<LogicBefore100, EntityLogicSite>("UpdateBeforeSimulation100"));
        Add(vrageGame, Entity, "UpdateAfterSimulation", 0, LogicCall<LogicAfter, EntityLogicSite>("UpdateAfterSimulation"));
        Add(vrageGame, Entity, "UpdateAfterSimulation10", 0, LogicCall<LogicAfter10, EntityLogicSite>("UpdateAfterSimulation10"));
        Add(vrageGame, Entity, "UpdateAfterSimulation100", 0, LogicCall<LogicAfter100, EntityLogicSite>("UpdateAfterSimulation100"));
        Add(vrageGame, Entity, "UpdateOnceBeforeFrame", 0, LogicCall<LogicOnce, EntityLogicSite>("UpdateOnceBeforeFrame"));

        Add(vrageGame, GameLogic, "UpdateOnceBeforeFrame", 0, LogicCall<LogicOnce, GameLogicSite>("UpdateOnceBeforeFrame"));
        AddGameLogicLambdas(table, vrageGame);

        return table;
    }

    /// <summary>
    /// Component updates in MyGameLogic run inside lambdas passed to Iterate, so the calls are in the
    /// lambdas and not in the methods that create them. The lambdas are found from the ldftn that builds each one.
    /// </summary>
    private static void AddGameLogicLambdas(List<CallerSpec> table, Assembly vrageGame)
    {
        var targets = new[]
        {
            LogicCall<LogicBefore, GameLogicSite>("UpdateBeforeSimulation"),
            LogicCall<LogicBefore10, GameLogicSite>("UpdateBeforeSimulation10"),
            LogicCall<LogicBefore100, GameLogicSite>("UpdateBeforeSimulation100"),
            LogicCall<LogicAfter, GameLogicSite>("UpdateAfterSimulation"),
            LogicCall<LogicAfter10, GameLogicSite>("UpdateAfterSimulation10"),
            LogicCall<LogicAfter100, GameLogicSite>("UpdateAfterSimulation100"),
        };

        foreach (string outer in new[] { "UpdateBeforeSimulation", "UpdateAfterSimulation" })
        {
            string id = GameLogic + "::" + outer + "/lambdas";
            MethodBase? method = Resolve(vrageGame, GameLogic, outer, 0);
            if (method == null)
            {
                table.Add(new CallerSpec(id, null, targets));
                continue;
            }

            foreach (IlStep step in PatchEngine.ReadIl(method))
            {
                if (step.OpCode != OpCodes.Ldftn || !(step.Operand is MethodInfo lambda))
                    continue;

                CallTarget? match = FindLambdaTarget(lambda, targets);
                if (match != null)
                    table.Add(new CallerSpec(GameLogic + "::" + outer + "/" + match.Method, lambda, new[] { match }));
            }
        }
    }

    private static CallTarget? FindLambdaTarget(MethodInfo lambda, CallTarget[] targets)
    {
        foreach (IlStep step in PatchEngine.ReadIl(lambda))
        {
            foreach (CallTarget target in targets)
            {
                if (CallMatch.Matches(step.OpCode, step.Operand, target))
                    return target;
            }
        }

        return null;
    }

    private static CallTarget Tree(string type, string method, int parameters, int scope) =>
        new(type, method, parameters, 1, CallSiteKind.Bracket, scope, null);

    private static CallTarget Frame(string type, string method) =>
        new(type, method, 0, 1, CallSiteKind.FrameEnd, Scope.Frame, null);

    private static CallTarget Swap(string type, string method, int parameters, int expected, int site, MethodInfo? hook) =>
        new(type, method, parameters, expected, CallSiteKind.Substitute, site, hook);

    private static CallTarget Counter(string type, string method, int parameters, MethodInfo? hook) =>
        new(type, method, parameters, 1, CallSiteKind.Counter, Scope.NetMonitor, hook);

    // These load the caller's own arguments by position, so they only go in while those are what the
    // hook expects. A changed method marks a tally unavailable, where the wrong type would be a crash.
    private static CallTarget EventTally(string overload, MethodBase? caller, bool recipientsFit, IlStep recipients) =>
        new(ReplicationServer, "DispatchEvent", 3, 1, CallSiteKind.Tally, Scope.NetMonitor,
            recipientsFit && TakesAnEvent(caller) ? Hook(typeof(RpcHook), nameof(RpcHook.NoteSent)) : null,
            overload,
            new[] { new IlStep(OpCodes.Ldarg_1), new IlStep(OpCodes.Ldarg_2), recipients });

    /// <summary>True when a method takes an event's data and then its call site, after <c>this</c>.</summary>
    internal static bool TakesAnEvent(MethodBase? caller)
    {
        if (caller == null || caller.IsStatic)
            return false;

        ParameterInfo[] parameters = caller.GetParameters();

        return parameters.Length >= 2 && parameters[0].ParameterType == typeof(IPacketData) && parameters[1].ParameterType == typeof(CallSite);
    }

    private static CallTarget EntityCall<T>(string method, int expected, int site) where T : struct, IEntityCall =>
        Swap(Entity, method, 0, expected, site, Generic(typeof(EntityHooks), nameof(EntityHooks.Run), typeof(T)));

    private static CallTarget LogicCall<TCall, TSite>(string method)
        where TCall : struct, ILogicCall
        where TSite : struct, ISiteId =>
        Swap(Logic, method, 1, 1, default(TSite).Id, Generic(typeof(LogicHooks), nameof(LogicHooks.Run), typeof(TCall), typeof(TSite)));

    private static MethodInfo? Hook(Type type, string method) =>
        type.GetMethod(method, BindingFlags.Public | BindingFlags.Static);

    private static MethodInfo? Generic(Type type, string method, params Type[] arguments) =>
        Hook(type, method)?.MakeGenericMethod(arguments);

    private static MethodBase? Resolve(Assembly assembly, string typeName, string method, int parameters)
    {
        Type? type = assembly.GetType(typeName, throwOnError: false);
        if (type == null)
            return null;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                   BindingFlags.Static | BindingFlags.DeclaredOnly;

        List<MethodInfo> matches = type.GetMethods(flags)
            .Where(candidate => candidate.Name == method && candidate.GetParameters().Length == parameters)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }
}
