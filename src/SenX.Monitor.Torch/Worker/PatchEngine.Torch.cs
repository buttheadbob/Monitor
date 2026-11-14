using System.Diagnostics;
using System.Reflection;
using Sandbox.Engine.Physics;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>
/// Applying the table through Torch's own patch manager. Four things about it shape this file:
/// <list type="bullet">
/// <item>Monitor runs last so other plugins' edits are inside what's timed, and so they never see Monitor's.
/// Priority can't do that here, because Torch's sort overruns its list when a low priority entry comes
/// before a higher one. Post-transpilers run after everything, and <see cref="PostTranspiler"/> keeps
/// Monitor's clear of that sort. It won't go in beside a negative priority.</item>
/// <item>Removing a context leaves post-transpilers behind, so these are taken out by hand.</item>
/// <item>A rebuild runs every transpiler again, including when someone else changes a method later.</item>
/// <item>A commit does whatever is waiting and stops at the first failure, leaving that method bare and
/// the rest still waiting for whoever commits next. That could be Torch, where it would stop the server.
/// So Monitor clears what's waiting, then commits one method at a time, which always names the right
/// method when one fails and leaves nothing behind. It adds a few lines per method to Torch's log.</item>
/// </list>
/// </summary>
internal static partial class PatchEngine
{
    // Each pass gets past one failed method, so this only limits a table's worth of bad luck.
    private const int MostCommitPasses = 64;

    // Only ever stops a cluster step when Havok is out of memory, and then the server is restarting anyway.
    private const string TorchMemoryCheck = "PhysicsMemoryPatch.PrefixPhysics";

    private static readonly string[] s_clusterMethods = { "StepWorldsInternal", "StepWorldsParallel", "StepSingleWorld", "IsClusterActive" };

    private static readonly PostTranspiler s_transpile = new(typeof(PatchEngine), nameof(TranspileFirst), nameof(Transpile));

    private static PatchContext? s_context;

    /// <summary>Torch's patch manager, which everything that patches here goes through.</summary>
    public static PatchManager? Manager { get; set; }

    /// <summary>Patches everything it safely can. Never throws.</summary>
    public static void Apply()
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            List<CallerSpec> table = TargetTable.Build();
            List<SiteResult> evaluated = Evaluate(table);

            if (CommitRemaining())
                PatchAll(table);
            else
                MonitorLog.Warning("Torch could not apply another plugin's patches, so Monitor left the game alone.");

            RecordResults(evaluated);
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Patching failed, Monitor will not measure anything.", exception);
            TakeEverythingOut();
        }

        LoadMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
    }

    private static void PatchAll(List<CallerSpec> table)
    {
        s_context = Manager!.AcquireContext();

        foreach (CallerSpec caller in table)
        {
            if (caller.Method == null)
                continue;

            MethodRewritePattern all = Manager.GetPattern(caller.Method);

            if (!TorchIl.TakesAnotherEntry(all.PostTranspilers))
            {
                MonitorLog.Warning($"Skipped {caller.Id}, because another plugin's post-transpiler has a negative priority.");
                continue;
            }

            s_active[caller.Method] = caller;
            s_transpile.Add(s_context.GetPattern(caller.Method), all);
            PatchedMethods++;

            try
            {
                Manager.Commit();
            }
            catch (Exception exception)
            {
                // Dropping it also rebuilds it, which puts back any other plugin's patches the failure took out.
                MonitorLog.Warning($"Could not patch {caller.Id}: {exception.Message}");
                Drop(caller);
                CommitRemaining();
            }
        }
    }

    /// <summary>
    /// Commits what's waiting, however many fail. A commit stops at the first failure and won't try that
    /// method again, so going round finishes the rest and nothing is left for a commit nobody's guarding.
    /// True if it got to the end.
    /// </summary>
    internal static bool CommitRemaining()
    {
        for (int pass = 0; pass < MostCommitPasses; pass++)
        {
            try
            {
                Manager!.Commit();
                return true;
            }
            catch (Exception exception)
            {
                MonitorLog.Error("Torch could not apply a patch.", exception);
            }
        }

        return false;
    }

    private static void Drop(CallerSpec caller)
    {
        s_transpile.Remove(s_context!.GetPattern(caller.Method!));
        s_active.Remove(caller.Method!);
        PatchedMethods--;

        Forget(caller);
    }

    // Whatever's left must not sit waiting for Torch's next commit, where a failure wouldn't be caught.
    private static void TakeEverythingOut()
    {
        if (s_context == null || Manager == null)
            return;

        foreach (CallerSpec caller in s_active.Values.ToList())
            Drop(caller);

        CommitRemaining();
    }

    /// <summary>Removes only Monitor's own patches. Other plugins' patches are left alone.</summary>
    public static void Remove()
    {
        Gates.CloseAll();

        if (s_context == null || Manager == null)
            return;

        // A context can't take post-transpilers out by itself.
        foreach (CallerSpec caller in s_active.Values.ToList())
            Drop(caller);

        Manager.FreeContext(s_context);
        s_context = null;
        CommitRemaining();
    }

    /// <summary>
    /// Why clusters can't step alone, or null when they can. A cluster that steps alone relies on the
    /// parallel path skipping it, otherwise it would step twice in a frame. So this needs both hooks
    /// in place, the game's code exactly as verified, and no other plugin changing these methods.
    /// Checked at the start of each run, because other plugins may patch after Monitor loads.
    /// </summary>
    public static string? ClusterTurnProblem()
    {
        // Plugins first, because one that changes these also fails the check below, and its name is the useful answer.
        if (!PatchList.CanRead)
            return "this version of Torch doesn't show what else patches physics";

        const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        foreach (string name in s_clusterMethods)
        {
            MethodBase? method = typeof(MyPhysics).GetMethods(Everything).FirstOrDefault(candidate => candidate.Name == name);

            if (method == null)
                return "the game no longer has " + name;

            PatchEntryRow? other = PatchList.Rows(method).FirstOrDefault(row => row.Patch != TorchMemoryCheck);

            if (other != null)
                return "the plugin " + other.Plugin + " also changes " + name;
        }

        return ClusterSitesVerified(Results) ? null : "this game build's physics stepping differs from what Monitor has verified";
    }

    /// <summary>Times each plugin's Update. It needs the plugins' types, so this comes after they've been found.</summary>
    public static void HookPlugins(IEnumerable<Type> plugins)
    {
        // With nothing else patched, nothing would be measuring them.
        if (s_context != null && Manager != null && s_active.Count > 0)
            s_pluginsHooked = Hooks.PluginHooks.Apply(s_context, Manager, plugins);
    }

    [PatchPriority(int.MaxValue)]
    private static IEnumerable<MsilInstruction> TranspileFirst(
        IEnumerable<MsilInstruction> instructions,
        MethodBase __methodBase,
        Func<Type, MsilLocal> __localCreator) => Transpile(instructions, __methodBase, __localCreator);

    // As a post-transpiler this sees other plugins' edits, so their changes are inside what gets timed.
    private static IEnumerable<MsilInstruction> Transpile(
        IEnumerable<MsilInstruction> instructions,
        MethodBase __methodBase,
        Func<Type, MsilLocal> __localCreator)
    {
        if (!s_active.TryGetValue(__methodBase, out CallerSpec? caller))
            return instructions;

        // This runs again each time anyone changes this method, and the count is of what's in it now.
        var counts = new Dictionary<CallTarget, int>();
        var matched = new Dictionary<CallTarget, int>();
        List<MsilInstruction> rewritten = MsilRewriter.Rewrite(instructions, caller.Targets, __localCreator, counts, matched);

        NoteRewritten(caller, counts, matched);
        return rewritten;
    }

    /// <summary>What a method's original code does, as the table reads it.</summary>
    internal static IEnumerable<IlStep> ReadIl(MethodBase method) =>
        PatchUtilities.ReadInstructions(method).Select(instruction => new IlStep(instruction.OpCode, MsilRewriter.CalleeOf(instruction)));
}
