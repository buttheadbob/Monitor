using System;
using System.Runtime.CompilerServices;
using Sandbox.Game.Entities;
using SenX.Monitor.Core;

namespace SenX.Monitor.Magnetar.Hooks;

/// <summary>
/// Stands in for <c>MyCubeGrid.Invoke</c> inside <c>Dispatch</c>. That is where grid systems and
/// game logic run, and neither older profiler could see which callback took the time. Patching
/// <c>Invoke</c> itself would not work: it is marked AggressiveInlining, so <c>Dispatch</c> is rewritten.
/// </summary>
public static class GridCallbackHook
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Invoke(MyCubeGrid grid, in MyCubeGrid.Update update, MyCubeGrid.UpdateQueue queue)
    {
        byte gate = Gates.Read(Site.GridCallback);
        if (gate == Gates.Closed)
        {
            update.Callback();
            return;
        }

        InvokeOpen(grid, update.Callback, gate);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InvokeOpen(MyCubeGrid grid, Action callback, int shift)
    {
        if (!Sampler.Hit(grid.EntityId, Site.GridCallback, shift))
        {
            callback();
            return;
        }

        long started = Clock.Now();
        callback();
        KeyedRecord.AddDelegate(Site.GridCallback, callback, Clock.Now() - started, shift);
    }
}

/// <summary>Stands in for the callback call in <c>ProcessInvokeLater</c>. Deferred work nobody has measured before.</summary>
public static class InvokeLaterHook
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Invoke(Action callback)
    {
        byte gate = Gates.Read(Site.InvokeLater);
        if (gate == Gates.Closed)
        {
            callback();
            return;
        }

        InvokeOpen(callback, gate);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InvokeOpen(Action callback, int shift)
    {
        if (!Sampler.Hit(RuntimeHelpers.GetHashCode(callback), Site.InvokeLater, shift))
        {
            callback();
            return;
        }

        long started = Clock.Now();
        callback();
        KeyedRecord.AddDelegate(Site.InvokeLater, callback, Clock.Now() - started, shift);
    }
}
