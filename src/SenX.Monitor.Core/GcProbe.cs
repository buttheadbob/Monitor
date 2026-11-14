using System;
using System.Reflection;
using System.Runtime;

namespace SenX.Monitor.Core;

/// <summary>
/// Facts from the runtime, not the game, so they pass the trust rule. Monitor only reports GC
/// settings and never changes them.
/// </summary>
public static class GcProbe
{
    private static Func<long>? s_allocatedOnThread;

    /// <summary>Whether this process runs Server GC. Fixed at process start.</summary>
    public static bool IsServerGc => GCSettings.IsServerGC;

    public static GCLatencyMode LatencyMode => GCSettings.LatencyMode;

    /// <summary>True if the runtime can report per-thread allocation.</summary>
    public static bool CanReadThreadAllocation => s_allocatedOnThread != null;

    internal static void Initialize()
    {
        if (s_allocatedOnThread != null)
            return;

        // Not part of netstandard2.0, and its visibility differs by runtime, so bind it if present.
        MethodInfo? method = typeof(GC).GetMethod(
            "GetAllocatedBytesForCurrentThread",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        if (method != null && method.ReturnType == typeof(long) && method.GetParameters().Length == 0)
            s_allocatedOnThread = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
    }

    /// <summary>Bytes allocated by the calling thread so far, or 0 when unavailable.</summary>
    public static long AllocatedOnThread() => s_allocatedOnThread?.Invoke() ?? 0L;
}
