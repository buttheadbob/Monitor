namespace SenX.Monitor.Contracts;

/// <summary>
/// How a part of a mod is named in history. Rows are stored by name, so the part's name carries its
/// mod's, which keeps it attached across restarts when its type keys change.
/// </summary>
public static class ModNames
{
    public const string Separator = " > ";

    private const int RandomLength = 12;
    private const int HashLength = 8;

    public static string Part(string mod, string part) => mod + Separator + part;

    /// <summary>The start of every part of a mod.</summary>
    public static string PartsOf(string mod) => mod + Separator;

    /// <summary>A part's own name, without its mod. From the last separator, because a mod's title can hold one.</summary>
    public static string PartName(string fullName)
    {
        int at = fullName.LastIndexOf(Separator, System.StringComparison.Ordinal);
        return at < 0 ? fullName : fullName.Substring(at + Separator.Length);
    }

    /// <summary>
    /// A plugin's name without what its loader added. Magnetar names each compile "Name-1a2b3c4d_x8k2m1qz.a1b", the end of which
    /// is random, so the plain assembly name would be a new plugin to history every time one is rebuilt.
    /// </summary>
    public static string PluginName(string assembly)
    {
        int random = assembly.Length - RandomLength - 1;

        if (random <= 0 || assembly[random] != '_' || assembly[random + 9] != '.')
            return assembly;

        int hash = random - HashLength - 1;
        bool hashed = hash > 0 && assembly[hash] == '-' && IsHex(assembly, hash + 1, HashLength);

        return assembly.Substring(0, hashed ? hash : random);
    }

    private static bool IsHex(string text, int start, int length)
    {
        for (int at = start; at < start + length; at++)
        {
            if (!System.Uri.IsHexDigit(text[at]))
                return false;
        }

        return true;
    }
}
