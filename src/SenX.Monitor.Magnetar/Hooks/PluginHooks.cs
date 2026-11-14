using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SenX.Monitor.Core;
using VRage.Plugins;

namespace SenX.Monitor.Magnetar.Hooks;

/// <summary>Called by Harmony around a plugin's Update. Public, because generated code calls these.</summary>
public static class PluginUpdateShims
{
    public static void Prefix(object __instance, out long __state)
    {
        byte gate = Gates.Read(Site.PluginUpdate);

        __state = gate == Gates.Closed || !Sampler.Hit(RuntimeHelpers.GetHashCode(__instance), Site.PluginUpdate, gate) ? 0L : Clock.Now();
    }

    public static void Postfix(long __state, object __instance)
    {
        if (__state == 0L)
            return;

        long ticks = Clock.Now() - __state;
        byte gate = Gates.Read(Site.PluginUpdate);

        if (gate != Gates.Closed)
            KeyedRecord.AddType(Site.PluginUpdate, __instance.GetType(), ticks, gate);
    }
}

/// <summary>
/// Times each plugin's own Update. Magnetar calls them from its loader, which isn't in the game's
/// files and so can't be checked offline like Monitor's other patches. These go on the plugins'
/// methods instead, and timing a call once a frame per plugin costs next to nothing.
/// </summary>
internal static class PluginHooks
{
    private static readonly HarmonyMethod s_prefix = new(AccessTools.Method(typeof(PluginUpdateShims), nameof(PluginUpdateShims.Prefix)));
    private static readonly HarmonyMethod s_postfix = new(AccessTools.Method(typeof(PluginUpdateShims), nameof(PluginUpdateShims.Postfix)));

    /// <summary>Hooks what it can, and returns how many. A plugin that won't take it only loses its own row.</summary>
    public static int Apply(Harmony harmony, IEnumerable<Type> plugins)
    {
        int hooked = 0;
        var done = new HashSet<MethodInfo>();

        foreach (Type plugin in plugins)
        {
            try
            {
                MethodInfo? update = UpdateOf(plugin);

                if (update == null || update.IsAbstract || !done.Add(update))
                    continue;

                harmony.Patch(update, s_prefix, s_postfix);
                hooked++;
            }
            catch (Exception exception)
            {
                MonitorLog.Warning("Monitor can't time " + plugin.Name + "'s Update: " + exception.Message);
            }
        }

        return hooked;
    }

    /// <summary>The method that really runs for a plugin's Update.</summary>
    public static MethodInfo? UpdateOf(Type plugin)
    {
        InterfaceMapping map = plugin.GetInterfaceMap(typeof(IPlugin));
        int index = Array.FindIndex(map.InterfaceMethods, method => method.Name == nameof(IPlugin.Update));

        return index < 0 ? null : map.TargetMethods[index];
    }
}
