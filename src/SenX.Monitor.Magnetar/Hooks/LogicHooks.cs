using System.Runtime.CompilerServices;
using Sandbox.Game.Entities;
using SenX.Monitor.Core;
using VRage.Game.Entity.EntityComponents.Interfaces;

namespace SenX.Monitor.Magnetar.Hooks;

public interface ISiteId
{
    int Id { get; }
}

/// <summary>Components updated by MyGameLogic's own lists.</summary>
public readonly struct GameLogicSite : ISiteId
{
    public int Id => Site.GameLogic;
}

/// <summary>Components updated by their entity, which is how most mod logic runs.</summary>
public readonly struct CompositeLogicSite : ISiteId
{
    public int Id => Site.CompositeLogic;
}

public readonly struct EntityLogicSite : ISiteId
{
    public int Id => Site.EntityLogic;
}

public interface ILogicCall
{
    void Invoke(IMyGameLogicComponent component, bool entityUpdate);
}

public readonly struct LogicBefore : ILogicCall
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(IMyGameLogicComponent component, bool entityUpdate) => component.UpdateBeforeSimulation(entityUpdate);
}

public readonly struct LogicBefore10 : ILogicCall
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(IMyGameLogicComponent component, bool entityUpdate) => component.UpdateBeforeSimulation10(entityUpdate);
}

public readonly struct LogicBefore100 : ILogicCall
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(IMyGameLogicComponent component, bool entityUpdate) => component.UpdateBeforeSimulation100(entityUpdate);
}

public readonly struct LogicAfter : ILogicCall
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(IMyGameLogicComponent component, bool entityUpdate) => component.UpdateAfterSimulation(entityUpdate);
}

public readonly struct LogicAfter10 : ILogicCall
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(IMyGameLogicComponent component, bool entityUpdate) => component.UpdateAfterSimulation10(entityUpdate);
}

public readonly struct LogicAfter100 : ILogicCall
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(IMyGameLogicComponent component, bool entityUpdate) => component.UpdateAfterSimulation100(entityUpdate);
}

public readonly struct LogicOnce : ILogicCall
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(IMyGameLogicComponent component, bool entityUpdate) => component.UpdateOnceBeforeFrame(entityUpdate);
}

/// <summary>Game logic components, keyed by type, so time can be traced to the mod or plugin that owns it.</summary>
public static class LogicHooks
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Run<TCall, TSite>(IMyGameLogicComponent component, bool entityUpdate)
        where TCall : struct, ILogicCall
        where TSite : struct, ISiteId
    {
        byte gate = Gates.Read(default(TSite).Id);
        if (gate == Gates.Closed)
        {
            default(TCall).Invoke(component, entityUpdate);
            return;
        }

        RunOpen<TCall, TSite>(component, entityUpdate, gate);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunOpen<TCall, TSite>(IMyGameLogicComponent component, bool entityUpdate, int shift)
        where TCall : struct, ILogicCall
        where TSite : struct, ISiteId
    {
        // Its parts are timed one by one, so timing the wrapper too would count them twice.
        bool wrapper = Gates.Read(Site.CompositeLogic) != Gates.Closed && component is MyCompositeGameLogicComponent;

        if (wrapper || !Sampler.Hit(RuntimeHelpers.GetHashCode(component), default(TSite).Id, shift))
        {
            default(TCall).Invoke(component, entityUpdate);
            return;
        }

        long started = Clock.Now();
        default(TCall).Invoke(component, entityUpdate);
        KeyedRecord.AddType(default(TSite).Id, component.GetType(), Clock.Now() - started, shift);
    }
}
