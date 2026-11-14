using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SenX.Monitor.Magnetar.Patching;
using VRage.Game.Entity;

namespace SenX.Monitor.Verify;

/// <summary>
/// Proves on the real game code that Monitor sees a site being taken, and given back. A pretend plugin does what a
/// real one could: it swaps a call Monitor times for a wrapper of its own, after Monitor has patched that method.
/// </summary>
internal static class TakeoverCheck
{
    private const string OwnerId = "verify.other-plugin";
    private const string Caller = "Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator";
    private const string Method = "UpdateBeforeSimulation";

    private static readonly MethodInfo s_target = AccessTools.DeclaredMethod(typeof(MyEntity), Method, Type.EmptyTypes);

    public static bool Run()
    {
        var other = new Harmony(OwnerId);
        MethodBase caller = AccessTools.Method(AccessTools.TypeByName(Caller), Method);
        bool passed = Check("nothing is taken over to begin with", !Lost().Any());

        other.Patch(caller, transpiler: new HarmonyMethod(typeof(TakeoverCheck), nameof(Redirect)));

        passed &= Check("a site taken after Monitor patched is seen", Lost().Count > 0);
        passed &= Check("it says who took it", Lost().All(result => result.TakenBy.Contains(OwnerId)));
        passed &= Check("it's in the note run reports carry", PatchEngine.TakenOverNote()?.Contains(OwnerId) == true);
        passed &= Check("nothing else was disturbed", PatchEngine.Results.Count(result => result.State == SiteState.Ok) == PatchEngine.Results.Count - Lost().Count);
        passed &= Check("nothing is called left alone", PatchEngine.Results.All(result => result.State != SiteState.Refused));

        other.Unpatch(caller, HarmonyPatchType.All, OwnerId);

        passed &= Check("it comes back when the other plugin leaves", !Lost().Any());
        passed &= Check("the count isn't added up across rebuilds", PatchEngine.Results.All(result => result.Patched == result.Target.Expected));

        return passed;
    }

    private static List<SiteResult> Lost() => PatchEngine.Results.Where(result => result.State == SiteState.TakenOver).ToList();

    private static bool Check(string name, bool passed)
    {
        Console.WriteLine((passed ? "  ok      " : "  FAILED  ") + name);
        return passed;
    }

    private static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(s_target))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(TakeoverCheck), nameof(Wrapper));
            }

            yield return instruction;
        }
    }

    public static void Wrapper(MyEntity entity) => entity.UpdateBeforeSimulation();
}
