using System.Globalization;
using System.Reflection;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>
/// Reading a method through Torch, for the fingerprint. This build's table comes from Torch's own
/// reader, through tools/SenX.Monitor.Torch.Lock, and it's separate from Magnetar's.
/// </summary>
internal static partial class IlFingerprint
{
    public static string Compute(MethodBase method) => Compute(PatchUtilities.ReadInstructions(method).ToList());

    public static string Compute(IReadOnlyList<MsilInstruction> instructions)
    {
        var labels = new Dictionary<MsilLabel, int>();

        for (int i = 0; i < instructions.Count; i++)
        {
            foreach (MsilLabel label in instructions[i].Labels)
                labels[label] = i;
        }

        ulong hash = Offset;

        for (int i = 0; i < instructions.Count; i++)
        {
            MsilInstruction instruction = instructions[i];
            hash = Mix(hash, (ushort)instruction.OpCode.Value);
            hash = MixOperand(hash, instruction.Operand, labels, i);
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static ulong MixOperand(ulong hash, MsilOperand? operand, Dictionary<MsilLabel, int> labels, int index)
    {
        switch (operand)
        {
            case null:
                return hash;
            case MsilOperandBrTarget branch:
                return Mix(hash, labels.TryGetValue(branch.Target, out int target) ? (ulong)(target - index) : 0UL);
            case MsilOperandSwitch table:
                foreach (MsilLabel entry in table.Labels)
                    hash = Mix(hash, labels.TryGetValue(entry, out int destination) ? (ulong)(destination - index) : 0UL);
                return hash;
            case MsilOperandInline<MsilLocal> local:
                return Mix(hash, (ulong)local.Value.Index);
            case MsilOperandInline<MsilArgument> argument:
                return Mix(hash, (ulong)argument.Value.Position);
            case MsilOperandInline<MethodBase> method:
                return MixMember(hash, method.Value);
            case MsilOperandInline<FieldInfo> field:
                return MixMember(hash, field.Value);
            case MsilOperandInline<Type> type:
                return MixMember(hash, type.Value);
            case MsilOperandInline<MemberInfo> member:
                return MixMember(hash, member.Value);
            case MsilOperandInline<string> text:
                return MixMember(hash, text.Value);
            case MsilOperandInline<int> number:
                return MixMember(hash, number.Value);
            case MsilOperandInline<long> number:
                return MixMember(hash, number.Value);
            case MsilOperandInline<float> number:
                return MixMember(hash, number.Value);
            case MsilOperandInline<double> number:
                return MixMember(hash, number.Value);
            default:
                return MixMember(hash, operand.GetType().Name);
        }
    }
}
