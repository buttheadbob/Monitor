using SenX.Monitor.Api;

namespace SenX.Monitor.Contracts;

/// <summary>Short words for which threads something ran on, the same in chat, reports and both UIs.</summary>
public static class ThreadTags
{
    /// <summary>Explains a run's main-wait figure, for its tooltip.</summary>
    public const string MainWaitTip = "Game thread time spent waiting for worker threads, which is the only way their time costs the frame. It includes handing the work out, so it can read slightly high.";

    /// <summary>Explains a run's parallel work figure, for its tooltip.</summary>
    public const string ParallelWorkTip = "Worker thread time in the game's parallel updates, summed across workers. It isn't frame time.";

    /// <summary>The column header for <see cref="Tag(double, double)"/> in chat tables.</summary>
    public const string Header = "On";

    /// <summary>Explains the column, for the note under a chat table.</summary>
    public const string Note = "On is M for the game thread, W for workers, M+W for both, and - where no time was recorded.";

    /// <summary>M, W or M+W, or a dash when there's no time to go by.</summary>
    public static string Tag(double main, double worker) => Tag(MonitorThreads.Of(main, worker));

    public static string Tag(MonitorThread thread) => thread switch
    {
        MonitorThread.Main => "M",
        MonitorThread.Worker => "W",
        MonitorThread.Mixed => "M+W",
        _ => "-",
    };

    /// <summary>In words, for a jump's text.</summary>
    public static string Words(MonitorThread thread) => thread switch
    {
        MonitorThread.Main => "the game thread",
        MonitorThread.Worker => "worker threads",
        MonitorThread.Mixed => "both the game thread and workers",
        _ => "no thread",
    };

    /// <summary>What a thread jump says, such as "Grid Miner moved from worker threads to the game thread".</summary>
    public static string JumpText(HistoryDimension list, string name, MonitorThread from, MonitorThread to) =>
        ListWord(list) + " " + name + " moved from " + Words(from) + " to " + Words(to);

    /// <summary>What the row says when a total found more jumps than it lists.</summary>
    public static string NotListedText(uint count) =>
        count.ToString(System.Globalization.CultureInfo.InvariantCulture) + (count == 1U ? " more thread change" : " more thread changes") + " not listed";

    private static string ListWord(HistoryDimension list) => list switch
    {
        HistoryDimension.Grids => "Grid",
        HistoryDimension.BlockTypes => "Block type",
        HistoryDimension.Definitions => "Block",
        HistoryDimension.EntityTypes => "Entity type",
        HistoryDimension.Session => "Session component",
        HistoryDimension.Logic => "Game logic",
        HistoryDimension.Callbacks => "Callback",
        HistoryDimension.Scripts => "Script",
        HistoryDimension.Rpcs => "RPC",
        HistoryDimension.Mods => "Mod",
        HistoryDimension.ModParts => "Mod part",
        _ => "Something",
    };

    /// <summary>The game thread's share of the time, for a bar. Zero with no time.</summary>
    public static double MainPercent(double main, double worker)
    {
        double mainPart = main > 0d ? main : 0d;
        double total = mainPart + (worker > 0d ? worker : 0d);
        return total > 0d ? mainPart / total * 100d : 0d;
    }

    /// <summary>One word, for a column in the UIs.</summary>
    public static string Name(MonitorThread thread) => thread switch
    {
        MonitorThread.Main => "Main",
        MonitorThread.Worker => "Worker",
        MonitorThread.Mixed => "Mixed",
        _ => string.Empty,
    };
}
