using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>What Harmony says is patched.</summary>
internal static partial class PatchList
{
    private static IEnumerable<MethodBase> AllPatched() => Harmony.GetAllPatchedMethods();

    public static List<PatchEntryRow> Rows(MethodBase method)
    {
        var rows = new List<PatchEntryRow>();
        Patches? info = Harmony.GetPatchInfo(method);

        if (info == null)
            return rows;

        Add(rows, info.Prefixes, PatchKinds.Prefix);
        Add(rows, info.Postfixes, PatchKinds.Postfix);
        Add(rows, info.Transpilers, PatchKinds.Transpiler);
        Add(rows, info.Finalizers, PatchKinds.Finalizer);
        return rows;
    }

    private static void Add(List<PatchEntryRow> rows, IEnumerable<Patch> patches, string kind)
    {
        foreach (Patch patch in patches)
        {
            if (patch.owner == PatchEngine.OwnHarmonyId || patch.owner == PatchAudit.HarmonyId || patch.owner == DrillDown.HarmonyId)
                continue;

            MethodInfo? method = PatchMethodOf(patch);

            rows.Add(new PatchEntryRow
            {
                Kind = kind,
                Owner = patch.owner,
                Plugin = method?.DeclaringType?.Assembly.GetName().Name is { } assembly ? ModNames.PluginName(assembly) : patch.owner,
                Patch = method == null ? "?" : (method.DeclaringType?.Name ?? "?") + "." + method.Name,
                Priority = patch.priority,
            });
        }
    }

    // A plugin's patch can refuse to resolve, and that mustn't lose the rest of the list.
    internal static MethodInfo? PatchMethodOf(Patch patch)
    {
        try
        {
            return patch.PatchMethod;
        }
        catch (Exception exception) when (exception is ArgumentException or TypeLoadException or NullReferenceException)
        {
            return null;
        }
    }
}

/// <summary>What a call has timed so far. Harmony keeps one for each call, for the shims to share.</summary>
public struct AuditTiming
{
    public long Mark;
    public long Prefix;
    public long Original;
}

/// <summary>
/// Called by Harmony around an audited method. The two prefixes sit either side of every other plugin's
/// prefixes, and the two postfixes either side of theirs, which splits a call into three. A method
/// that throws loses that one sample, which is harmless. Public, because generated code calls these.
/// </summary>
public static class AuditShims
{
    public static void PrefixFirst(ref AuditTiming __state) => __state.Mark = Clock.Now();

    public static void PrefixLast(ref AuditTiming __state)
    {
        __state.Prefix = Clock.Now() - __state.Mark;
        __state.Mark = Clock.Now();
    }

    public static void PostfixFirst(ref AuditTiming __state)
    {
        __state.Original = Clock.Now() - __state.Mark;
        __state.Mark = Clock.Now();
    }

    public static void PostfixLast(ref AuditTiming __state, MethodBase __originalMethod)
    {
        long postfix = Clock.Now() - __state.Mark;
        PatchAudit.Record(__originalMethod, __state.Prefix, __state.Original, postfix);
    }
}

/// <summary>Putting an audit's shims in and taking them out through Harmony.</summary>
internal static partial class PatchAudit
{
    public const string HarmonyId = "senx.monitor.audit";

    private static readonly HarmonyMethod s_prefixFirst = Shim(nameof(AuditShims.PrefixFirst), int.MaxValue);
    private static readonly HarmonyMethod s_prefixLast = Shim(nameof(AuditShims.PrefixLast), int.MinValue);
    private static readonly HarmonyMethod s_postfixFirst = Shim(nameof(AuditShims.PostfixFirst), int.MaxValue);
    private static readonly HarmonyMethod s_postfixLast = Shim(nameof(AuditShims.PostfixLast), int.MinValue);

    private static Harmony? s_harmony;

    private static HarmonyMethod Shim(string name, int priority) =>
        new(AccessTools.Method(typeof(AuditShims), name)) { priority = priority };

    private static void BeginPatching() => s_harmony = new Harmony(HarmonyId);

    // A processor holds one prefix and one postfix, and adding another replaces it, so each pair goes in by itself.
    private static void Patch(MethodBase method)
    {
        s_harmony!.CreateProcessor(method).AddPrefix(s_prefixFirst).AddPostfix(s_postfixFirst).Patch();
        s_harmony.CreateProcessor(method).AddPrefix(s_prefixLast).AddPostfix(s_postfixLast).Patch();
    }

    private static void Unpatch(MethodBase method) => s_harmony!.Unpatch(method, HarmonyPatchType.All, HarmonyId);

    private static void UnpatchAll()
    {
        OwnPatches.Remove(s_harmony);
        s_harmony = null;
    }
}
