using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>Putting a drill-down in and taking it out through Harmony.</summary>
internal static partial class DrillDown
{
    public const string HarmonyId = "senx.monitor.drill";

    private static readonly HarmonyMethod s_transpiler = new(AccessTools.Method(typeof(DrillDown), nameof(Transpile)));

    private static Harmony? s_harmony;

    private static void Patch(MethodBase body)
    {
        s_harmony ??= new Harmony(HarmonyId);
        s_harmony.Patch(body, transpiler: s_transpiler);
    }

    private static void Unpatch(MethodBase body) => s_harmony!.Unpatch(body, HarmonyPatchType.All, HarmonyId);

    private static void UnpatchAll()
    {
        OwnPatches.Remove(s_harmony);
        s_harmony = null;
    }

    /// <summary>
    /// Times the whole method and every call in it. Each addition leaves the stack as it found it, and
    /// labels and exception blocks move to the first new instruction, the same as Monitor's own
    /// patches. A site that doesn't fit is left alone, and the method still runs as written.
    /// </summary>
    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        LocalBuilder whole = generator.DeclareLocal(typeof(long));
        LocalBuilder started = generator.DeclareLocal(typeof(long));

        var output = new List<CodeInstruction>
        {
            new(OpCodes.Call, s_clockNow),
            new(OpCodes.Stloc, whole),
        };

        foreach (CodeInstruction instruction in instructions)
        {
            // Nothing can sit between a tail call and its return, so one of those isn't counted as a return.
            if (instruction.opcode == OpCodes.Ret && !IsTailCall(output, output.Count - 2))
            {
                var load = new CodeInstruction(OpCodes.Ldloc, whole);
                load.MoveLabelsFrom(instruction);
                load.MoveBlocksFrom(instruction);

                output.Add(load);
                output.Add(new CodeInstruction(OpCodes.Ldc_I4, WholeMethod));
                output.Add(new CodeInstruction(OpCodes.Call, s_record));
                output.Add(instruction);
                continue;
            }

            int site = instruction.operand is MethodBase callee && IsCall(instruction) && !IsTailCall(output, output.Count - 1) ? SiteOf(callee) : -1;

            if (site < 0)
            {
                output.Add(instruction);
                continue;
            }

            int insertAt = output.Count;

            // Prefixes such as constrained. must stay attached to their call, so new code goes before them.
            while (insertAt > 0 && output[insertAt - 1].opcode.OpCodeType == OpCodeType.Prefix)
                insertAt--;

            CodeInstruction displaced = insertAt < output.Count ? output[insertAt] : instruction;

            var read = new CodeInstruction(OpCodes.Call, s_clockNow);
            read.MoveLabelsFrom(displaced);
            read.MoveBlocksFrom(displaced);

            output.Insert(insertAt, read);
            output.Insert(insertAt + 1, new CodeInstruction(OpCodes.Stloc, started));
            output.Add(instruction);
            output.Add(new CodeInstruction(OpCodes.Ldloc, started));
            output.Add(new CodeInstruction(OpCodes.Ldc_I4, site));
            output.Add(new CodeInstruction(OpCodes.Call, s_record));
        }

        return output;
    }

    private static bool IsCall(CodeInstruction instruction) =>
        instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Newobj;

    // Looks back over the prefixes on a call, which can come in either order.
    private static bool IsTailCall(List<CodeInstruction> output, int lastPrefix)
    {
        for (int at = lastPrefix; at >= 0 && output[at].opcode.OpCodeType == OpCodeType.Prefix; at--)
        {
            if (output[at].opcode == OpCodes.Tailcall)
                return true;
        }

        return false;
    }
}
