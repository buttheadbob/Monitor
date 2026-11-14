namespace SenX.Monitor.Core;

/// <summary>Which threads something's time went on. Numbers match the public API's <c>MonitorThread</c>.</summary>
public enum ThreadUse : uint
{
    /// <summary>No time to go by.</summary>
    None = 0,

    /// <summary>The game thread, which is the frame.</summary>
    Main = 1,

    /// <summary>Worker threads, which cost the frame only while the game thread waits for them.</summary>
    Worker = 2,

    /// <summary>Both, each with at least <see cref="ThreadUses.MinorPercent"/>% of the time.</summary>
    Mixed = 3,
}

/// <summary>Works out a <see cref="ThreadUse"/> from main and worker time. The public API has the same rule.</summary>
public static class ThreadUses
{
    /// <summary>A side with less of the time than this doesn't count, so a few stray calls don't make something mixed.</summary>
    public const int MinorPercent = 5;

    public static ThreadUse Of(long main, long worker)
    {
        if (main < 0L)
            main = 0L;

        if (worker < 0L)
            worker = 0L;

        long total = main + worker;

        if (total <= 0L)
            return ThreadUse.None;

        if (worker * 100L < total * MinorPercent)
            return ThreadUse.Main;

        if (main * 100L < total * MinorPercent)
            return ThreadUse.Worker;

        return ThreadUse.Mixed;
    }
}
