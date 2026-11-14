using System.Runtime.CompilerServices;

namespace SenX.Monitor.Core;

/// <summary>
/// The always-on tree's live counters. Written only on the game thread, so there is no TLS and
/// no Interlocked, just an indexed add. Readers work from per-frame committed copies.
/// </summary>
public static unsafe class TreeCounters
{
    public static long* RawTicks;
    public static long* Calls;

    internal static void Initialize()
    {
        if (RawTicks != null)
            return;

        Calls = Native.Allocate<long>(Gates.Capacity);
        RawTicks = Native.Allocate<long>(Gates.Capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(int scope, long rawTicks)
    {
        RawTicks[scope] += rawTicks;
        Calls[scope]++;
    }
}
