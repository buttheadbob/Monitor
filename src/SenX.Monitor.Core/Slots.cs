using System.Runtime.InteropServices;

namespace SenX.Monitor.Core;

/// <summary>Which part of the update cycle a call belongs to. Fields on a slot, so one entity needs only one slot.</summary>
public static class Phase
{
    public const int Before = 0;
    public const int After = 1;
    public const int Once = 2;
    public const int Simulate = 3;
    public const int Every10 = 4;
    public const int Every100 = 5;
    public const int Other = 6;
    public const int Count = 7;
}

/// <summary>How <see cref="EntitySlot.DefinitionKey"/> holds both a definition and a class in one number.</summary>
public static class EntityKeys
{
    /// <summary>The row for all blocks together, used when listing entities by type.</summary>
    public const long Blocks = 1L;

    private const long TypeMask = 0xFFFFFFFFL;

    public static long Pack(int definition, long typeHandle) => ((long)definition << 32) | (typeHandle & TypeMask);

    /// <summary>The class part of a key. Blocks of one class share this across their definitions.</summary>
    public static long TypeOf(long key) => key & TypeMask;
}

/// <summary>Packs the site into the low bits of a key, so one type gets separate rows for Before and After.</summary>
public static class SiteKeys
{
    private const int SiteBits = 10;

    public static long Pack(long value, int site) => (value << SiteBits) | (long)site;

    public static long ValueOf(long key) => key >> SiteBits;
}

/// <summary>Per-entity timing. Holds ids only, never object references, so nothing keeps an entity alive.</summary>
[StructLayout(LayoutKind.Sequential, Size = 96)]
public unsafe struct EntitySlot
{
    /// <summary>Table key, must stay first. Zero means empty.</summary>
    public long Id;
    public long GridId;
    public long OwnerId;
    public long DefinitionKey;
    public uint Samples;
    public uint MaxUnits;

    /// <summary>Scaled time per <see cref="Phase"/>, in raw clock ticks.</summary>
    public fixed long PhaseTicks[Phase.Count];
}

/// <summary>Timing keyed by something other than an entity: a type id or a delegate code pointer.</summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct KeySlot
{
    /// <summary>Table key, must stay first. Zero means empty.</summary>
    public long Key;
    /// <summary>Scaled time in raw clock ticks.</summary>
    public long ScaledTicks;
    public long Scope;
    public uint Samples;
    public uint MaxUnits;
}

/// <summary>One call slower than the threshold. Answers "what spiked", which averages hide.</summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct SlowEntry
{
    public ulong Frame;
    public long Id;
    public long Units;
    public int Site;
    private readonly int _padding;
}
