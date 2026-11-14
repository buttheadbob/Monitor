using System;
using System.Runtime.InteropServices;

namespace SenX.Monitor.Core;

/// <summary>
/// Native memory for hot buffers. The GC never sees it: no tracing, no LOH, no write barriers.
/// Memory is never freed. It lives until process exit, which rules out use-after-free from
/// straggler threads. Everything is therefore allocated once, at a safe moment.
/// </summary>
public static unsafe class Native
{
    public const int CacheLine = 64;

    /// <summary>Allocates zeroed, cache-line aligned memory. Touches every page, so never call this on a game thread during play.</summary>
    public static byte* AllocateZeroed(long bytes)
    {
        long raw = Marshal.AllocHGlobal(new IntPtr(bytes + CacheLine)).ToInt64();
        byte* aligned = (byte*)((raw + CacheLine - 1) & ~(long)(CacheLine - 1));
        Clear(aligned, bytes);
        return aligned;
    }

    public static T* Allocate<T>(long count) where T : unmanaged =>
        (T*)AllocateZeroed(count * sizeof(T));

    /// <summary>Zeroes memory. Written by hand because netstandard2.0 has no block clear without extra packages.</summary>
    public static void Clear(void* memory, long bytes)
    {
        byte* p = (byte*)memory;
        long words = bytes >> 3;

        for (long i = 0; i < words; i++)
            ((long*)p)[i] = 0L;

        for (long i = words << 3; i < bytes; i++)
            p[i] = 0;
    }
}
