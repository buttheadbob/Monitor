using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Patching;
using Xunit;

namespace SenX.Monitor.Magnetar.Tests;

public interface IDrillThing
{
    int Number();
}

public struct DrillThing : IDrillThing
{
    public int Number() => 7;
}

public abstract class DrillAbstract
{
    public abstract void Nothing();
}

public sealed class DrillWidget
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public DrillWidget() => DrillTarget.Fast();
}

// A generic class, so its call on T carries a constrained. prefix.
public static class DrillHolder<T>
    where T : struct, IDrillThing
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Read()
    {
        T thing = default;
        return thing.Number();
    }
}

// Stands in for the plugin or game code being looked inside.
public static class DrillTarget
{
    public static int Fasts;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Slow() => Thread.Sleep(2);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Fast() => Fasts++;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Doubled(int value)
    {
        Slow();
        Fast();
        return value * 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Twice()
    {
        Fast();
        Fast();
        return Fasts;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Branching(int value)
    {
        if (value > 0)
        {
            Fast();
            return "positive";
        }

        return value < 0 ? Negative() : "zero";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Negative() => "negative";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Guarded(bool fail)
    {
        string text = "start";

        try
        {
            try
            {
                if (fail)
                    Fail();

                Fast();
                text += " ran";
            }
            finally
            {
                Fast();
                text += " cleaned";
            }
        }
        catch (InvalidOperationException)
        {
            text += " " + Negative();
        }

        return text;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Fail() => throw new InvalidOperationException("planned");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<int> Later(int value)
    {
        await Task.Yield();
        Fast();
        return value + 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IEnumerable<int> Numbers()
    {
        Fast();
        yield return 1;
        Fast();
        yield return 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Counted()
    {
        var list = new List<int>();
        Fast();
        return list.Count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static DrillWidget Made() => new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int PatchedByAPlugin() => 1;

    public static void PluginPrefix()
    {
    }
}

public sealed class DrillDownTests : IDisposable
{
    private const string PluginId = "some.drilled.plugin";
    private const string FastName = "DrillTarget.Fast";
    private const string SlowName = "DrillTarget.Slow";
    private const int FirstSite = 1;

    private static readonly MethodInfo s_doubled = Target(nameof(DrillTarget.Doubled));
    private static readonly MethodInfo s_twice = Target(nameof(DrillTarget.Twice));
    private static readonly MethodInfo s_fast = Target(nameof(DrillTarget.Fast));
    private static readonly MethodInfo s_record = AccessTools.Method(typeof(DrillShims), nameof(DrillShims.Record));

    private readonly double _bias;
    private readonly double _timedCall;
    private readonly Harmony _plugin = new(PluginId);

    public DrillDownTests()
    {
        MonitorCore.Initialize();
        FrameEngine.Start();

        _bias = Calibration.ReadBiasNanos;
        _timedCall = Calibration.TimedCallNanos;
    }

    public void Dispose()
    {
        DrillDown.RemoveAll();
        SenX.Monitor.Magnetar.Patching.OwnPatches.Remove(_plugin);
        Calibration.Set(_bias, _timedCall);
    }

    private static MethodInfo Target(string name) => AccessTools.Method(typeof(DrillTarget), name);

    private static DrillResultResponse Result() => JsonConvert.DeserializeObject<DrillResultResponse>(DrillDown.ResultJson())!;

    private static bool Patched(MethodBase method) => Harmony.GetPatchInfo(method)?.Owners.Contains(DrillDown.HarmonyId) == true;

    private static void Start(MethodBase method, int seconds = 1)
    {
        DrillStartResponse start = DrillDown.Start(new DrillStartRequest { Id = DrillDown.Remember(method), Seconds = seconds });

        Assert.True(start.Started, start.Message);
    }

    // Bounded, so a fault fails the test and can't hang the run.
    private static DrillResultResponse Finish(Action duringMeasuring)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(30);

        while (Result().State == RunState.Running && DateTime.UtcNow < giveUp)
        {
            DrillDown.Step();
            duringMeasuring();
        }

        DrillResultResponse result = Result();
        Assert.Equal(RunState.Finished, result.State);
        return result;
    }

    private static DrillResultResponse Finish() => Finish(() => { });

    private static List<CodeInstruction> Rewrite(params CodeInstruction[] instructions)
    {
        DrillDown.ClearSites();

        return DrillDown.Transpile(instructions, new DynamicMethod("Body", typeof(void), Type.EmptyTypes).GetILGenerator()).ToList();
    }

    [Fact]
    public void Its_calls_are_listed_costliest_first()
    {
        Start(s_doubled);

        DrillResultResponse result = Finish(() => DrillTarget.Doubled(1));

        Assert.Equal([SlowName, FastName], result.Rows.Select(row => row.Name));
    }

    [Fact]
    public void A_result_says_what_was_looked_at()
    {
        Start(s_doubled);

        DrillResultResponse result = Finish(() => DrillTarget.Doubled(1));

        Assert.Equal("DrillTarget.Doubled", result.Method);
        Assert.Contains("SenX.Monitor.Magnetar.Tests", result.Owner);
        Assert.Equal(1, result.Seconds);
        Assert.True(result.CallsPerFrame > 0d);
        Assert.True(result.MainMs > 0d);
    }

    [Fact]
    public void The_longest_call_is_kept()
    {
        Start(s_doubled);

        DrillResultResponse result = Finish(() => DrillTarget.Doubled(1));

        Assert.True(result.Rows.Single(row => row.Name == SlowName).PeakMs >= 1d, "It sleeps for 2 ms.");
    }

    [Fact]
    public void Calls_to_the_same_thing_share_a_row()
    {
        Start(s_twice);

        DrillResultResponse result = Finish(() => DrillTarget.Twice());

        Assert.Equal(result.CallsPerFrame * 2d, Assert.Single(result.Rows).CallsPerFrame, 6);
    }

    [Fact]
    public void A_row_can_be_looked_inside_in_turn()
    {
        Start(s_doubled);

        DrillResultResponse result = Finish(() => DrillTarget.Doubled(1));

        Assert.NotEmpty(result.Rows.Single(row => row.Name == FastName).Id);
    }

    [Fact]
    public void Starting_from_a_row_works()
    {
        Start(s_doubled);
        string id = Finish(() => DrillTarget.Doubled(1)).Rows.Single(row => row.Name == SlowName).Id;

        Assert.True(DrillDown.Start(new DrillStartRequest { Id = id, Seconds = 1 }).Started);
    }

    [Fact]
    public void A_method_keeps_returning_what_it_did()
    {
        Start(s_doubled);

        Assert.Equal(42, DrillTarget.Doubled(21));
    }

    [Fact]
    public void Every_way_out_of_a_method_is_timed()
    {
        Start(Target(nameof(DrillTarget.Branching)));

        Assert.Equal("positive", DrillTarget.Branching(1));
        Assert.Equal("negative", DrillTarget.Branching(-1));
        Assert.Equal("zero", DrillTarget.Branching(0));

        Assert.Equal(3d, Finish().CallsPerFrame, 6);
    }

    [Fact]
    public void Try_and_finally_still_work()
    {
        Start(Target(nameof(DrillTarget.Guarded)));

        Assert.Equal("start ran cleaned", DrillTarget.Guarded(false));
        Assert.Equal("start cleaned negative", DrillTarget.Guarded(true));
    }

    [Fact]
    public void A_call_that_throws_only_loses_its_own_time()
    {
        Start(Target(nameof(DrillTarget.Guarded)));

        DrillTarget.Guarded(true);

        DrillResultResponse result = Finish();

        Assert.DoesNotContain(result.Rows, row => row.Name == "DrillTarget.Fail");
        Assert.Contains(result.Rows, row => row.Name == FastName);
    }

    [Fact]
    public async Task An_async_method_is_looked_inside_where_its_code_is()
    {
        Start(Target(nameof(DrillTarget.Later)));

        Assert.Equal(2, await DrillTarget.Later(1));

        Assert.Contains(Finish().Rows, row => row.Name == FastName);
    }

    [Fact]
    public void An_iterator_is_looked_inside_where_its_code_is()
    {
        Start(Target(nameof(DrillTarget.Numbers)));

        Assert.Equal([1, 2], DrillTarget.Numbers().ToArray());

        Assert.Contains(Finish().Rows, row => row.Name == FastName);
    }

    [Fact]
    public void A_call_on_a_generic_type_still_works()
    {
        Start(AccessTools.Method(typeof(DrillHolder<DrillThing>), nameof(DrillHolder<DrillThing>.Read)));

        Assert.Equal(7, DrillHolder<DrillThing>.Read());

        DrillRow row = Assert.Single(Finish().Rows);
        Assert.Equal("IDrillThing.Number", row.Name);
        Assert.Empty(row.Id);
    }

    [Fact]
    public void Constructors_count_as_calls()
    {
        Start(Target(nameof(DrillTarget.Made)));

        Assert.NotNull(DrillTarget.Made());

        Assert.Equal("DrillWidget..ctor", Assert.Single(Finish().Rows).Name);
    }

    [Fact]
    public void Framework_properties_and_constructors_are_left_out()
    {
        Start(Target(nameof(DrillTarget.Counted)));

        Assert.Equal(0, DrillTarget.Counted());

        Assert.Equal(FastName, Assert.Single(Finish().Rows).Name);
    }

    [Fact]
    public void Time_on_other_threads_is_kept_apart()
    {
        Start(s_doubled);

        DrillResultResponse result = Finish(() =>
        {
            var worker = new Thread(() => DrillTarget.Doubled(1));
            worker.Start();
            worker.Join();
        });

        DrillRow row = result.Rows.Single(entry => entry.Name == SlowName);

        Assert.Equal(0d, result.MainMs);
        Assert.True(result.WorkerMs > 0d);
        Assert.Equal(0d, row.MainMs);
        Assert.True(row.WorkerMs > 0d);
    }

    [Fact]
    public void Calls_on_other_threads_take_nothing_from_main_thread_time()
    {
        // A call each costs 1 unit of clock overhead, so 20,000 of them would wipe out the 1 ms below.
        Calibration.Set(readBiasNanos: 100d, timedCallNanos: 0d);
        Start(s_twice);

        DrillDown.Record(Clock.Frequency / 1000L, FirstSite);

        var worker = new Thread(() =>
        {
            for (int i = 0; i < 20000; i++)
                DrillDown.Record(0L, FirstSite);
        });

        worker.Start();
        worker.Join();

        Assert.InRange(Assert.Single(Finish().Rows).MainMs, 0.9d, 1.1d);
    }

    [Fact]
    public void Timing_the_calls_inside_isnt_counted_as_the_methods_own_work()
    {
        Calibration.Set(readBiasNanos: 0d, timedCallNanos: 1000000d);
        Start(s_twice);

        DrillDown.Record(Clock.Frequency / 1000L, 0);
        DrillDown.Record(0L, FirstSite);

        DrillResultResponse result = Finish();

        // The method took 1 ms, and all of it was the timing of the one call inside.
        Assert.Equal(0d, result.MainMs, 6);
        Assert.Equal(0d, result.SelfMs, 6);
    }

    [Fact]
    public void Own_work_is_what_the_calls_inside_dont_explain()
    {
        Calibration.Set(readBiasNanos: 0d, timedCallNanos: 0d);
        Start(s_twice);

        DrillDown.Record(Clock.Frequency / 100L, 0);
        DrillDown.Record(Clock.Frequency / 250L, FirstSite);

        DrillResultResponse result = Finish();

        Assert.InRange(result.MainMs, 9.9d, 10.1d);
        Assert.InRange(result.SelfMs, 5.9d, 6.1d);
    }

    [Fact]
    public void Nothing_is_left_behind()
    {
        Start(s_doubled);
        Assert.True(Patched(s_doubled));

        Finish();

        Assert.False(Patched(s_doubled));
        Assert.Equal(42, DrillTarget.Doubled(21));
    }

    [Fact]
    public void Another_plugins_patch_stays_in()
    {
        _plugin.Patch(s_doubled, prefix: new HarmonyMethod(typeof(DrillTarget), nameof(DrillTarget.PluginPrefix)));
        Start(s_doubled);

        Finish();

        Assert.False(Patched(s_doubled));
        Assert.Contains(PluginId, Harmony.GetPatchInfo(s_doubled)!.Owners);
    }

    [Fact]
    public void Shutting_down_part_way_through_leaves_nothing_behind()
    {
        Start(s_doubled, seconds: 60);

        DrillDown.RemoveAll();

        Assert.False(Patched(s_doubled));
    }

    [Fact]
    public void One_at_a_time()
    {
        Start(s_doubled, seconds: 60);

        DrillStartResponse second = DrillDown.Start(new DrillStartRequest { Id = DrillDown.Remember(s_twice), Seconds = 1 });

        Assert.False(second.Started);
        Assert.False(Patched(s_twice));
    }

    [Fact]
    public void Another_can_follow_the_first()
    {
        Start(s_doubled);
        Finish();

        Start(s_twice);

        Assert.True(Patched(s_twice));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(DrillDown.MaxSeconds + 1)]
    public void A_length_outside_the_limits_is_refused(int seconds)
    {
        Assert.False(DrillDown.Start(new DrillStartRequest { Id = DrillDown.Remember(s_doubled), Seconds = seconds }).Started);
    }

    [Fact]
    public void A_method_it_hasnt_listed_is_refused()
    {
        DrillStartResponse start = DrillDown.Start(new DrillStartRequest { Id = "Not.A.Method()", Seconds = 1 });

        Assert.False(start.Started);
        Assert.NotNull(start.Message);
    }

    [Fact]
    public void A_method_with_no_code_is_refused()
    {
        MethodInfo nothing = AccessTools.Method(typeof(DrillAbstract), nameof(DrillAbstract.Nothing));

        Assert.False(DrillDown.Start(new DrillStartRequest { Id = DrillDown.Remember(nothing), Seconds = 1 }).Started);
    }

    [Fact]
    public void It_stops_by_itself_if_timing_costs_too_much()
    {
        Calibration.Set(readBiasNanos: 0d, timedCallNanos: 10000000d);
        Start(s_twice, seconds: 60);

        for (int frame = 0; frame < 40; frame++)
        {
            DrillTarget.Twice();
            FrameEngine.OnFrameEnd(Clock.Now());
        }

        DrillDown.Step();

        Assert.False(Patched(s_twice));
        Assert.True(Result().EndedEarly);
    }

    [Fact]
    public void It_doesnt_judge_the_cost_from_a_few_frames()
    {
        Calibration.Set(readBiasNanos: 0d, timedCallNanos: 10000000d);
        Start(s_twice, seconds: 60);

        DrillTarget.Twice();
        FrameEngine.OnFrameEnd(Clock.Now());
        DrillDown.Step();

        Assert.True(Patched(s_twice));
    }

    [Fact]
    public void A_normal_end_isnt_reported_as_early()
    {
        Start(s_twice);

        Assert.False(Finish(() => DrillTarget.Twice()).EndedEarly);
    }

    [Fact]
    public void Being_told_when_its_done()
    {
        DrillResultResponse? seen = null;

        Assert.True(DrillDown.Start(new DrillStartRequest { Id = DrillDown.Remember(s_doubled), Seconds = 1 }, result => seen = result).Started);

        Finish();

        Assert.Equal("DrillTarget.Doubled", seen?.Method);
    }

    [Fact]
    public void It_says_when_its_still_going()
    {
        Start(s_doubled, seconds: 60);

        Assert.Equal(RunState.Running, Result().State);
    }

    [Fact]
    public void A_method_can_be_found_by_part_of_its_name()
    {
        MethodInfo method = Target(nameof(DrillTarget.PatchedByAPlugin));
        _plugin.Patch(method, prefix: new HarmonyMethod(typeof(DrillTarget), nameof(DrillTarget.PluginPrefix)));

        DrillRoot root = Assert.Single(DrillDown.Roots("drilltarget.patchedbya"));

        Assert.Equal("DrillTarget.PatchedByAPlugin", root.Name);
        Assert.True(DrillDown.Start(new DrillStartRequest { Id = root.Id, Seconds = 1 }).Started);
    }

    [Fact]
    public void A_search_with_no_match_finds_nothing()
    {
        Assert.Empty(DrillDown.Roots("no method is called this"));
    }

    [Fact]
    public void A_tail_call_keeps_its_return_right_after_it()
    {
        List<CodeInstruction> output = Rewrite(new CodeInstruction(OpCodes.Tailcall), new CodeInstruction(OpCodes.Call, s_fast), new CodeInstruction(OpCodes.Ret));

        int at = output.FindIndex(instruction => instruction.opcode == OpCodes.Tailcall);

        Assert.Equal(OpCodes.Call, output[at + 1].opcode);
        Assert.Same(s_fast, output[at + 1].operand);
        Assert.Equal(OpCodes.Ret, output[at + 2].opcode);
        Assert.DoesNotContain(output, instruction => ReferenceEquals(instruction.operand, s_record));
    }

    [Fact]
    public void A_tail_call_with_another_prefix_is_still_a_tail_call()
    {
        List<CodeInstruction> output = Rewrite(
            new CodeInstruction(OpCodes.Tailcall),
            new CodeInstruction(OpCodes.Constrained, typeof(DrillThing)),
            new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(IDrillThing), nameof(IDrillThing.Number))),
            new CodeInstruction(OpCodes.Ret));

        Assert.DoesNotContain(output, instruction => ReferenceEquals(instruction.operand, s_record));
    }

    [Fact]
    public void A_prefix_stays_on_its_call()
    {
        List<CodeInstruction> output = Rewrite(
            new CodeInstruction(OpCodes.Ldloca_S, (byte)0),
            new CodeInstruction(OpCodes.Constrained, typeof(DrillThing)),
            new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(IDrillThing), nameof(IDrillThing.Number))),
            new CodeInstruction(OpCodes.Pop),
            new CodeInstruction(OpCodes.Ret));

        int prefix = output.FindIndex(instruction => instruction.opcode == OpCodes.Constrained);

        Assert.Equal(OpCodes.Callvirt, output[prefix + 1].opcode);
        Assert.Equal(OpCodes.Stloc, output[prefix - 1].opcode);
    }

    [Fact]
    public void A_jump_to_a_call_lands_on_its_timing()
    {
        var call = new CodeInstruction(OpCodes.Call, s_fast);
        call.labels.Add(default);
        call.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));

        List<CodeInstruction> output = Rewrite(call, new CodeInstruction(OpCodes.Ret));

        // The first two are the clock for the whole method, which a jump back mustn't run again.
        Assert.Empty(output[0].labels);
        Assert.Single(output[2].labels);
        Assert.Single(output[2].blocks);
        Assert.Empty(call.labels);
        Assert.Empty(call.blocks);
    }

    [Fact]
    public void A_jump_to_a_return_lands_on_its_timing()
    {
        var done = new CodeInstruction(OpCodes.Ret);
        done.labels.Add(default);

        List<CodeInstruction> output = Rewrite(done);

        Assert.Equal(OpCodes.Ldloc, output[2].opcode);
        Assert.Single(output[2].labels);
        Assert.Empty(done.labels);
    }

    [Fact]
    public void Timing_a_call_leaves_the_stack_as_it_found_it()
    {
        List<CodeInstruction> output = Rewrite(new CodeInstruction(OpCodes.Call, s_fast), new CodeInstruction(OpCodes.Ret));

        Assert.Equal(
            new[] { OpCodes.Call, OpCodes.Stloc, OpCodes.Call, OpCodes.Stloc, OpCodes.Call, OpCodes.Ldloc, OpCodes.Ldc_I4, OpCodes.Call, OpCodes.Ldloc, OpCodes.Ldc_I4, OpCodes.Call, OpCodes.Ret },
            output.Select(instruction => instruction.opcode));
    }

    [Fact]
    public void Past_the_limit_a_call_is_left_alone()
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

        MethodInfo[] many = typeof(Convert).GetMethods(Public)
            .Concat(typeof(Math).GetMethods(Public))
            .Take(DrillDown.MaxSites + 10)
            .ToArray();

        Assert.True(many.Length > DrillDown.MaxSites);

        List<CodeInstruction> input = many.Select(method => new CodeInstruction(OpCodes.Call, method)).ToList();
        input.Add(new CodeInstruction(OpCodes.Ret));

        List<CodeInstruction> output = Rewrite(input.ToArray());

        // One for each site, and one for the method as a whole.
        Assert.Equal(DrillDown.MaxSites + 1, output.Count(instruction => ReferenceEquals(instruction.operand, s_record)));
    }
}
