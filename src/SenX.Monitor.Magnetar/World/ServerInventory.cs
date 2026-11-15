using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Game.World;
using SenX.Monitor.Contracts;
using VRage.Game;

namespace SenX.Monitor.Magnetar.World;

/// <summary>
/// The plugins and mods this server started with, so Quasar can show when they changed. Neither can
/// change without a restart, so each is read once. Until it has been read, its list is null, which
/// isn't the same as an empty list: Quasar would take that for everything having been removed.
/// </summary>
internal static partial class ServerInventory
{
    private static volatile List<RuntimeItem>? s_plugins;
    private static volatile List<Type>? s_pluginTypes;
    private static volatile List<RuntimeItem>? s_mods;

    public static List<RuntimeItem>? Plugins => s_plugins;

    /// <summary>Every other plugin's class, for timing their Updates. Null until they've been found.</summary>
    public static List<Type>? PluginTypes => s_pluginTypes;

    public static List<RuntimeItem>? Mods => s_mods;

    /// <summary>Reads the session's mods, on the game thread. Returns false if they aren't loaded yet.</summary>
    public static bool ReadMods(MySession session)
    {
        try
        {
            List<MyObjectBuilder_Checkpoint.ModItem>? loaded = session.Mods;
            if (loaded == null)
                return false;

            var mods = new List<RuntimeItem>(loaded.Count);

            foreach (MyObjectBuilder_Checkpoint.ModItem mod in loaded)
            {
                string id = mod.PublishedFileId.ToString(CultureInfo.InvariantCulture);
                mods.Add(new RuntimeItem { Id = id, Name = string.IsNullOrEmpty(mod.FriendlyName) ? id : mod.FriendlyName });
            }

            s_mods = mods;
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not read this server's mods, so it won't mark changes to them.", exception);
        }

        return true;
    }
}
