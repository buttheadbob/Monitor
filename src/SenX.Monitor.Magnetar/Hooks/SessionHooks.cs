using System.Runtime.CompilerServices;
using SenX.Monitor.Core;
using VRage.Game.Components;

namespace SenX.Monitor.Magnetar.Hooks;

public interface ISessionCall
{
    int Site { get; }

    void Invoke(MySessionComponentBase component);
}

public readonly struct SessionBeforeCall : ISessionCall
{
    public int Site => Core.Site.SessionBefore;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(MySessionComponentBase component) => component.UpdateBeforeSimulation();
}

public readonly struct SessionSimulateCall : ISessionCall
{
    public int Site => Core.Site.SessionSimulate;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(MySessionComponentBase component) => component.Simulate();
}

public readonly struct SessionAfterCall : ISessionCall
{
    public int Site => Core.Site.SessionAfter;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(MySessionComponentBase component) => component.UpdateAfterSimulation();
}

/// <summary>Session components, game and mod alike. Few calls and high value, so only the group's guard ever samples these.</summary>
public static class SessionHooks
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Run<T>(MySessionComponentBase component) where T : struct, ISessionCall
    {
        byte gate = Gates.Read(default(T).Site);
        if (gate == Gates.Closed)
        {
            default(T).Invoke(component);
            return;
        }

        RunOpen<T>(component, gate);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunOpen<T>(MySessionComponentBase component, int shift) where T : struct, ISessionCall
    {
        if (!Sampler.Hit(RuntimeHelpers.GetHashCode(component), default(T).Site, shift))
        {
            default(T).Invoke(component);
            return;
        }

        long started = Clock.Now();
        default(T).Invoke(component);
        KeyedRecord.AddType(default(T).Site, component.GetType(), Clock.Now() - started, shift);
    }
}
