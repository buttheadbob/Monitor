using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Hooks;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>
/// Rewrites call sites. Two forms, both safe by construction:
/// <list type="bullet">
/// <item>Bracket: a clock read is stored in a local just before the call, and the time recorded just
/// after it. Both leave the evaluation stack as they found it, so arguments and return values are
/// never touched. Labels and exception blocks move to the first new instruction, so a branch to the
/// call can't skip the clock read.</item>
/// <item>Substitute: the call becomes a call to a hook with the same stack shape, one for one.</item>
/// <item>Tally: a hook runs just before the call, which stays as it was. What's added takes off the stack
/// what it put on, so the call's own arguments below it are never touched.</item>
/// </list>
/// A site that doesn't fit is left alone. The method still runs exactly as the game wrote it.
/// </summary>
internal static class CallSiteRewriter
{
    private static readonly MethodInfo ClockNow = AccessTools.Method(typeof(Clock), nameof(Clock.Now));
    private static readonly MethodInfo TreeStop = AccessTools.Method(typeof(TreeHooks), nameof(TreeHooks.Stop));
    private static readonly MethodInfo FrameEnd = AccessTools.Method(typeof(FrameEngine), nameof(FrameEngine.OnFrameEnd));

    public static bool Matches(CodeInstruction instruction, CallTarget target) =>
        CallMatch.Matches(instruction.opcode, instruction.operand, target);

    public static List<CodeInstruction> Rewrite(
        IEnumerable<CodeInstruction> source,
        IReadOnlyList<CallTarget> targets,
        ILGenerator generator,
        Dictionary<CallTarget, int> rewritten,
        Dictionary<CallTarget, int>? matched = null)
    {
        var output = new List<CodeInstruction>();

        foreach (CodeInstruction instruction in source)
        {
            CallTarget? target = targets.FirstOrDefault(candidate => Matches(instruction, candidate));
            if (target == null)
            {
                output.Add(instruction);
                continue;
            }

            if (matched != null)
            {
                matched.TryGetValue(target, out int seen);
                matched[target] = seen + 1;
            }

            bool done = target.Kind switch
            {
                CallSiteKind.Substitute or CallSiteKind.Counter => Substitute(instruction, target, output),
                CallSiteKind.Tally => Tally(instruction, target, output),
                _ => Bracket(instruction, target, generator, output),
            };

            if (done)
            {
                rewritten.TryGetValue(target, out int count);
                rewritten[target] = count + 1;
            }
        }

        return output;
    }

    private static bool Bracket(CodeInstruction call, CallTarget target, ILGenerator generator, List<CodeInstruction> output)
    {
        LocalBuilder started = generator.DeclareLocal(typeof(long));

        // A prefix such as constrained. must stay attached to its call, so new code goes before it.
        int insertAt = output.Count;
        if (insertAt > 0 && output[insertAt - 1].opcode.OpCodeType == OpCodeType.Prefix)
            insertAt--;

        CodeInstruction displaced = insertAt < output.Count ? output[insertAt] : call;

        var read = new CodeInstruction(OpCodes.Call, ClockNow);
        read.MoveLabelsFrom(displaced);
        read.MoveBlocksFrom(displaced);

        output.Insert(insertAt, read);
        output.Insert(insertAt + 1, new CodeInstruction(OpCodes.Stloc, started));
        output.Add(call);
        output.Add(new CodeInstruction(OpCodes.Ldloc, started));

        if (target.Kind == CallSiteKind.FrameEnd)
        {
            output.Add(new CodeInstruction(OpCodes.Call, FrameEnd));
        }
        else
        {
            output.Add(new CodeInstruction(OpCodes.Ldc_I4, target.Scope));
            output.Add(new CodeInstruction(OpCodes.Call, TreeStop));
        }

        return true;
    }

    private static bool Tally(CodeInstruction call, CallTarget target, List<CodeInstruction> output)
    {
        if (target.Hook == null || target.Loads == null)
        {
            output.Add(call);
            return false;
        }

        int insertAt = output.Count;

        // Prefixes must stay attached to their call, so new code goes before them.
        while (insertAt > 0 && output[insertAt - 1].opcode.OpCodeType == OpCodeType.Prefix)
            insertAt--;

        CodeInstruction displaced = insertAt < output.Count ? output[insertAt] : call;

        List<CodeInstruction> added = target.Loads.Select(step => new CodeInstruction(step.OpCode, step.Operand)).ToList();
        added.Add(new CodeInstruction(OpCodes.Call, target.Hook));
        added[0].MoveLabelsFrom(displaced);
        added[0].MoveBlocksFrom(displaced);

        output.InsertRange(insertAt, added);
        output.Add(call);
        return true;
    }

    private static bool Substitute(CodeInstruction call, CallTarget target, List<CodeInstruction> output)
    {
        var method = (MethodInfo)call.operand;

        bool hasPrefix = output.Count > 0 && output[output.Count - 1].opcode.OpCodeType == OpCodeType.Prefix;
        bool isBaseCall = call.opcode == OpCodes.Call && method.IsVirtual;

        if (hasPrefix || isBaseCall || target.Hook == null || !Fits(target.Hook, method))
        {
            output.Add(call);
            return false;
        }

        call.opcode = OpCodes.Call;
        call.operand = target.Hook;
        output.Add(call);
        return true;
    }

    public static bool Fits(MethodInfo hook, MethodInfo call) => CallMatch.Fits(hook, call);
}
