using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SenX.Monitor.Core;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>Applying the table through Harmony.</summary>
internal static partial class PatchEngine
{
    private const string HarmonyId = "senx.monitor";

    /// <summary>The Harmony id Monitor's own patches go under.</summary>
    public const string OwnHarmonyId = HarmonyId;

    private static Harmony? s_harmony;

    /// <summary>Patches everything it safely can. Never throws.</summary>
    public static void Apply()
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            List<CallerSpec> table = TargetTable.Build();
            List<SiteResult> evaluated = Evaluate(table);

            s_harmony = new Harmony(HarmonyId);
            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(PatchEngine), nameof(Transpile)), Priority.Last);

            foreach (CallerSpec caller in table)
            {
                if (caller.Method == null)
                    continue;

                try
                {
                    s_active[caller.Method] = caller;
                    s_harmony.Patch(caller.Method, transpiler: transpiler);
                    PatchedMethods++;
                }
                catch (Exception exception)
                {
                    s_active.Remove(caller.Method);
                    Forget(caller);

                    MonitorLog.Warning($"Could not patch {caller.Id}: {exception.Message}");
                }
            }

            RecordResults(evaluated);
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Patching failed, Monitor will not measure anything.", exception);
        }

        LoadMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
    }

    /// <summary>Removes only Monitor's own patches. Other plugins' patches are left alone.</summary>
    public static void Remove()
    {
        Gates.CloseAll();
        OwnPatches.Remove(s_harmony);
        s_harmony = null;
        ForgetAll();
    }

    /// <summary>
    /// Why clusters can't step alone, or null when they can. A cluster that steps alone relies on the
    /// parallel path skipping it, otherwise it would step twice in a frame. So this needs both hooks
    /// in place, the game's code exactly as verified, and no other plugin changing these methods. A plugin
    /// that only watches them is fine, and Quasar's agent times StepWorldsInternal on every managed server.
    /// Checked at the start of each run, because other plugins may patch after Monitor loads.
    /// </summary>
    public static string? ClusterTurnProblem()
    {
        // Plugins first, because one that changes these also fails the check below, and its name is the useful answer.
        foreach (string name in new[] { "StepWorldsInternal", "StepWorldsParallel", "StepSingleWorld", "IsClusterActive" })
        {
            MethodBase? method = AccessTools.Method("Sandbox.Engine.Physics.MyPhysics:" + name);
            if (method == null)
                return "the game no longer has " + name;

            string? other = OwnerWhoCouldChange(Harmony.GetPatchInfo(method));
            if (other != null)
                return "the plugin " + other + " also changes " + name;
        }

        return ClusterSitesVerified(Results) ? null : "this game build's physics stepping differs from what Monitor has verified";
    }

    // A transpiler or a finalizer changes what runs. A prefix or a postfix might not, which is down to its signature.
    private static string? OwnerWhoCouldChange(Patches? patches)
    {
        if (patches == null)
            return null;

        Patch? changes = patches.Transpilers.Concat(patches.Finalizers).FirstOrDefault(patch => patch.owner != HarmonyId)
                         ?? patches.Prefixes.Concat(patches.Postfixes).FirstOrDefault(patch => patch.owner != HarmonyId && !OnlyWatches(PatchList.PatchMethodOf(patch)));

        return changes?.owner;
    }

    // Returning nothing means it can't skip the method or replace its result, and a by-ref parameter is the
    // other way to reach those, apart from __state, which is only the patch's own note to itself.
    private static bool OnlyWatches(MethodInfo? patch) =>
        patch != null &&
        patch.ReturnType == typeof(void)
        && patch.GetParameters().All(parameter => !parameter.ParameterType.IsByRef || parameter.Name == "__state");

    /// <summary>Times each plugin's Update. It needs the plugins' types, so this comes after they've been found.</summary>
    public static void HookPlugins(IEnumerable<Type> plugins)
    {
        if (s_harmony != null)
            s_pluginsHooked = Hooks.PluginHooks.Apply(s_harmony, plugins);
    }

    private static IEnumerable<CodeInstruction> Transpile(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator,
        MethodBase __originalMethod)
    {
        // Priority.Last means this sees other plugins' edits, so their changes are inside what gets timed.
        if (!s_active.TryGetValue(__originalMethod, out CallerSpec? caller))
            return instructions;

        // This runs again each time anyone changes this method, and the count is of what's in it now.
        var counts = new Dictionary<CallTarget, int>();
        var matched = new Dictionary<CallTarget, int>();
        List<CodeInstruction> rewritten = CallSiteRewriter.Rewrite(instructions, caller.Targets, generator, counts, matched);

        NoteRewritten(caller, counts, matched);
        return rewritten;
    }

    /// <summary>What a method's original code does, as the table reads it.</summary>
    internal static IEnumerable<IlStep> ReadIl(MethodBase method) =>
        PatchProcessor.GetOriginalInstructions(method).Select(instruction => new IlStep(instruction.opcode, instruction.operand));
}
