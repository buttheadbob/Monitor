using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Patching;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SenX.Monitor.Magnetar.Tests;

// Sample code shaped like the game's, patched by real Harmony. Each case targets something that
// went wrong, or could go wrong, in a rewriter.
public class Base
{
    public virtual int Value() => 10;
}

public class Derived : Base
{
    public override int Value() => base.Value() + 1;
}

public struct PlainStruct
{
}

public static class Sample
{
    public static int Calls;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Work() => Calls++;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Add(int a, int b) => a + b;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Throw() => throw new InvalidOperationException("from the game");

    // The same shape as MySession.Update: the branch lands exactly on the call.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BranchOntoCall(bool skip)
    {
        if (!skip)
            Calls += 100;

        Work();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ValueOnStack() => Add(2, 3) * 2;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ArgumentsOnStack(int a) => Add(a + 1, Add(a, 2));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int InsideTry()
    {
        try
        {
            Throw();
            return 1;
        }
        catch (InvalidOperationException)
        {
            return 2;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Unprotected() => Throw();

    // A struct that doesn't override ToString compiles to constrained. callvirt, with no generics.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ConstrainedCall()
    {
        var value = default(PlainStruct);
        return value.ToString()!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Virtual(Base value) => value.Value();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Twice() => Add(1, 1) + Add(2, 2);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Tallied(int a) => Add(a, 1);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Over(int a, int b) => a + b;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Over(int a, string b) => a + b.Length;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int TwoOverloads(int a) => Over(a, 1) + Over(a, "xy");
}

public static class SampleHooks
{
    public static int Runs;

    public static int Add(int a, int b)
    {
        Runs++;
        return Sample.Add(a, b);
    }

    public static int Value(Base target)
    {
        Runs++;
        return target.Value();
    }

    public static string Describe(object target) => target.ToString() ?? string.Empty;

    public static int Tallies;
    public static int LastTallied;

    public static void Tally(int value)
    {
        Tallies++;
        LastTallied = value;
    }

    public static long WrongReturn(int a, int b) => a + b;

    public static int WrongArguments(int a) => a;
}

public unsafe class RewriterTests : IDisposable
{
    private const int TestScope = 500;
    private const string SampleType = "SenX.Monitor.Magnetar.Tests.Sample";

    private static IReadOnlyList<CallTarget> s_targets = Array.Empty<CallTarget>();
    private static readonly Dictionary<CallTarget, int> s_rewritten = new();

    private readonly Harmony _harmony = new("senx.monitor.tests." + Guid.NewGuid().ToString("N"));

    public RewriterTests()
    {
        MonitorCore.Initialize();
        s_rewritten.Clear();
        Sample.Calls = 0;
        SampleHooks.Runs = 0;
        SampleHooks.Tallies = 0;
        SampleHooks.LastTallied = 0;
        TreeCounters.Calls[TestScope] = 0L;
        TreeCounters.RawTicks[TestScope] = 0L;
    }

    public void Dispose() => SenX.Monitor.Magnetar.Patching.OwnPatches.Remove(_harmony);

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator) =>
        CallSiteRewriter.Rewrite(instructions, s_targets, generator, s_rewritten);

    private static CallTarget Bracket(string method, int parameters) =>
        new(SampleType, method, parameters, 1, CallSiteKind.Bracket, TestScope, null);

    private static CallTarget Swap(string type, string method, int parameters, string hook) =>
        new(type, method, parameters, 1, CallSiteKind.Substitute, Site.First, AccessTools.Method(typeof(SampleHooks), hook));

    private static CallTarget Tally(string type, string method, int parameters, OpCode load, string? secondParameter = null, bool hook = true) =>
        new(type, method, parameters, 1, CallSiteKind.Tally, TestScope, hook ? AccessTools.Method(typeof(SampleHooks), nameof(SampleHooks.Tally)) : null, secondParameter, new[] { new IlStep(load) });

    private void Patch(string method, params CallTarget[] targets)
    {
        s_targets = targets;
        _harmony.Patch(
            AccessTools.Method(typeof(Sample), method),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(RewriterTests), nameof(Transpile)), Priority.Last));
    }

    private static long Calls => TreeCounters.Calls[TestScope];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_branch_landing_on_the_call_is_still_timed(bool skip)
    {
        // The old Profiler left the label on the call, so this path skipped its clock read.
        Patch(nameof(Sample.BranchOntoCall), Bracket(nameof(Sample.Work), 0));

        Sample.BranchOntoCall(skip);

        Assert.Equal(1L, Calls);
        Assert.Equal(skip ? 1 : 101, Sample.Calls);
    }

    [Fact]
    public void A_return_value_stays_on_the_stack()
    {
        Patch(nameof(Sample.ValueOnStack), Bracket(nameof(Sample.Add), 2));

        Assert.Equal(10, Sample.ValueOnStack());
        Assert.Equal(1L, Calls);
    }

    [Fact]
    public void Arguments_built_over_several_instructions_survive()
    {
        // The old Profiler assumed one instruction per argument.
        s_targets = new[] { new CallTarget(SampleType, nameof(Sample.Add), 2, 2, CallSiteKind.Bracket, TestScope, null) };
        _harmony.Patch(
            AccessTools.Method(typeof(Sample), nameof(Sample.ArgumentsOnStack)),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(RewriterTests), nameof(Transpile)), Priority.Last));

        Assert.Equal(13, Sample.ArgumentsOnStack(5));
        Assert.Equal(2L, Calls);
    }

    [Fact]
    public void A_call_inside_try_still_reaches_its_catch()
    {
        Patch(nameof(Sample.InsideTry), Bracket(nameof(Sample.Throw), 0));

        Assert.Equal(2, Sample.InsideTry());
    }

    [Fact]
    public void Exceptions_from_a_timed_call_reach_the_caller_untouched()
    {
        Patch(nameof(Sample.Unprotected), Bracket(nameof(Sample.Throw), 0));

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(Sample.Unprotected);
        Assert.Equal("from the game", thrown.Message);
    }

    [Fact]
    public void Every_matching_site_in_a_method_is_rewritten()
    {
        Patch(nameof(Sample.Twice), new CallTarget(SampleType, nameof(Sample.Add), 2, 2, CallSiteKind.Bracket, TestScope, null));

        Assert.Equal(6, Sample.Twice());
        Assert.Equal(2L, Calls);
    }

    [Fact]
    public void A_substituted_call_runs_the_hook_and_keeps_its_result()
    {
        Patch(nameof(Sample.ValueOnStack), Swap(SampleType, nameof(Sample.Add), 2, nameof(SampleHooks.Add)));

        Assert.Equal(10, Sample.ValueOnStack());
        Assert.Equal(1, SampleHooks.Runs);
    }

    [Fact]
    public void A_hook_with_the_wrong_return_type_is_refused_and_the_game_call_stays()
    {
        CallTarget target = Swap(SampleType, nameof(Sample.Add), 2, nameof(SampleHooks.WrongReturn));
        Patch(nameof(Sample.ValueOnStack), target);

        Assert.Equal(10, Sample.ValueOnStack());
        Assert.False(s_rewritten.ContainsKey(target));
    }

    [Fact]
    public void A_hook_with_the_wrong_arguments_is_refused_and_the_game_call_stays()
    {
        CallTarget target = Swap(SampleType, nameof(Sample.Add), 2, nameof(SampleHooks.WrongArguments));
        Patch(nameof(Sample.ValueOnStack), target);

        Assert.Equal(10, Sample.ValueOnStack());
        Assert.False(s_rewritten.ContainsKey(target));
    }

    [Fact]
    public void A_virtual_call_substitutes_and_still_dispatches_to_the_override()
    {
        string baseType = typeof(Base).FullName!;
        Patch(nameof(Sample.Virtual), Swap(baseType, nameof(Base.Value), 0, nameof(SampleHooks.Value)));

        Assert.Equal(11, Sample.Virtual(new Derived()));
        Assert.Equal(1, SampleHooks.Runs);
    }

    [Fact]
    public void A_base_call_is_never_substituted()
    {
        // Swapping base.Value() for a virtual hook would recurse into the override forever.
        s_targets = new[] { Swap(typeof(Base).FullName!, nameof(Base.Value), 0, nameof(SampleHooks.Value)) };
        _harmony.Patch(
            AccessTools.Method(typeof(Derived), nameof(Derived.Value)),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(RewriterTests), nameof(Transpile)), Priority.Last));

        Assert.Equal(11, new Derived().Value());
        Assert.Equal(0, SampleHooks.Runs);
    }

    [Fact]
    public void A_constrained_call_keeps_its_prefix_when_bracketed()
    {
        MethodInfo method = AccessTools.Method(typeof(Sample), nameof(Sample.ConstrainedCall));
        Assert.Contains(PatchProcessor.GetOriginalInstructions(method), instruction => instruction.opcode == OpCodes.Constrained);

        Patch(nameof(Sample.ConstrainedCall), new CallTarget("System.Object", nameof(ToString), 0, 1, CallSiteKind.Bracket, TestScope, null));

        Assert.Equal(typeof(PlainStruct).FullName, Sample.ConstrainedCall());
        Assert.Equal(1L, Calls);
    }

    [Fact]
    public void A_tally_runs_and_leaves_the_call_and_its_result_alone()
    {
        CallTarget target = Tally(SampleType, nameof(Sample.Add), 2, OpCodes.Ldarg_0);
        Patch(nameof(Sample.Tallied), target);

        Assert.Equal(6, Sample.Tallied(5));

        Assert.Equal(1, SampleHooks.Tallies);
        Assert.Equal(5, SampleHooks.LastTallied);
        Assert.Equal(1, s_rewritten[target]);
    }

    [Fact]
    public void A_tally_goes_on_the_overload_it_names()
    {
        CallTarget target = Tally(SampleType, nameof(Sample.Over), 2, OpCodes.Ldarg_0, "String");
        Patch(nameof(Sample.TwoOverloads), target);

        Assert.Equal(7, Sample.TwoOverloads(2));

        Assert.Equal(1, SampleHooks.Tallies);
        Assert.Equal(1, s_rewritten[target]);
    }

    [Fact]
    public void Each_overload_can_have_a_tally_of_its_own()
    {
        CallTarget ints = Tally(SampleType, nameof(Sample.Over), 2, OpCodes.Ldc_I4_1, "Int32");
        CallTarget strings = Tally(SampleType, nameof(Sample.Over), 2, OpCodes.Ldc_I4_2, "String");
        Patch(nameof(Sample.TwoOverloads), ints, strings);

        Assert.Equal(7, Sample.TwoOverloads(2));

        Assert.Equal(2, SampleHooks.Tallies);
        Assert.Equal(2, SampleHooks.LastTallied);
    }

    [Fact]
    public void A_tally_with_no_hook_leaves_the_site_alone()
    {
        CallTarget target = Tally(SampleType, nameof(Sample.Add), 2, OpCodes.Ldarg_0, hook: false);
        Patch(nameof(Sample.Tallied), target);

        Assert.Equal(6, Sample.Tallied(5));

        Assert.Equal(0, SampleHooks.Tallies);
        Assert.False(s_rewritten.ContainsKey(target));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_branch_landing_on_a_tallied_call_is_still_counted(bool skip)
    {
        Patch(nameof(Sample.BranchOntoCall), Tally(SampleType, nameof(Sample.Work), 0, OpCodes.Ldc_I4_3));

        Sample.BranchOntoCall(skip);

        Assert.Equal(1, SampleHooks.Tallies);
        Assert.Equal(skip ? 1 : 101, Sample.Calls);
    }

    [Fact]
    public void A_tally_goes_in_front_of_a_prefix_and_the_call_still_works()
    {
        Patch(nameof(Sample.ConstrainedCall), Tally("System.Object", nameof(ToString), 0, OpCodes.Ldc_I4_3));

        Assert.Equal(typeof(PlainStruct).FullName, Sample.ConstrainedCall());
        Assert.Equal(1, SampleHooks.Tallies);
    }

    [Fact]
    public void A_tally_inside_a_try_stays_inside_it()
    {
        Patch(nameof(Sample.InsideTry), Tally(SampleType, nameof(Sample.Throw), 0, OpCodes.Ldc_I4_3));

        Assert.Equal(2, Sample.InsideTry());
        Assert.Equal(1, SampleHooks.Tallies);
    }

    [Fact]
    public void A_constrained_call_is_never_substituted()
    {
        CallTarget target = new("System.Object", nameof(ToString), 0, 1, CallSiteKind.Substitute, Site.First, AccessTools.Method(typeof(SampleHooks), nameof(SampleHooks.Describe)));
        Patch(nameof(Sample.ConstrainedCall), target);

        Assert.Equal(typeof(PlainStruct).FullName, Sample.ConstrainedCall());
        Assert.False(s_rewritten.ContainsKey(target));
    }

    [Fact]
    public void Another_plugins_transpiler_runs_first_and_is_measured()
    {
        // Monitor runs last, so a call another plugin added is inside what gets timed.
        _harmony.Patch(
            AccessTools.Method(typeof(Sample), nameof(Sample.ValueOnStack)),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(OtherPlugin), nameof(OtherPlugin.AddWorkCall))));

        Patch(nameof(Sample.ValueOnStack), Bracket(nameof(Sample.Add), 2));

        Assert.Equal(10, Sample.ValueOnStack());
        Assert.Equal(1, Sample.Calls);
        Assert.Equal(1L, Calls);
    }

    [Fact]
    public void Fingerprint_ignores_other_plugins_patches()
    {
        MethodInfo method = AccessTools.Method(typeof(Sample), nameof(Sample.ValueOnStack));
        string before = IlFingerprint.Compute(method);

        _harmony.Patch(method, transpiler: new HarmonyMethod(AccessTools.Method(typeof(OtherPlugin), nameof(OtherPlugin.AddWorkCall))));

        Assert.Equal(before, IlFingerprint.Compute(method));
    }

    [Fact]
    public void Fingerprint_changes_when_a_call_target_changes()
    {
        // The template's hash skipped member operands, so these two would have matched.
        var original = new List<CodeInstruction>
        {
            new(OpCodes.Call, AccessTools.Method(typeof(Sample), nameof(Sample.Work))),
            new(OpCodes.Ret),
        };

        var swapped = new List<CodeInstruction>
        {
            new(OpCodes.Call, AccessTools.Method(typeof(Sample), nameof(Sample.Unprotected))),
            new(OpCodes.Ret),
        };

        Assert.NotEqual(IlFingerprint.Compute(original), IlFingerprint.Compute(swapped));
    }

    [Fact]
    public void Fingerprint_is_stable_across_calls()
    {
        MethodInfo method = AccessTools.Method(typeof(Sample), nameof(Sample.InsideTry));

        Assert.Equal(IlFingerprint.Compute(method), IlFingerprint.Compute(method));
    }

    [Fact]
    public void A_missing_method_is_unavailable_and_nothing_throws()
    {
        var spec = new CallerSpec("missing", null, new[] { Bracket(nameof(Sample.Work), 0) });

        SiteResult result = PatchEngine.Evaluate(new[] { spec }).Single();

        Assert.Equal(SiteState.Unavailable, result.State);
    }

    [Fact]
    public void A_missing_target_is_unavailable()
    {
        MethodInfo method = AccessTools.Method(typeof(Sample), nameof(Sample.ValueOnStack));
        var spec = new CallerSpec("sample", method, new[] { Bracket("NoSuchMethod", 0) });

        Assert.Equal(SiteState.Unavailable, PatchEngine.Evaluate(new[] { spec }).Single().State);
    }

    [Fact]
    public void An_unexpected_site_count_is_unavailable()
    {
        MethodInfo method = AccessTools.Method(typeof(Sample), nameof(Sample.Twice));
        var spec = new CallerSpec("sample", method, new[] { Bracket(nameof(Sample.Add), 2) });

        Assert.Equal(SiteState.Unavailable, PatchEngine.Evaluate(new[] { spec }).Single().State);
    }

    [Fact]
    public void An_unknown_method_with_the_right_sites_is_drifted_not_lost()
    {
        MethodInfo method = AccessTools.Method(typeof(Sample), nameof(Sample.ValueOnStack));
        var spec = new CallerSpec("not in the fingerprint table", method, new[] { Bracket(nameof(Sample.Add), 2) });

        SiteResult result = PatchEngine.Evaluate(new[] { spec }).Single();

        Assert.Equal(SiteState.Drifted, result.State);
        Assert.Equal(1, result.Found);
    }
}

public static class OtherPlugin
{
    public static IEnumerable<CodeInstruction> AddWorkCall(IEnumerable<CodeInstruction> instructions)
    {
        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Sample), nameof(Sample.Work)));

        foreach (CodeInstruction instruction in instructions)
            yield return instruction;
    }
}
