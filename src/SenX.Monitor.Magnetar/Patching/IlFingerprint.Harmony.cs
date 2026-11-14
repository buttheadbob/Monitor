using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>Reading a method through Harmony, for the fingerprint.</summary>
internal static partial class IlFingerprint
{
    public static string Compute(MethodBase method) => Compute(PatchProcessor.GetOriginalInstructions(method));

    public static string Compute(IReadOnlyList<CodeInstruction> instructions)
    {
        var labels = new Dictionary<Label, int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            foreach (Label label in instructions[i].labels)
                labels[label] = i;
        }

        ulong hash = Offset;
        for (int i = 0; i < instructions.Count; i++)
        {
            CodeInstruction instruction = instructions[i];
            hash = Mix(hash, (ushort)instruction.opcode.Value);
            hash = MixOperand(hash, instruction.operand, labels, i);
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static ulong MixOperand(ulong hash, object? operand, Dictionary<Label, int> labels, int index)
    {
        switch (operand)
        {
            case Label label:
                return Mix(hash, labels.TryGetValue(label, out int target) ? (ulong)(target - index) : 0UL);
            case Label[] table:
                foreach (Label entry in table)
                    hash = Mix(hash, labels.TryGetValue(entry, out int destination) ? (ulong)(destination - index) : 0UL);
                return hash;
            case LocalVariableInfo local:
                return Mix(hash, (ulong)local.LocalIndex);
            default:
                return MixMember(hash, operand);
        }
    }
}
