using System;
using System.Diagnostics;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SenX.Monitor.Core;
using VRage.Game.Entity;
using VRage.GameServices;
using VRage.ModAPI;
using VRage.Network;
using VRage.Serialization;

namespace SenX.Monitor.Magnetar.World;

/// <summary>
/// Feeds the census from the live game, on the game thread. Counts come from the world itself. Ping
/// comes from the game, because that's the one figure Monitor can't measure for itself. If anything
/// here fails, the census stops and everything else carries on.
/// </summary>
internal struct CensusWalker : ICensusSource
{
    // Concealment's own mark on what it has stopped, in both its Torch and Magnetar builds. The game doesn't use this bit.
    private const EntityFlags ConcealedFlag = (EntityFlags)4;

    // Plain references, held only between a snapshot and the visit that clears them.
    private static MyEntity?[] s_entities = new MyEntity?[1 << 14];
    private static bool s_stopped;

    // Kept, because making one for each pass is the costly part of asking.
    private static Process? s_process;
    private static bool s_noCpu;

    /// <summary>Called once per frame, off the frame hook.</summary>
    public static void Step()
    {
        if (s_stopped || MySession.Static == null)
            return;

        try
        {
            var walker = default(CensusWalker);
            Census.Step(ref walker, FrameEngine.Paused);
        }
        catch (Exception exception)
        {
            s_stopped = true;
            Array.Clear(s_entities, 0, s_entities.Length);
            MonitorLog.Error("Monitor's census failed and has stopped. Measuring carries on.", exception);
        }
    }

    public int Snapshot()
    {
        var entities = MyEntities.GetEntities();

        if (entities.Count > s_entities.Length)
            s_entities = new MyEntity?[entities.Count * 2];

        int count = 0;

        foreach (MyEntity entity in entities)
        {
            if (count == s_entities.Length)
                break;

            s_entities[count++] = entity;
        }

        return count;
    }

    public void Visit(int index)
    {
        MyEntity? entity = s_entities[index];
        s_entities[index] = null;

        // The world can change between slices, so a snapshot may hold something already gone.
        if (entity == null || entity.MarkedForClose)
            return;

        switch (entity)
        {
            case MyCubeGrid grid when !grid.IsPreview:
                Census.AddGrid(FactionOfGrid(grid), grid.BlocksCount, grid.BlocksPCU, (grid.Flags & ConcealedFlag) != 0, grid.EntityId, OwnerOfGrid(grid), grid.IsStatic);
                break;
            case MyCharacter _:
                Census.AddCharacter();
                break;
            case MyFloatingObject _:
                Census.AddFloatingObject();
                break;
            case MyVoxelMap _:
                Census.AddAsteroid();
                break;
            case MyPlanet _:
                Census.AddPlanet();
                break;
            case MyVoxelBase _:
                Census.AddVoxel();
                break;
        }
    }

    public void Finish()
    {
        MySession session = MySession.Static;
        SerializableDictionary<ulong, short>? pings = ReadPings();

        foreach (MyPlayer player in session.Players.GetOnlinePlayers())
        {
            if (!player.IsRealPlayer || player.Identity == null)
                continue;

            long identity = player.Identity.IdentityId;
            ulong peer = player.Id.SteamId;
            int ping = pings != null && pings.Dictionary.TryGetValue(peer, out short value) ? value : -1;

            Census.AddPlayer(identity, FactionOf(session, identity), ping, peer, QueuedFor(peer));
        }

        int factions = 0;
        foreach (var faction in session.Factions)
        {
            factions++;

            foreach (var member in faction.Value.Members)
                Census.NoteMember(member.Key, faction.Key);
        }

        Census.SetFactionCount(factions);
        Census.SetMemory(Environment.WorkingSet, GC.GetTotalMemory(false));
        ReadCpu();
    }

    // One figure going missing is no reason to lose the rest of the census.
    private static void ReadCpu()
    {
        if (s_noCpu)
            return;

        try
        {
            s_process ??= Process.GetCurrentProcess();
            Census.SetCpuTime(s_process.TotalProcessorTime.Ticks);
        }
        catch (Exception exception)
        {
            s_noCpu = true;
            MonitorLog.Warning("Monitor can't read the server's CPU use here, so that figure stays at zero: " + exception.Message);
        }
    }

    // Steam's count of what's waiting for this player, which nothing in the game can see. EOS doesn't keep one, so it reads zero there.
    private static int QueuedFor(ulong peer)
    {
        IMyPeer2Peer? network = MyGameService.Peer2Peer;
        var state = default(MyP2PSessionState);

        return network != null && network.GetSessionState(peer, ref state) ? state.BytesQueuedForSend : 0;
    }

    private static SerializableDictionary<ulong, short>? ReadPings()
    {
        if (MyMultiplayer.Static == null || !(MyMultiplayer.ReplicationLayer is MyReplicationServer server))
            return null;

        server.GetClientPings(out SerializableDictionary<ulong, short> pings);
        return pings;
    }

    private static long OwnerOfGrid(MyCubeGrid grid)
    {
        var owners = grid.BigOwners;
        return owners.Count > 0 ? owners[0] : 0L;
    }

    private static long FactionOfGrid(MyCubeGrid grid)
    {
        long owner = OwnerOfGrid(grid);
        return owner != 0L ? FactionOf(MySession.Static, owner) : Census.NoFaction;
    }

    private static long FactionOf(MySession session, long identity) =>
        session.Factions.TryGetPlayerFaction(identity)?.FactionId ?? Census.NoFaction;
}
