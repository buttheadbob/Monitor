using System.Runtime.CompilerServices;
using SenX.Monitor.Core;
using VRage.Game.Entity;

namespace SenX.Monitor.Magnetar.Hooks;

/// <summary>One entity update call. A struct per call means the JIT folds the constants and devirtualizes.</summary>
public interface IEntityCall
{
    int Site { get; }

    int Phase { get; }

    void Invoke(MyEntity entity);
}

public readonly struct BeforeCall : IEntityCall
{
    public int Site => Core.Site.EntityBefore;

    public int Phase => Core.Phase.Before;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(MyEntity entity) => entity.UpdateBeforeSimulation();
}

public readonly struct Before10Call : IEntityCall
{
    public int Site => Core.Site.EntityBefore10;

    public int Phase => Core.Phase.Every10;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(MyEntity entity) => entity.UpdateBeforeSimulation10();
}

public readonly struct Before100Call : IEntityCall
{
    public int Site => Core.Site.EntityBefore100;

    public int Phase => Core.Phase.Every100;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(MyEntity entity) => entity.UpdateBeforeSimulation100();
}

public readonly struct AfterCall : IEntityCall
{
    public int Site => Core.Site.EntityAfter;

    public int Phase => Core.Phase.After;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(MyEntity entity) => entity.UpdateAfterSimulation();
}

public readonly struct After10Call : IEntityCall
{
    public int Site => Core.Site.EntityAfter10;

    public int Phase => Core.Phase.Every10;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(MyEntity entity) => entity.UpdateAfterSimulation10();
}

public readonly struct After100Call : IEntityCall
{
    public int Site => Core.Site.EntityAfter100;

    public int Phase => Core.Phase.Every100;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(MyEntity entity) => entity.UpdateAfterSimulation100();
}

public readonly struct SimulateCall : IEntityCall
{
    public int Site => Core.Site.EntitySimulate;

    public int Phase => Core.Phase.Simulate;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(MyEntity entity) => entity.Simulate();
}

public readonly struct OnceCall : IEntityCall
{
    public int Site => Core.Site.EntityOnce;

    public int Phase => Core.Phase.Once;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(MyEntity entity) => entity.UpdateOnceBeforeFrame();
}

/// <summary>
/// Stands in for an entity update inside the orchestrator's loops. Closed, it costs a byte read
/// and a branch. The open path stays out of line, so the closed path stays small.
/// </summary>
public static unsafe class EntityHooks
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Run<T>(MyEntity entity) where T : struct, IEntityCall
    {
        byte gate = Gates.Ptr[default(T).Site];
        if (gate == Gates.Closed)
        {
            default(T).Invoke(entity);
            return;
        }

        RunOpen<T>(entity, gate);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunOpen<T>(MyEntity entity, int shift) where T : struct, IEntityCall
    {
        long id = entity.EntityId;
        if (!Sampler.Hit(id, default(T).Site, shift))
        {
            default(T).Invoke(entity);
            return;
        }

        long started = Clock.Now();
        default(T).Invoke(entity);
        long elapsed = Clock.Now() - started;

        EntitySlot* created = Recorder.AddEntity(default(T).Site, default(T).Phase, id, elapsed, shift);
        if (created != null)
            SlotMeta.Fill(created, entity);
    }
}
