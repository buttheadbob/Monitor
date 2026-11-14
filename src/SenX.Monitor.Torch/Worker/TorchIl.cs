using System.Reflection;
using System.Reflection.Emit;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>
/// Where Torch's rebuild of a method can change what it does, or its sort can go wrong. Rewrites keep a lost
/// leave with <see cref="KeepLeaves"/>. Audits and plugin timing only add prefixes and suffixes, so they have
/// no rewrite to do it in, and they refuse such a method.
/// </summary>
internal static class TorchIl
{
    /// <summary>
    /// True when a plain rebuild of this method could change what it does. When Torch writes a method back
    /// out it drops the leave at the end of a try, because the IL generator adds its own, and that one goes
    /// to the end of the handlers. So in <c>try { A(); return; } catch { } B();</c>, B would run after a success.
    /// </summary>
    public static bool LosesALeave(MethodBase method) => LostLeaves(PatchUtilities.ReadInstructions(method).ToList()).Count > 0;

    /// <summary>
    /// True when one try has several catches. Torch reads each as its own try and writes them back nested, so
    /// an exception thrown inside the first catch could be caught by the second, which it never could before.
    /// </summary>
    public static bool HasSiblingCatches(MethodBase method)
    {
        MethodBody? body = method.GetMethodBody();

        return body != null && body.ExceptionHandlingClauses
            .GroupBy(clause => (clause.TryOffset, clause.TryLength))
            .Any(clauses => clauses.Count() > 1);
    }

    /// <summary>
    /// True when an entry of default priority can go on the end of this set. Torch's sort overruns its list
    /// when a lower priority comes before a higher one, and it has already overwritten that list by the time
    /// it throws, which loses the other plugin's entry and leaves this one in for good.
    /// </summary>
    public static bool TakesAnotherEntry(IEnumerable<MethodInfo> entries) =>
        entries.All(entry => (entry.GetCustomAttribute<PatchPriorityAttribute>()?.Priority ?? 0) >= 0);

    /// <summary>Keeps every leave Torch would lose. It only drops one right before a handler, so a nop between them is enough, and nothing ever reaches the nop.</summary>
    public static List<MsilInstruction> KeepLeaves(List<MsilInstruction> code)
    {
        List<int> lost = LostLeaves(code);

        for (int i = lost.Count - 1; i >= 0; i--)
            code.Insert(lost[i] + 1, new MsilInstruction(OpCodes.Nop));

        return code;
    }

    /// <summary>Where each leave is that Torch would drop and shouldn't.</summary>
    public static List<int> LostLeaves(IReadOnlyList<MsilInstruction> code)
    {
        var lost = new List<int>();

        for (int i = 0; i + 1 < code.Count; i++)
        {
            MsilInstruction leave = code[i];

            if (leave.OpCode != OpCodes.Leave && leave.OpCode != OpCodes.Leave_S)
                continue;

            if (!code[i + 1].TryCatchOperations.Any(EndsATry))
                continue;

            int end = EndOfHandlers(code, i + 1);

            if (end < 0 || leave.Operand is not MsilOperandBrTarget branch || !code[end].Labels.Contains(branch.Target))
                lost.Add(i);
        }

        return lost;
    }

    private static bool EndsATry(MsilTryCatchOperation operation) => operation.Type != MsilTryCatchOperationType.BeginExceptionBlock;

    private static bool StartsAHandler(MsilTryCatchOperation operation) =>
        operation.Type is MsilTryCatchOperationType.BeginClauseBlock or MsilTryCatchOperationType.BeginFaultBlock or MsilTryCatchOperationType.BeginFinallyBlock;

    // Where control lands after the last handler of this try. Tries inside the handlers are stepped over.
    private static int EndOfHandlers(IReadOnlyList<MsilInstruction> code, int from)
    {
        int depth = 0;

        for (int i = from; i < code.Count; i++)
        {
            int ends = code[i].TryCatchOperations.Count(operation => operation.Type == MsilTryCatchOperationType.EndExceptionBlock);
            int begins = code[i].TryCatchOperations.Count(operation => operation.Type == MsilTryCatchOperationType.BeginExceptionBlock);

            // On one instruction, what ends was opened before what begins, as with a try straight after a catch.
            if (ends > depth)
            {
                // Torch reads a try with two catches as two tries, so one ending into the next catch hasn't ended.
                if (!code[i].TryCatchOperations.Any(StartsAHandler))
                    return i;

                depth = 0;
            }
            else
            {
                depth -= ends;
            }

            depth += begins;
        }

        return -1;
    }
}

/// <summary>
/// A post-transpiler that goes on safely. Torch's sort overruns when a lower priority comes before a higher
/// one, so one that's first in a method's list takes the top priority, which nothing added later can
/// be above. One joining a list stays at the default, which is safe beside other defaults.
/// </summary>
internal sealed class PostTranspiler
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private readonly MethodInfo _first;
    private readonly MethodInfo _later;

    public PostTranspiler(Type owner, string first, string later)
    {
        _first = owner.GetMethod(first, AnyStatic)!;
        _later = owner.GetMethod(later, AnyStatic)!;
    }

    public void Add(MethodRewritePattern own, MethodRewritePattern all) =>
        own.PostTranspilers.Add(all.PostTranspilers.Count == 0 ? _first : _later);

    public void Remove(MethodRewritePattern own)
    {
        own.PostTranspilers.Remove(_first);
        own.PostTranspilers.Remove(_later);
    }
}
