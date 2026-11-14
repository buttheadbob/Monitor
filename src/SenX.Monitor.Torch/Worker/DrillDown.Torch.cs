using System.Reflection;
using System.Reflection.Emit;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>
/// Putting a drill-down in and taking it out through Torch's patch manager. This is a normal transpiler,
/// so it runs before Monitor's post-transpiler and sees the method's own calls and not Monitor's hooks.
/// </summary>
internal static partial class DrillDown
{
    private static readonly MethodInfo s_transpiler = typeof(DrillDown).GetMethod(nameof(Transpile), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static PatchContext? s_context;

    private static void Patch(MethodBase body)
    {
        // Torch's rebuilt method takes its instance as an object, which isn't what a struct's method gets.
        if (body.DeclaringType?.IsValueType == true && !body.IsStatic)
            throw new NotSupportedException("Torch can't patch a method on a struct.");

        if (TorchIl.HasSiblingCatches(body))
            throw new NotSupportedException("Torch rebuilding this method would change how its catches work.");

        PatchManager manager = PatchEngine.Manager!;

        if (!TorchIl.TakesAnotherEntry(manager.GetPattern(body).Transpilers))
            throw new NotSupportedException("Another plugin's transpiler on this method has a negative priority, which Torch can't sort beside this one.");

        s_context = manager.AcquireContext();
        s_context.GetPattern(body).Transpilers.Add(s_transpiler);

        try
        {
            manager.Commit();
        }
        catch
        {
            // Left waiting, this would go in by itself the next time anything commits.
            UnpatchAll();
            throw;
        }
    }

    private static void Unpatch(MethodBase body) => UnpatchAll();

    private static void UnpatchAll()
    {
        if (s_context == null)
            return;

        PatchEngine.Manager!.FreeContext(s_context);
        s_context = null;
        PatchEngine.CommitRemaining();
    }

    /// <summary>
    /// Times the whole method and every call in it. Each addition leaves the stack as it found it, and
    /// labels and exception blocks move to the first new instruction, the same as Monitor's own
    /// patches. A site that doesn't fit is left alone, and the method still runs as written.
    /// </summary>
    private static IEnumerable<MsilInstruction> Transpile(IEnumerable<MsilInstruction> instructions, Func<Type, MsilLocal> __localCreator)
    {
        MsilLocal whole = __localCreator(typeof(long));
        MsilLocal started = __localCreator(typeof(long));

        var output = new List<MsilInstruction> { MsilRewriter.Call(s_clockNow), whole.AsValueStore() };

        foreach (MsilInstruction instruction in instructions)
        {
            // Nothing can sit between a tail call and its return, so one of those isn't counted as a return.
            if (instruction.OpCode == OpCodes.Ret && !IsTailCall(output, output.Count - 2))
            {
                MsilInstruction load = whole.AsValueLoad();
                MsilRewriter.MoveMarks(instruction, load);

                output.Add(load);
                output.Add(MsilRewriter.LoadInt(WholeMethod));
                output.Add(MsilRewriter.Call(s_record));
                output.Add(instruction);
                continue;
            }

            int site = IsCall(instruction) && MsilRewriter.CalleeOf(instruction) is { } callee && !IsTailCall(output, output.Count - 1) ? SiteOf(callee) : -1;

            if (site < 0)
            {
                output.Add(instruction);
                continue;
            }

            int insertAt = MsilRewriter.BeforePrefixes(output);
            MsilInstruction displaced = insertAt < output.Count ? output[insertAt] : instruction;

            MsilInstruction read = MsilRewriter.Call(s_clockNow);
            MsilRewriter.MoveMarks(displaced, read);

            output.Insert(insertAt, read);
            output.Insert(insertAt + 1, started.AsValueStore());
            output.Add(instruction);
            output.Add(started.AsValueLoad());
            output.Add(MsilRewriter.LoadInt(site));
            output.Add(MsilRewriter.Call(s_record));
        }

        return TorchIl.KeepLeaves(output);
    }

    private static bool IsCall(MsilInstruction instruction) =>
        instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Newobj;

    // Looks back over the prefixes on a call, which can come in either order.
    private static bool IsTailCall(List<MsilInstruction> output, int lastPrefix)
    {
        for (int at = lastPrefix; at >= 0 && output[at].OpCode.OpCodeType == OpCodeType.Prefix; at--)
        {
            if (output[at].OpCode == OpCodes.Tailcall)
                return true;
        }

        return false;
    }
}
