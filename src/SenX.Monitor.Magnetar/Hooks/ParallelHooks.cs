using System.Runtime.CompilerServices;
using Sandbox.Game.Entities;
using SenX.Monitor.Core;
using VRage.Game.Entity;

namespace SenX.Monitor.Magnetar.Hooks;

public interface IParallelCall
{
    int Site { get; }

    int Phase { get; }

    void Invoke(IMyParallelUpdateable entity);
}

public readonly struct ParallelBeforeCall : IParallelCall
{
    public int Site => Core.Site.ParallelBefore;

    public int Phase => Core.Phase.Before;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(IMyParallelUpdateable entity) => entity.UpdateBeforeSimulationParallel();
}

public readonly struct ParallelAfterCall : IParallelCall
{
    public int Site => Core.Site.ParallelAfter;

    public int Phase => Core.Phase.After;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(IMyParallelUpdateable entity) => entity.UpdateAfterSimulationParallel();
}

/// <summary>
/// Parallel entity updates, which run on worker threads. Grids run their parallel queues here, and
/// the old Profiler never saw the Before side of this. Not every receiver is an entity.
/// </summary>
public static unsafe class ParallelHooks
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Run<T>(IMyParallelUpdateable entity) where T : struct, IParallelCall
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
    private static void RunOpen<T>(IMyParallelUpdateable entity, int shift) where T : struct, IParallelCall
    {
        var known = entity as MyEntity;
        long id = known != null ? known.EntityId : RuntimeHelpers.GetHashCode(entity);

        if (!Sampler.Hit(id, default(T).Site, shift))
        {
            default(T).Invoke(entity);
            return;
        }

        long started = Clock.Now();
        default(T).Invoke(entity);
        long elapsed = Clock.Now() - started;

        if (known == null)
        {
            KeyedRecord.AddType(default(T).Site, entity.GetType(), elapsed, shift);
            return;
        }

        EntitySlot* created = Recorder.AddEntity(default(T).Site, default(T).Phase, id, elapsed, shift);
        if (created != null)
            SlotMeta.Fill(created, known);
    }
}
