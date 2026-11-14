using System.Runtime.CompilerServices;
using SenX.Monitor.Core;

namespace SenX.Monitor.Magnetar.Hooks;

/// <summary>The exit of a bracketed call. Tree sites all run on the game thread, so this is a plain indexed add.</summary>
public static class TreeHooks
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Stop(long started, int scope) => TreeCounters.Add(scope, Clock.Now() - started);
}
