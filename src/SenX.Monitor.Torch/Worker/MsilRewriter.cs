using System.Reflection;
using System.Reflection.Emit;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Hooks;
using Torch.Managers.PatchManager.MSIL;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>
/// Rewrites call sites in Torch's own instruction model, in the same three ways the Magnetar build does:
/// <list type="bullet">
/// <item>Bracket: a clock read is stored in a local just before the call, and the time recorded just
/// after it. Both leave the evaluation stack as they found it, so arguments and return values are
/// never touched. Labels and exception blocks move to the first new instruction, so a branch to the
/// call can't skip the clock read.</item>
/// <item>Substitute: the call becomes a call to a hook with the same stack shape, one for one.</item>
/// <item>Tally: a hook runs just before the call, which stays as it was.</item>
/// </list>
/// A site that doesn't fit is left alone. The method still runs exactly as the game wrote it.
/// </summary>
internal static class MsilRewriter
{
    private static readonly MethodInfo s_clockNow = typeof(Clock).GetMethod(nameof(Clock.Now))!;
    private static readonly MethodInfo s_treeStop = typeof(TreeHooks).GetMethod(nameof(TreeHooks.Stop))!;
    private static readonly MethodInfo s_frameEnd = typeof(FrameEngine).GetMethod(nameof(FrameEngine.OnFrameEnd))!;

    public static List<MsilInstruction> Rewrite(
        IEnumerable<MsilInstruction> source,
        IReadOnlyList<CallTarget> targets,
        Func<Type, MsilLocal> newLocal,
        Dictionary<CallTarget, int> rewritten,
        Dictionary<CallTarget, int>? matched = null)
    {
        var output = new List<MsilInstruction>();

        foreach (MsilInstruction instruction in source)
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
                _ => Bracket(instruction, target, newLocal, output),
            };

            if (done)
            {
                rewritten.TryGetValue(target, out int count);
                rewritten[target] = count + 1;
            }
        }

        return TorchIl.KeepLeaves(output);
    }

    public static bool Matches(MsilInstruction instruction, CallTarget target) =>
        CallMatch.Matches(instruction.OpCode, CalleeOf(instruction), target);

    /// <summary>What a call, a newobj or an ldftn refers to, or null for anything else.</summary>
    public static MethodBase? CalleeOf(MsilInstruction instruction) => (instruction.Operand as MsilOperandInline<MethodBase>)?.Value;

    public static MsilInstruction Call(MethodInfo method) => new MsilInstruction(OpCodes.Call).InlineValue<MethodBase>(method);

    public static MsilInstruction LoadInt(int value) => new MsilInstruction(OpCodes.Ldc_I4).InlineValue(value);

    /// <summary>Hands a branch target and any exception block over to the code now going first.</summary>
    public static void MoveMarks(MsilInstruction from, MsilInstruction to)
    {
        foreach (MsilLabel label in from.Labels)
            to.Labels.Add(label);

        from.Labels.Clear();

        to.TryCatchOperations.AddRange(from.TryCatchOperations);
        from.TryCatchOperations.Clear();
    }

    /// <summary>Where new code before a call goes. Prefixes such as constrained. must stay attached to their call.</summary>
    public static int BeforePrefixes(List<MsilInstruction> output)
    {
        int insertAt = output.Count;

        while (insertAt > 0 && output[insertAt - 1].OpCode.OpCodeType == OpCodeType.Prefix)
            insertAt--;

        return insertAt;
    }

    private static bool Bracket(MsilInstruction call, CallTarget target, Func<Type, MsilLocal> newLocal, List<MsilInstruction> output)
    {
        MsilLocal started = newLocal(typeof(long));

        int insertAt = BeforePrefixes(output);
        MsilInstruction displaced = insertAt < output.Count ? output[insertAt] : call;

        MsilInstruction read = Call(s_clockNow);
        MoveMarks(displaced, read);

        output.Insert(insertAt, read);
        output.Insert(insertAt + 1, started.AsValueStore());
        output.Add(call);
        output.Add(started.AsValueLoad());

        if (target.Kind == CallSiteKind.FrameEnd)
        {
            output.Add(Call(s_frameEnd));
        }
        else
        {
            output.Add(LoadInt(target.Scope));
            output.Add(Call(s_treeStop));
        }

        return true;
    }

    private static bool Tally(MsilInstruction call, CallTarget target, List<MsilInstruction> output)
    {
        if (target.Hook == null || target.Loads == null)
        {
            output.Add(call);
            return false;
        }

        int insertAt = BeforePrefixes(output);
        MsilInstruction displaced = insertAt < output.Count ? output[insertAt] : call;

        List<MsilInstruction> added = target.Loads.Select(Build).ToList();
        added.Add(Call(target.Hook));
        MoveMarks(displaced, added[0]);

        output.InsertRange(insertAt, added);
        output.Add(call);
        return true;
    }

    private static MsilInstruction Build(IlStep step)
    {
        var instruction = new MsilInstruction(step.OpCode);

        return step.Operand switch
        {
            null => instruction,
            FieldInfo field => instruction.InlineValue(field),
            MethodBase method => instruction.InlineValue(method),
            _ => throw new NotSupportedException("A tally can't load a " + step.Operand.GetType().Name + "."),
        };
    }

    private static bool Substitute(MsilInstruction call, CallTarget target, List<MsilInstruction> output)
    {
        var method = (MethodInfo)CalleeOf(call)!;

        bool hasPrefix = output.Count > 0 && output[output.Count - 1].OpCode.OpCodeType == OpCodeType.Prefix;
        bool isBaseCall = call.OpCode == OpCodes.Call && method.IsVirtual;

        if (hasPrefix || isBaseCall || target.Hook == null || !CallMatch.Fits(target.Hook, method))
        {
            output.Add(call);
            return false;
        }

        // An instruction's opcode can't change, so this is a copy, which keeps its labels and blocks.
        output.Add(call.CopyWith(OpCodes.Call).InlineValue<MethodBase>(target.Hook));
        return true;
    }
}
