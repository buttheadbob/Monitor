using System.Reflection;
using System.Runtime.CompilerServices;
using NLog;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Patching;
using Torch;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SenX.Monitor.Magnetar
{
    /// <summary>Plugin logging, into Torch's log. Never called on a hot path.</summary>
    internal static class MonitorLog
    {
        private static readonly Logger s_log = LogManager.GetLogger("Monitor");

        public static void Info(string message) => s_log.Info(message);

        public static void Warning(string message) => s_log.Warn(message);

        public static void Error(string message, Exception exception) => s_log.Error(exception, message);
    }
}

namespace SenX.Monitor.Magnetar.Hooks
{
    /// <summary>Called by Torch around a plugin's Update. Public, because generated code calls these.</summary>
    public static class PluginUpdateShims
    {
        public static void Prefix(object __instance, ref long __local_started)
        {
            byte gate = Gates.Read(Site.PluginUpdate);

            __local_started = gate == Gates.Closed || !Sampler.Hit(RuntimeHelpers.GetHashCode(__instance), Site.PluginUpdate, gate) ? 0L : Clock.Now();
        }

        public static void Suffix(object __instance, ref long __local_started)
        {
            if (__local_started == 0L)
                return;

            long ticks = Clock.Now() - __local_started;
            byte gate = Gates.Read(Site.PluginUpdate);

            if (gate != Gates.Closed)
                KeyedRecord.AddType(Site.PluginUpdate, __instance.GetType(), ticks, gate);
        }
    }

    /// <summary>
    /// Times each plugin's own Update. Torch calls them one after another from its plugin manager, inside
    /// the game's own plugin update, and timing a call once a frame per plugin costs next to nothing.
    /// </summary>
    internal static class PluginHooks
    {
        private static readonly MethodInfo s_prefix = typeof(PluginUpdateShims).GetMethod(nameof(PluginUpdateShims.Prefix))!;
        private static readonly MethodInfo s_suffix = typeof(PluginUpdateShims).GetMethod(nameof(PluginUpdateShims.Suffix))!;
        private static readonly PostTranspiler s_keepLeaves = new(typeof(PluginHooks), nameof(KeepLeavesFirst), nameof(KeepLeaves));

        /// <summary>Hooks what it can, one at a time, and returns how many. A plugin that won't take it only loses its own row.</summary>
        public static int Apply(PatchContext context, PatchManager manager, IEnumerable<Type> plugins)
        {
            int hooked = 0;
            var done = new HashSet<MethodInfo>();

            foreach (Type plugin in plugins)
            {
                MethodInfo? update = null;

                try
                {
                    update = UpdateOf(plugin);

                    if (update == null || update.IsAbstract || !done.Add(update))
                        continue;

                    MethodRewritePattern all = manager.GetPattern(update);

                    if (WhyNot(update, all) is { } problem)
                    {
                        MonitorLog.Warning("Monitor can't time " + plugin.Name + "'s Update, because " + problem + ".");
                        continue;
                    }

                    MethodRewritePattern pattern = context.GetPattern(update);
                    pattern.Prefixes.Add(s_prefix);
                    pattern.Suffixes.Add(s_suffix);

                    if (TorchIl.LosesALeave(update))
                        s_keepLeaves.Add(pattern, all);

                    manager.Commit();
                    hooked++;
                }
                catch (Exception exception)
                {
                    MonitorLog.Warning("Monitor can't time " + plugin.Name + "'s Update: " + exception.Message);

                    if (update != null)
                        Unhook(context, manager, update);
                }
            }

            return hooked;
        }

        private static string? WhyNot(MethodInfo update, MethodRewritePattern all)
        {
            if (TorchIl.HasSiblingCatches(update))
                return "Torch rebuilding it would change how its catches work";

            bool safe = TorchIl.TakesAnotherEntry(all.Prefixes) && TorchIl.TakesAnotherEntry(all.Suffixes) && TorchIl.TakesAnotherEntry(all.PostTranspilers);

            return safe ? null : "another plugin's patch on it has a negative priority";
        }

        [PatchPriority(int.MaxValue)]
        private static IEnumerable<MsilInstruction> KeepLeavesFirst(IEnumerable<MsilInstruction> instructions) => KeepLeaves(instructions);

        private static IEnumerable<MsilInstruction> KeepLeaves(IEnumerable<MsilInstruction> instructions) => TorchIl.KeepLeaves(instructions.ToList());

        // Left waiting, these would go in by themselves the next time anything commits.
        private static void Unhook(PatchContext context, PatchManager manager, MethodInfo update)
        {
            try
            {
                MethodRewritePattern pattern = context.GetPattern(update);
                pattern.Prefixes.Remove(s_prefix);
                pattern.Suffixes.Remove(s_suffix);
                s_keepLeaves.Remove(pattern);
                PatchEngine.CommitRemaining();
            }
            catch (Exception exception)
            {
                MonitorLog.Warning("Monitor could not take a hook back out: " + exception.Message);
            }
        }

        /// <summary>The method that really runs for a plugin's Update, or null if it never wrote one.</summary>
        public static MethodInfo? UpdateOf(Type plugin)
        {
            InterfaceMapping map = plugin.GetInterfaceMap(typeof(ITorchPlugin));
            int index = Array.FindIndex(map.InterfaceMethods, method => method.Name == nameof(ITorchPlugin.Update));

            if (index < 0)
                return null;

            MethodInfo update = map.TargetMethods[index];
            return update.DeclaringType == typeof(TorchPluginBase) ? null : update;
        }
    }
}

namespace SenX.Monitor.Magnetar.World
{
    /// <summary>Finding Torch's plugins, which its plugin manager simply lists.</summary>
    internal static partial class ServerInventory
    {
        public static void ReadPlugins()
        {
            try
            {
                var plugins = new List<RuntimeItem>();
                var types = new List<Type>();

                foreach (ITorchPlugin plugin in TorchBase.Instance.Managers.GetManager<IPluginManager>().Plugins.Values)
                {
                    plugins.Add(new RuntimeItem { Id = plugin.Id.ToString(), Name = plugin.Name + " " + plugin.Version });

                    // Timing Monitor's own Update would only measure the measuring.
                    if (plugin.GetType().Assembly != typeof(ServerInventory).Assembly)
                        types.Add(plugin.GetType());
                }

                s_plugins = plugins;
                s_pluginTypes = types;
            }
            catch (Exception exception)
            {
                MonitorLog.Error("Monitor could not list this server's plugins, so it won't mark changes to them.", exception);
            }
        }
    }
}
