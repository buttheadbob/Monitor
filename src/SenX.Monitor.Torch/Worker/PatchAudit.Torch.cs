using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>
/// What's patched. Torch has no public list, so this reads its private table. If a later Torch moves
/// that, the list comes back empty and <see cref="CanRead"/> says so, and nothing else breaks.
/// </summary>
internal static partial class PatchList
{
    private static readonly FieldInfo? s_patterns = typeof(PatchManager).GetField("_rewritePatterns", BindingFlags.NonPublic | BindingFlags.Static);

    /// <summary>False when this version of Torch keeps its patches somewhere else.</summary>
    public static bool CanRead => Table != null;

    private static IDictionary? Table => s_patterns?.GetValue(null) as IDictionary;

    private static IEnumerable<MethodBase> AllPatched()
    {
        IDictionary? table = Table;

        if (table == null)
            return Array.Empty<MethodBase>();

        // Torch locks this table itself while it changes it.
        lock (table)
            return table.Keys.Cast<MethodBase>().ToList();
    }

    public static List<PatchEntryRow> Rows(MethodBase method)
    {
        var rows = new List<PatchEntryRow>();
        IDictionary? table = Table;

        if (table == null)
            return rows;

        lock (table)
        {
            if (table[method] is MethodRewritePattern pattern)
            {
                Add(rows, pattern.Prefixes, PatchKinds.Prefix);
                Add(rows, pattern.Suffixes, PatchKinds.Postfix);
                Add(rows, pattern.Transpilers, PatchKinds.Transpiler);
                Add(rows, pattern.PostTranspilers, PatchKinds.Transpiler);
            }
        }

        return rows;
    }

    private static void Add(List<PatchEntryRow> rows, IEnumerable<MethodInfo> patches, string kind)
    {
        foreach (MethodInfo patch in patches)
        {
            Assembly? owner = patch.DeclaringType?.Assembly;

            if (owner == typeof(PatchList).Assembly)
                continue;

            string name = owner?.GetName().Name ?? "?";

            rows.Add(new PatchEntryRow
            {
                Kind = kind,
                Owner = name,
                Plugin = name,
                Patch = (patch.DeclaringType?.Name ?? "?") + "." + patch.Name,
                Priority = patch.GetCustomAttribute<PatchPriorityAttribute>()?.Priority ?? 0,
            });
        }
    }
}

/// <summary>Called by rewritten code. Public, because generated code calls it.</summary>
public static class AuditShims
{
    public static void Finish(long started, long afterPrefixes, long beforeSuffixes, int index)
    {
        long ended = Clock.Now();

        // A prefix that stops the original stops the ones after it too, so the mark after the last isn't set.
        long prefixesEnded = afterPrefixes == 0L ? beforeSuffixes : afterPrefixes;

        PatchAudit.Record(index, prefixesEnded - started, beforeSuffixes - prefixesEnded, ended - beforeSuffixes);
    }
}

/// <summary>
/// An audit on Torch, as one post-transpiler. That runs once a method is whole, with its prefixes and
/// suffixes in place, so the clock can be read around them with no shims and no priorities. The method's
/// row goes in as a number, so nothing has to look it up on each call.
///
/// Everything audited here is already rebuilt by Torch for the plugin that patched it, so this adds
/// no new risk from how it does that, and it keeps any leave that would otherwise be lost.
/// </summary>
internal static partial class PatchAudit
{
    private static readonly PostTranspiler s_transpiler = new(typeof(PatchAudit), nameof(TranspileFirst), nameof(Transpile));
    private static readonly MethodInfo s_clockNow = typeof(Clock).GetMethod(nameof(Clock.Now))!;
    private static readonly MethodInfo s_finish = typeof(AuditShims).GetMethod(nameof(AuditShims.Finish))!;

    private static PatchContext? s_context;

    private static void BeginPatching() => s_context ??= PatchEngine.Manager!.AcquireContext();

    private static void Patch(MethodBase method)
    {
        PatchManager manager = PatchEngine.Manager!;
        MethodRewritePattern all = manager.GetPattern(method);

        if (!TorchIl.TakesAnotherEntry(all.PostTranspilers))
            throw new NotSupportedException("Another plugin's post-transpiler on this method has a negative priority, which Torch can't sort beside this one.");

        s_transpiler.Add(s_context!.GetPattern(method), all);

        try
        {
            manager.Commit();
        }
        catch
        {
            // Left waiting, this would go in by itself the next time anything commits.
            Unpatch(method);
            throw;
        }
    }

    private static void Unpatch(MethodBase method)
    {
        s_transpiler.Remove(s_context!.GetPattern(method));
        PatchEngine.CommitRemaining();
    }

    private static void UnpatchAll()
    {
        if (s_context == null)
            return;

        // A context can't take post-transpilers out by itself.
        for (int index = 0; index < s_count; index++)
        {
            if (s_methods[index] is { } method)
                s_transpiler.Remove(s_context.GetPattern(method));
        }

        PatchEngine.Manager!.FreeContext(s_context);
        s_context = null;
        PatchEngine.CommitRemaining();
    }

    [PatchPriority(int.MaxValue)]
    private static IEnumerable<MsilInstruction> TranspileFirst(
        IEnumerable<MsilInstruction> instructions,
        MethodBase __methodBase,
        Func<Type, MsilLocal> __localCreator) => Transpile(instructions, __methodBase, __localCreator);

    private static IEnumerable<MsilInstruction> Transpile(
        IEnumerable<MsilInstruction> instructions,
        MethodBase __methodBase,
        Func<Type, MsilLocal> __localCreator)
    {
        List<MsilInstruction> code = instructions.ToList();
        int index = IndexOf(__methodBase);

        if (index < 0)
            return code;

        MethodRewritePattern all = PatchEngine.Manager!.GetPattern(__methodBase);

        // Another plugin's post-transpiler can have moved these. No timing is better than wrong timing.
        if (!TryFind(code, all, out int lastPrefix, out int firstSuffix, out int end))
        {
            s_problems[index] = "Monitor couldn't find this method's prefixes and suffixes.";
            return code;
        }

        MsilLocal started = __localCreator(typeof(long));
        MsilLocal afterPrefixes = __localCreator(typeof(long));
        MsilLocal beforeSuffixes = __localCreator(typeof(long));

        // From the end back, so each position stays good. Each addition leaves the stack as it found it,
        // so these are safe with a return value, a prefix's answer or a suffix's arguments on it.
        var finish = new List<MsilInstruction>();

        if (firstSuffix < 0)
            finish.AddRange(ReadClockInto(beforeSuffixes));

        finish.Add(started.AsValueLoad());
        finish.Add(afterPrefixes.AsValueLoad());
        finish.Add(beforeSuffixes.AsValueLoad());
        finish.Add(MsilRewriter.LoadInt(index));
        finish.Add(MsilRewriter.Call(s_finish));
        MsilRewriter.MoveMarks(code[end], finish[0]);
        code.InsertRange(end, finish);

        if (firstSuffix >= 0)
        {
            List<MsilInstruction> read = ReadClockInto(beforeSuffixes);
            MsilRewriter.MoveMarks(code[firstSuffix], read[0]);
            code.InsertRange(firstSuffix, read);
        }

        if (lastPrefix >= 0)
            code.InsertRange(lastPrefix + 1, ReadClockInto(afterPrefixes));

        List<MsilInstruction> start = ReadClockInto(started);

        // Nothing moves the first instruction's marks, so a loop back to it doesn't read the clock again.
        if (lastPrefix < 0)
        {
            start.Add(started.AsValueLoad());
            start.Add(afterPrefixes.AsValueStore());
        }

        code.InsertRange(0, start);
        return TorchIl.KeepLeaves(code);
    }

    private static List<MsilInstruction> ReadClockInto(MsilLocal local) => new() { MsilRewriter.Call(s_clockNow), local.AsValueStore() };

    private static bool TryFind(List<MsilInstruction> code, MethodRewritePattern all, out int lastPrefix, out int firstSuffix, out int end)
    {
        lastPrefix = -1;
        firstSuffix = -1;
        end = code.FindLastIndex(instruction => instruction.OpCode == OpCodes.Ret);

        foreach (MethodInfo prefix in Others(all.Prefixes))
        {
            int at = code.FindIndex(instruction => MsilRewriter.CalleeOf(instruction) == prefix);

            if (at < 0)
                return false;

            lastPrefix = Math.Max(lastPrefix, at);
        }

        foreach (MethodInfo suffix in Others(all.Suffixes))
        {
            int at = code.FindLastIndex(instruction => MsilRewriter.CalleeOf(instruction) == suffix);

            if (at < 0)
                return false;

            firstSuffix = firstSuffix < 0 ? at : Math.Min(firstSuffix, at);
        }

        return end >= 0 && lastPrefix < end && firstSuffix < end;
    }

    // Monitor's own plugin timing isn't what an audit is asking about.
    private static IEnumerable<MethodInfo> Others(IEnumerable<MethodInfo> patches) =>
        patches.Where(patch => patch.DeclaringType?.Assembly != typeof(PatchAudit).Assembly);
}
