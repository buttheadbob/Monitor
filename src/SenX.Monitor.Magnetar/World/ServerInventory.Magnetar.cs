using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SenX.Monitor.Contracts;
using VRage.Plugins;

namespace SenX.Monitor.Magnetar.World;

/// <summary>Finding Magnetar's plugins, by the game's plugin interface.</summary>
internal static partial class ServerInventory
{
    // Scanning a game or framework assembly's types is slow and can't find a plugin.
    private static readonly string[] s_neverPlugins =
    {
        "System", "Microsoft", "mscorlib", "netstandard", "WindowsBase", "PresentationCore", "PresentationFramework",
        "VRage", "Sandbox", "SpaceEngineers", "Havok", "HavokWrapper", "Newtonsoft", "0Harmony", "Mono.", "protobuf",
        "Steamworks", "Magnetar", "PluginSdk", "Quasar",
    };

    /// <summary>Finds loaded plugins. This reads a lot of metadata, so it's kept off the game thread.</summary>
    public static void ReadPlugins()
    {
        try
        {
            var plugins = new List<RuntimeItem>();
            var types = new List<Type>();

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                AssemblyName name = assembly.GetName();

                if (name.Name == null || assembly.IsDynamic || NeverAPlugin(name.Name))
                    continue;

                List<Type> found = PluginsIn(assembly);

                if (found.Count == 0)
                    continue;

                string plugin = ModNames.PluginName(name.Name);
                plugins.Add(new RuntimeItem { Id = plugin, Name = plugin + " " + name.Version });

                // Timing Monitor's own Update would only measure the measuring.
                if (assembly != typeof(ServerInventory).Assembly)
                    types.AddRange(found);
            }

            s_plugins = plugins;
            s_pluginTypes = types;
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not list this server's plugins, so it won't mark changes to them.", exception);
        }
    }

    private static bool NeverAPlugin(string name)
    {
        foreach (string prefix in s_neverPlugins)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static List<Type> PluginsIn(Assembly assembly)
    {
        var plugins = new List<Type>();
        Type?[] types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // Some types need something that isn't here. The ones that loaded are still worth checking.
            types = exception.Types;
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or TypeLoadException or NotSupportedException)
        {
            return plugins;
        }

        foreach (Type? type in types)
        {
            if (type != null && !type.IsAbstract && !type.IsInterface && typeof(IPlugin).IsAssignableFrom(type))
                plugins.Add(type);
        }

        return plugins;
    }
}
