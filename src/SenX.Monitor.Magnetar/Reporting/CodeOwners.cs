using System;
using System.Collections.Concurrent;
using System.Text;
using SenX.Monitor.Magnetar.Hooks;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>
/// Says whose code a timing key is, for background totals. It runs on the low priority thread, so it
/// works from a type's name and the mod list, which don't change once the world has loaded, and it
/// remembers each answer, so a key is only worked out once. The game thread reads names from here,
/// so nothing takes a lock it could get stuck behind.
/// </summary>
internal static class CodeOwners
{
    private const long NoOwner = 0L;

    private static readonly ConcurrentDictionary<long, long> s_ownerOfKey = new();
    private static readonly ConcurrentDictionary<long, string> s_names = new();

    /// <summary>The mod or plugin a key belongs to, or zero for the game's own code.</summary>
    public static long OwnerOf(long key)
    {
        if (s_ownerOfKey.TryGetValue(key, out long known))
            return known;

        long owner = WorkOut(key, out string name);

        if (owner != NoOwner)
            s_names[owner] = name;

        s_ownerOfKey[key] = owner;
        return owner;
    }

    /// <summary>What an owner is called.</summary>
    public static string NameOf(long owner) => s_names.TryGetValue(owner, out string? name) ? name : "Unknown";

    private static long WorkOut(long key, out string name)
    {
        name = string.Empty;

        try
        {
            Type? type = KeyNames.TypeOf(key);
            if (type == null)
                return NoOwner;

            name = LagReport.CodeOwner(type);
            return name == "Game" ? NoOwner : IdOf(name);
        }
        catch (Exception exception)
        {
            // One odd type shouldn't stop the totals, and it only costs its own row.
            MonitorLog.Warning("Monitor could not tell whose code a type is: " + exception.Message);
            return NoOwner;
        }
    }

    // From the name, so a mod keeps one id however many of its types are seen.
    private static long IdOf(string name)
    {
        ulong hash = 14695981039346656037UL;

        foreach (byte value in Encoding.UTF8.GetBytes(name))
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        long id = (long)hash;
        return id == NoOwner ? 1L : id;
    }
}
