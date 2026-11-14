using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using HarmonyLib;
using SenX.Monitor.Core;

namespace SenX.Monitor.Benchmarks;

// A loop over virtual calls, the same shape as the game's entity update loops. Each style patches
// its own copy, so one style's patches can't affect another's numbers.

public abstract class Entity
{
    public long Id;
    public int Value;

    public abstract void Tick();
}

public sealed class PlainEntity : Entity
{
    public override void Tick() => Value++;
}

public sealed class EntryPatchedEntity : Entity
{
    public override void Tick() => Value++;
}

public sealed class LookupPatchedEntity : Entity
{
    public override void Tick() => Value++;
}

public static class Loops
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Unpatched(Entity[] entities)
    {
        for (int i = 0; i < entities.Length; i++)
            entities[i].Tick();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void PatchedNoChange(Entity[] entities)
    {
        for (int i = 0; i < entities.Length; i++)
            entities[i].Tick();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Tree(Entity[] entities)
    {
        for (int i = 0; i < entities.Length; i++)
            entities[i].Tick();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Closed(Entity[] entities)
    {
        for (int i = 0; i < entities.Length; i++)
            entities[i].Tick();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Sparse(Entity[] entities)
    {
        for (int i = 0; i < entities.Length; i++)
            entities[i].Tick();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Sampled(Entity[] entities)
    {
        for (int i = 0; i < entities.Length; i++)
            entities[i].Tick();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Full(Entity[] entities)
    {
        for (int i = 0; i < entities.Length; i++)
            entities[i].Tick();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void FullChained(Entity[] entities)
    {
        for (int i = 0; i < entities.Length; i++)
            entities[i].Tick();
    }
}

/// <summary>The planned hook shape, with one struct per site so the JIT can fold the constant.</summary>
public interface ITickCall
{
    int Site { get; }

    void Invoke(Entity entity);
}

public struct ClosedCall : ITickCall
{
    public readonly int Site => 31;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Invoke(Entity entity) => entity.Tick();
}

public struct SparseCall : ITickCall
{
    public readonly int Site => 32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Invoke(Entity entity) => entity.Tick();
}

public struct SampledCall : ITickCall
{
    public readonly int Site => 33;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Invoke(Entity entity) => entity.Tick();
}

public struct FullCall : ITickCall
{
    public readonly int Site => 34;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Invoke(Entity entity) => entity.Tick();
}

public struct TreeCall : ITickCall
{
    public readonly int Site => 35;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Invoke(Entity entity) => entity.Tick();
}

public static unsafe class Hooks
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Run<T>(Entity entity) where T : struct, ITickCall
    {
        byte gate = Gates.Read(default(T).Site);
        if (gate == Gates.Closed)
        {
            default(T).Invoke(entity);
            return;
        }

        RunOpen<T>(entity, gate);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunOpen<T>(Entity entity, int shift) where T : struct, ITickCall
    {
        long id = entity.Id;
        if (!Sampler.Hit(id, default(T).Site, shift))
        {
            default(T).Invoke(entity);
            return;
        }

        long started = Clock.Now();
        default(T).Invoke(entity);
        Recorder.AddEntity(default(T).Site, Phase.Before, id, Clock.Now() - started, shift);
    }

    /// <summary>An always-on tree scope: two clock reads and an indexed add, nothing else.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunTree<T>(Entity entity) where T : struct, ITickCall
    {
        long started = Clock.Now();
        default(T).Invoke(entity);
        TreeCounters.Add(default(T).Site, Clock.Now() - started);
    }

    /// <summary>The chained idea: the end of one call starts the next, which halves clock reads.</summary>
    public static long ChainLast;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunChained<T>(Entity entity) where T : struct, ITickCall
    {
        long id = entity.Id;
        default(T).Invoke(entity);

        long now = Clock.Now();
        Recorder.AddEntity(default(T).Site, Phase.Before, id, now - ChainLast, 0);
        ChainLast = now;
    }
}

/// <summary>Method-entry patches: the Profiler's style, and the same with Quasar's per-call lookup.</summary>
public static class EntryPatches
{
    public static readonly ConcurrentDictionary<MethodBase, int> Ids = new();

    public static void StaticPrefix(out long __state) => __state = Clock.Now();

    public static void StaticPostfix(long __state) => TreeCounters.Add(40, Clock.Now() - __state);

    public static void LookupPrefix(out long __state) => __state = Clock.Now();

    public static void LookupPostfix(long __state, MethodBase __originalMethod)
    {
        int id = Ids.TryGetValue(__originalMethod, out int value) ? value : 0;
        TreeCounters.Add(id, Clock.Now() - __state);
    }
}

/// <summary>Swaps the virtual call for the hook, one instruction for one, as the real rewriter will.</summary>
public static class Rewriter
{
    private static readonly MethodInfo Tick = AccessTools.Method(typeof(Entity), nameof(Entity.Tick));

    public static MethodInfo? Replacement;

    public static IEnumerable<CodeInstruction> NoChange(IEnumerable<CodeInstruction> instructions) => instructions;

    public static IEnumerable<CodeInstruction> Substitute(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Callvirt && Equals(instruction.operand, Tick))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = Replacement;
            }

            yield return instruction;
        }
    }
}

/// <summary>
/// The real comparison: every style goes through actual Harmony output, because the plan's
/// numbers mean nothing if inlining into a DynamicMethod behaves differently.
/// </summary>
public class PatchStyleBenchmarks
{
    private const int Count = 1024;
    private const string HarmonyId = "senx.monitor.bench";

    private Entity[] _plain = Array.Empty<Entity>();
    private Entity[] _entry = Array.Empty<Entity>();
    private Entity[] _lookup = Array.Empty<Entity>();

    private static TEntity[] Create<TEntity>(Func<TEntity> factory, Random random) where TEntity : Entity
    {
        var entities = new TEntity[Count];
        var buffer = new byte[8];

        for (int i = 0; i < Count; i++)
        {
            random.NextBytes(buffer);
            entities[i] = factory();
            entities[i].Id = BitConverter.ToInt64(buffer, 0) | 1L;
        }

        return entities;
    }

    private static void Substitute(Harmony harmony, string loop, string hook, Type call)
    {
        MethodInfo original = AccessTools.Method(typeof(Loops), loop);
        Rewriter.Replacement = AccessTools.Method(typeof(Hooks), hook).MakeGenericMethod(call);
        harmony.Patch(original, transpiler: new HarmonyMethod(typeof(Rewriter), nameof(Rewriter.Substitute)));
    }

    [GlobalSetup]
    public unsafe void Setup()
    {
        MonitorCore.Initialize();
        ThreadBlocks.Initialize(DetailLayout.Default(32));
        ThreadBlocks.BindMainThread();

        var random = new Random(777);
        _plain = Create(() => new PlainEntity(), random);
        _entry = Create(() => new EntryPatchedEntity(), random);
        _lookup = Create(() => new LookupPatchedEntity(), random);

        var harmony = new Harmony(HarmonyId);

        harmony.Patch(
            AccessTools.Method(typeof(Loops), nameof(Loops.PatchedNoChange)),
            transpiler: new HarmonyMethod(typeof(Rewriter), nameof(Rewriter.NoChange)));

        MethodInfo entryTick = AccessTools.Method(typeof(EntryPatchedEntity), nameof(Entity.Tick));
        harmony.Patch(
            entryTick,
            prefix: new HarmonyMethod(typeof(EntryPatches), nameof(EntryPatches.StaticPrefix)),
            postfix: new HarmonyMethod(typeof(EntryPatches), nameof(EntryPatches.StaticPostfix)));

        MethodInfo lookupTick = AccessTools.Method(typeof(LookupPatchedEntity), nameof(Entity.Tick));
        EntryPatches.Ids[lookupTick] = 41;
        harmony.Patch(
            lookupTick,
            prefix: new HarmonyMethod(typeof(EntryPatches), nameof(EntryPatches.LookupPrefix)),
            postfix: new HarmonyMethod(typeof(EntryPatches), nameof(EntryPatches.LookupPostfix)));

        Substitute(harmony, nameof(Loops.Tree), nameof(Hooks.RunTree), typeof(TreeCall));
        Substitute(harmony, nameof(Loops.Closed), nameof(Hooks.Run), typeof(ClosedCall));
        Substitute(harmony, nameof(Loops.Sparse), nameof(Hooks.Run), typeof(SparseCall));
        Substitute(harmony, nameof(Loops.Sampled), nameof(Hooks.Run), typeof(SampledCall));
        Substitute(harmony, nameof(Loops.Full), nameof(Hooks.Run), typeof(FullCall));
        Substitute(harmony, nameof(Loops.FullChained), nameof(Hooks.RunChained), typeof(FullCall));

        Gates.Close(default(ClosedCall).Site);
        Gates.Open(default(SparseCall).Site, 10);
        Gates.Open(default(SampledCall).Site, 3);
        Gates.Open(default(FullCall).Site, 0);

        // A rewrite that silently failed would just measure the baseline, so prove it took.
        Loops.Tree(_plain);
        Loops.Full(_plain);
        if (TreeCounters.Calls[default(TreeCall).Site] != Count || ThreadBlocks.Current()->SiteSamples[default(FullCall).Site] != Count)
            throw new InvalidOperationException("Call-site substitution did not take effect.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        var harmony = new Harmony(HarmonyId);

        foreach (MethodBase method in harmony.GetPatchedMethods().ToList())
            harmony.Unpatch(method, HarmonyPatchType.All, HarmonyId);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Count, Description = "Unpatched loop")]
    public void Unpatched()
    {
        Sampler.NextFrame();
        Loops.Unpatched(_plain);
    }

    [Benchmark(OperationsPerInvoke = Count, Description = "Caller rebuilt by Harmony, no change")]
    public void PatchedNoChange()
    {
        Sampler.NextFrame();
        Loops.PatchedNoChange(_plain);
    }

    [Benchmark(OperationsPerInvoke = Count, Description = "Method entry, static scope id")]
    public void MethodEntry()
    {
        Sampler.NextFrame();
        Loops.Unpatched(_entry);
    }

    [Benchmark(OperationsPerInvoke = Count, Description = "Method entry, __originalMethod lookup")]
    public void MethodEntryLookup()
    {
        Sampler.NextFrame();
        Loops.Unpatched(_lookup);
    }

    [Benchmark(OperationsPerInvoke = Count, Description = "Call site, tree scope")]
    public void CallSiteTree()
    {
        Sampler.NextFrame();
        Loops.Tree(_plain);
    }

    [Benchmark(OperationsPerInvoke = Count, Description = "Call site, gate closed")]
    public void CallSiteClosed()
    {
        Sampler.NextFrame();
        Loops.Closed(_plain);
    }

    [Benchmark(OperationsPerInvoke = Count, Description = "Call site, 1 in 1024 timed")]
    public void CallSiteSparse()
    {
        Sampler.NextFrame();
        Loops.Sparse(_plain);
    }

    [Benchmark(OperationsPerInvoke = Count, Description = "Call site, 1 in 8 timed")]
    public void CallSiteSampled()
    {
        Sampler.NextFrame();
        Loops.Sampled(_plain);
    }

    [Benchmark(OperationsPerInvoke = Count, Description = "Call site, every call timed")]
    public void CallSiteFull()
    {
        Sampler.NextFrame();
        Loops.Full(_plain);
    }

    [Benchmark(OperationsPerInvoke = Count, Description = "Call site, every call timed, chained clock")]
    public void CallSiteChained()
    {
        Sampler.NextFrame();
        Hooks.ChainLast = Clock.Now();
        Loops.FullChained(_plain);
    }
}
