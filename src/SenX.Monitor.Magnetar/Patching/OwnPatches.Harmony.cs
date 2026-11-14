using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SenX.Monitor.Magnetar.Patching;

internal static class OwnPatches
{
    /// <summary>Removes what this Harmony id patched. Other plugins' patches on the same methods stay.</summary>
    public static void Remove(Harmony? harmony)
    {
        if (harmony == null)
            return;

        // A copy, because this changes what's being listed.
        foreach (MethodBase method in harmony.GetPatchedMethods().ToList())
        {
            // Unpatching rebuilds the method through other plugins' patches, and one failing mustn't leave the rest in.
            try
            {
                harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
            }
            catch (Exception exception)
            {
                MonitorLog.Error("Monitor could not take its patch out of " + method.Name + ".", exception);
            }
        }
    }
}
