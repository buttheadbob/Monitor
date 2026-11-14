using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Hooks;
using SenX.Monitor.Magnetar.Patching;
using VRage;
using VRage.GameServices;
using VRage.Library.Collections;
using VRage.Network;
using Xunit;

namespace SenX.Monitor.Magnetar.Tests;

// Stands in for Steam or EOS. Only what the hooks call is real.
public sealed class FakeNetwork : IMyPeer2Peer
{
    public bool Accept = true;
    public bool HasPacket = true;
    public ulong LastPeer;
    public int LastBytes;
    public MyP2PMessageEnum LastType;
    public int LastChannel;

    public int MTUSize => 0;

    public int NetworkUpdateLatency => 0;

    public string DetailedStats => string.Empty;

    public IEnumerable<(string Name, double Value)> Stats => Array.Empty<(string, double)>();

    public IEnumerable<(string Client, IEnumerable<(string Stat, double Value)> Stats)> ClientStats => Array.Empty<(string, IEnumerable<(string, double)>)>();

    public event Action<ulong> SessionRequest
    {
        add { }
        remove { }
    }

    public event Action<ulong, string> ConnectionFailed
    {
        add { }
        remove { }
    }

    public bool SendPacket(ulong remoteUser, byte[] data, int byteCount, MyP2PMessageEnum msgType, int channel)
    {
        LastPeer = remoteUser;
        LastBytes = byteCount;
        LastType = msgType;
        LastChannel = channel;
        return Accept;
    }

    public bool ReadPacket(byte[] buffer, ref uint dataSize, out ulong remoteUser, int channel)
    {
        remoteUser = HasPacket ? 77UL : 0UL;
        dataSize = HasPacket ? 500U : 0U;
        LastChannel = channel;
        return HasPacket;
    }

    public bool AcceptSession(ulong remotePeerId) => true;

    public bool CloseSession(ulong remotePeerId) => true;

    public bool IsPacketAvailable(out uint msgSize, int channel)
    {
        msgSize = 0U;
        return false;
    }

    public bool GetSessionState(ulong remoteUser, ref MyP2PSessionState state) => false;

    public void SetServer(bool server)
    {
    }

    public void BeginFrameProcessing()
    {
    }

    public void EndFrameProcessing()
    {
    }

    public void SignalServerJoined()
    {
    }
}

public sealed class FakeData : IPacketData
{
    public byte[] Data => Array.Empty<byte>();

    public IntPtr Ptr => IntPtr.Zero;

    public int Size { get; set; }

    public int Offset => 0;

    public void Return()
    {
    }
}

public sealed class FakeSite : CallSite
{
    public FakeSite(MethodInfo method)
        : base(null!, 1U, method, CallSiteFlags.None, ValidationType.None, 0f)
    {
    }

    public override bool Invoke(BitStream stream, object obj, bool validate) => true;
}

// The shapes a game update could turn the caller into. Only the first is safe to load arguments from.
public sealed class EventShapes
{
    public bool Right(IPacketData data, CallSite site, EndpointId target) => true;

    public bool Swapped(CallSite site, IPacketData data) => true;

    public bool TooShort(IPacketData data) => true;

    public static bool Shifted(IPacketData data, CallSite site) => true;
}

public sealed class NetHooksTests
{
    private const ulong Peer = 88UL;
    private const int PartAt = 6;
    private const int MessageIdAt = 8;
    private const int PacketBytes = 32;

    private readonly FakeNetwork _network = new();

    public NetHooksTests() => MonitorCore.Initialize();

    private static byte[] Packet(MyMessageId message, byte part = 0)
    {
        var data = new byte[PacketBytes];
        data[0] = 206;
        data[PartAt] = part;
        data[MessageIdAt] = (byte)message;
        return data;
    }

    private static MethodInfo Network(string name) => AccessTools.Method(typeof(IMyPeer2Peer), name);

    [Theory]
    [InlineData(MyMessageId.SERVER_STATE_SYNC, NetKind.StateSync)]
    [InlineData(MyMessageId.RPC, NetKind.Events)]
    [InlineData(MyMessageId.REPLICATION_CREATE, NetKind.Replication)]
    [InlineData(MyMessageId.REPLICATION_DESTROY, NetKind.Replication)]
    [InlineData(MyMessageId.REPLICATION_STREAM_BEGIN, NetKind.Replication)]
    [InlineData(MyMessageId.REPLICATION_ISLAND_DONE, NetKind.Replication)]
    [InlineData(MyMessageId.WORLD, NetKind.Other)]
    [InlineData(MyMessageId.FLUSH, NetKind.Other)]
    public void A_packet_is_read_for_what_its_for(MyMessageId message, NetKind kind)
    {
        Assert.Equal(kind, NetHooks.KindOf(Packet(message), PacketBytes));
    }

    [Fact]
    public void A_later_part_of_a_split_message_is_what_the_first_was()
    {
        // Only the first part carries the header, so what's at that place in a later part is just data.
        NetHooks.KindOf(Packet(MyMessageId.REPLICATION_CREATE), PacketBytes);

        Assert.Equal(NetKind.Replication, NetHooks.KindOf(Packet(MyMessageId.RPC, part: 1), PacketBytes));
    }

    [Fact]
    public void Sending_passes_everything_through()
    {
        byte[] data = Packet(MyMessageId.RPC);

        Assert.True(NetHooks.SendPacket(_network, Peer, data, 20, MyP2PMessageEnum.Unreliable, 5));

        Assert.Equal(Peer, _network.LastPeer);
        Assert.Equal(20, _network.LastBytes);
        Assert.Equal(MyP2PMessageEnum.Unreliable, _network.LastType);
        Assert.Equal(5, _network.LastChannel);
    }

    [Fact]
    public void A_sent_packet_counts_for_who_it_went_to()
    {
        NetTraffic.TakeDelta(Peer);

        NetHooks.SendPacket(_network, Peer, Packet(MyMessageId.RPC), 20, MyP2PMessageEnum.Reliable, 0);

        Assert.Equal(20L, NetTraffic.TakeDelta(Peer).SentBytes);
    }

    [Fact]
    public void A_refused_packet_says_so_and_isnt_counted_as_sent()
    {
        _network.Accept = false;
        NetTraffic.TakeDelta(Peer);

        Assert.False(NetHooks.SendPacket(_network, Peer, Packet(MyMessageId.RPC), 20, MyP2PMessageEnum.Reliable, 0));

        PeerDelta delta = NetTraffic.TakeDelta(Peer);

        Assert.Equal(0L, delta.SentBytes);
        Assert.Equal(1L, delta.FailedSends);
    }

    [Fact]
    public void Reading_passes_everything_through_and_counts_for_who_sent_it()
    {
        NetTraffic.TakeDelta(77UL);
        uint size = 0U;

        Assert.True(NetHooks.ReadPacket(_network, new byte[8], ref size, out ulong peer, 3));

        Assert.Equal(77UL, peer);
        Assert.Equal(500U, size);
        Assert.Equal(3, _network.LastChannel);
        Assert.Equal(500L, NetTraffic.TakeDelta(77UL).ReceivedBytes);
    }

    [Fact]
    public void Nothing_to_read_counts_for_nothing()
    {
        _network.HasPacket = false;
        uint size = 0U;

        Assert.False(NetHooks.ReadPacket(_network, new byte[8], ref size, out ulong _, 3));
    }

    [Fact]
    public void The_hooks_take_exactly_what_the_game_leaves_on_the_stack()
    {
        Assert.True(CallSiteRewriter.Fits(AccessTools.Method(typeof(NetHooks), nameof(NetHooks.SendPacket)), Network(nameof(IMyPeer2Peer.SendPacket))));
        Assert.True(CallSiteRewriter.Fits(AccessTools.Method(typeof(NetHooks), nameof(NetHooks.ReadPacket)), Network(nameof(IMyPeer2Peer.ReadPacket))));

        MethodInfo invoke = typeof(MyReplicationLayer).GetMethods().Single(method => method.Name == "Invoke" && method.GetParameters().Length == 6);

        Assert.True(CallSiteRewriter.Fits(AccessTools.Method(typeof(RpcHook), nameof(RpcHook.Invoke)), invoke));
    }

    private static MethodInfo Method(string name) => AccessTools.Method(typeof(DrillTarget), name);

    private static RollupRow SentRow(MethodInfo method)
    {
        var table = new RollupTable(1 << 12);
        NetTraffic.TakeSentRpcs(table);

        return table.Top(NetTraffic.RpcSlots).Single(row => row.Key == method.MethodHandle.Value.ToInt64());
    }

    [Fact]
    public void An_event_counts_its_bytes_for_everyone_who_gets_it()
    {
        MethodInfo method = Method(nameof(DrillTarget.Fast));
        NetTraffic.TakeSentRpcs(null);

        var recipients = new List<EndpointId> { new(1UL), new(2UL), new(3UL) };
        RpcHook.NoteSent(new FakeData { Size = 90 }, new FakeSite(method), recipients);

        RollupRow row = SentRow(method);

        Assert.Equal((90L + MyNetworkWriter.PACKET_HEADER_SIZE) * 3L, row.MainUnits);
        Assert.Equal(1L, row.Calls);
    }

    [Fact]
    public void An_event_to_one_client_counts_once()
    {
        MethodInfo method = Method(nameof(DrillTarget.Slow));
        NetTraffic.TakeSentRpcs(null);

        RpcHook.NoteSent(new FakeData { Size = 90 }, new FakeSite(method), null);

        Assert.Equal(90L + MyNetworkWriter.PACKET_HEADER_SIZE, SentRow(method).MainUnits);
    }

    [Fact]
    public void An_event_gets_its_name()
    {
        MethodInfo method = Method(nameof(DrillTarget.Fail));

        RpcHook.NoteSent(new FakeData { Size = 1 }, new FakeSite(method), null);

        Assert.Equal("DrillTarget.Fail", KeyNames.Describe(method.MethodHandle.Value.ToInt64()));
    }

    [Theory]
    [InlineData(nameof(EventShapes.Right), true)]
    [InlineData(nameof(EventShapes.Swapped), false)]
    [InlineData(nameof(EventShapes.TooShort), false)]
    [InlineData(nameof(EventShapes.Shifted), false)]
    public void Arguments_are_only_loaded_from_a_method_shaped_as_expected(string shape, bool safe)
    {
        // The arguments go in by position, so the wrong shape would crash the server rather than fail.
        Assert.Equal(safe, TargetTable.TakesAnEvent(AccessTools.Method(typeof(EventShapes), shape)));
    }

    [Fact]
    public void A_method_that_wasnt_found_isnt_safe_to_load_from()
    {
        Assert.False(TargetTable.TakesAnEvent(null));
    }

    [Fact]
    public void A_method_key_has_a_name_and_an_owner()
    {
        MethodInfo method = AccessTools.Method(typeof(DrillTarget), nameof(DrillTarget.Negative));

        KeyNames.Note(900001L, method);

        Assert.Equal("DrillTarget.Negative", KeyNames.Describe(900001L));
        Assert.Equal(typeof(DrillTarget), KeyNames.TypeOf(900001L));
    }

    [Fact]
    public void An_rpc_that_was_timed_can_be_looked_inside()
    {
        MethodInfo method = AccessTools.Method(typeof(DrillTarget), nameof(DrillTarget.Twice));

        KeyNames.Note(900002L, method);

        Assert.Contains(DrillDown.Roots("drilltarget.twice"), root => root.Name == "DrillTarget.Twice");
    }

    [Fact]
    public void A_sampled_plugin_update_only_times_some_calls()
    {
        // Time is scaled up by the shift, so timing every call would read that many times too high.
        const int Shift = 4;
        const int Calls = 16000;

        object plugin = new FakePlugin();
        Gates.Open(Site.PluginUpdate, Shift);

        try
        {
            int timed = 0;

            for (int i = 0; i < Calls; i++)
            {
                Sampler.NextFrame();
                PluginUpdateShims.Prefix(plugin, out long state);

                if (state != 0L)
                    timed++;
            }

            Assert.InRange(timed, (Calls >> Shift) / 2, (Calls >> Shift) * 2);
        }
        finally
        {
            Gates.CloseAll();
        }
    }

    [Fact]
    public void An_unsampled_plugin_update_is_always_timed()
    {
        object plugin = new FakePlugin();
        Gates.Open(Site.PluginUpdate, 0);

        try
        {
            for (int i = 0; i < 100; i++)
            {
                Sampler.NextFrame();
                PluginUpdateShims.Prefix(plugin, out long state);

                Assert.NotEqual(0L, state);
            }
        }
        finally
        {
            Gates.CloseAll();
        }
    }
}
