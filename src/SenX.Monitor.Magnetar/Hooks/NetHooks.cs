using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using SenX.Monitor.Core;
using VRage;
using VRage.GameServices;
using VRage.Library.Collections;
using VRage.Network;
using CallSite = VRage.Network.CallSite;

namespace SenX.Monitor.Magnetar.Hooks;

/// <summary>
/// Stands in for the two calls every packet goes through, in <c>MyNetworkWriter.SendAll</c> and
/// <c>MyReceiveQueue.ReceiveOne</c>. These are the last and first places the game has a packet, one
/// recipient at a time. They run on the network thread, and they aren't gated, because a packet costs
/// a table lookup and can't slow a frame.
/// </summary>
public static class NetHooks
{
    // Laid out by MyNetworkWriter.SendAll: magic, flag, checksum, part number, parts, then the header.
    private const int PartAt = 6;
    private const int MessageIdAt = 8;

    private static NetKind s_lastKind;

    public static bool SendPacket(IMyPeer2Peer network, ulong remoteUser, byte[] data, int byteCount, MyP2PMessageEnum msgType, int channel)
    {
        bool accepted = network.SendPacket(remoteUser, data, byteCount, msgType, channel);
        NetTraffic.Sent(remoteUser, byteCount, KindOf(data, byteCount), accepted);
        return accepted;
    }

    public static bool ReadPacket(IMyPeer2Peer network, byte[] buffer, ref uint dataSize, out ulong remoteUser, int channel)
    {
        bool read = network.ReadPacket(buffer, ref dataSize, out remoteUser, channel);

        if (read)
            NetTraffic.Received(remoteUser, dataSize);

        return read;
    }

    /// <summary>
    /// What a packet is for. Only the first part of a split message has a header, and parts go out in
    /// order on this thread, so a later part is what the first one was.
    /// </summary>
    internal static NetKind KindOf(byte[] data, int byteCount)
    {
        if (byteCount > MessageIdAt && data[PartAt] == 0)
            s_lastKind = KindOf((MyMessageId)data[MessageIdAt]);

        return s_lastKind;
    }

    private static NetKind KindOf(MyMessageId message)
    {
        switch (message)
        {
            case MyMessageId.SERVER_STATE_SYNC:
                return NetKind.StateSync;
            case MyMessageId.RPC:
                return NetKind.Events;
            case MyMessageId.REPLICATION_CREATE:
            case MyMessageId.REPLICATION_DESTROY:
            case MyMessageId.REPLICATION_STREAM_BEGIN:
            case MyMessageId.REPLICATION_ISLAND_DONE:
                return NetKind.Replication;
            default:
                return NetKind.Other;
        }
    }
}

/// <summary>
/// Stands in for <c>MyReplicationLayer.Invoke</c> inside the server's <c>OnEvent</c>. That is where a
/// client's RPC runs, one call for every RPC, on the game thread. The call site says which method it
/// runs, so one name covers an RPC on every grid and block.
/// </summary>
public static class RpcHook
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Invoke(MyReplicationLayer layer, CallSite site, BitStream stream, object obj, EndpointId source, MyClientStateBase client, bool validate)
    {
        NetTraffic.NoteRpc(source.Value);

        byte gate = Gates.Read(Site.Rpc);

        return gate == Gates.Closed
            ? layer.Invoke(site, stream, obj, source, client, validate)
            : InvokeOpen(layer, site, stream, obj, source, client, validate, gate);
    }

    /// <summary>
    /// Runs just before an event goes out, inside <c>MyReplicationServer.DispatchEvent</c>, which is the
    /// last place that knows both which RPC it is and who it's for. <paramref name="recipients"/> is null
    /// when it's going to one client. It can run on any thread, because the game can raise events from any.
    /// </summary>
    public static void NoteSent(IPacketData data, CallSite site, List<EndpointId>? recipients)
    {
        MethodInfo method = site.MethodInfo;
        long key = method.MethodHandle.Value.ToInt64();
        long bytes = (long)(data.Size + MyNetworkWriter.PACKET_HEADER_SIZE) * (recipients?.Count ?? 1);

        if (NetTraffic.SentRpc(key, bytes))
            KeyNames.Note(key, method);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool InvokeOpen(MyReplicationLayer layer, CallSite site, BitStream stream, object obj, EndpointId source, MyClientStateBase client, bool validate, int shift)
    {
        if (!Sampler.Hit((long)(source.Value ^ site.Id), Site.Rpc, shift))
            return layer.Invoke(site, stream, obj, source, client, validate);

        long started = Clock.Now();
        bool passed = layer.Invoke(site, stream, obj, source, client, validate);
        KeyedRecord.AddMethod(Site.Rpc, site.MethodInfo, Clock.Now() - started, shift);
        return passed;
    }
}
