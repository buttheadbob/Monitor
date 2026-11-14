using System.Globalization;

namespace SenX.Monitor.Services;

/// <summary>How the host's own state goes on disk.</summary>
internal static class StateFiles
{
    /// <summary>The ending of a file still being written.</summary>
    public const string Unfinished = ".writing";

    private const string EmptyId = "_none";
    private const uint HashStart = 2166136261U;
    private const uint HashStep = 16777619U;

    /// <summary>
    /// Writes a whole file so a crash leaves the old one or the new one, never half of each. A torn settings file
    /// would read as defaults, and a torn position would collect everything again and repeat old alerts. It's pushed
    /// to the disk before it takes the old one's place, though a power cut has only been reasoned about, not tried.
    /// </summary>
    public static void Write(string path, string text)
    {
        string writing = path + Unfinished;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);

        using (var stream = new FileStream(writing, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
            File.Replace(writing, path, null);
        else
            File.Move(writing, path);
    }

    /// <summary>
    /// A folder name for a server. Anything that isn't a letter or digit can't go in a path, and a short hash keeps
    /// names like EU-1 and EU_1 apart, because the two would otherwise share a folder and delete each other's history.
    /// An empty id must never mean the folder above, which holds every server's data.
    /// </summary>
    public static string FolderFor(string serverId)
    {
        if (serverId.Length == 0)
            return EmptyId;

        string plain = string.Concat(serverId.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

        if (plain == serverId)
            return plain;

        uint hash = HashStart;

        foreach (char c in serverId)
            hash = unchecked((hash ^ c) * HashStep);

        return plain + "-" + hash.ToString("x8", CultureInfo.InvariantCulture);
    }
}
