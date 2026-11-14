using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SenX.Monitor.Api;

/// <summary>
/// What other Quasar plugins can read from Monitor and ask it to do. Get it from Quasar's services.
/// Monitor might not be installed, so see docs/PublicApi.md for how to cope with that.
///
/// This can't see who is asking, so it can't check permissions. A plugin's own page or endpoint has
/// to check CanView before showing anything and CanControlServers before asking for a run.
/// </summary>
public interface IMonitorApi
{
    /// <summary>Goes up with each addition. Check it before using something newer than version 1.</summary>
    int Version { get; }

    /// <summary>
    /// Every server Quasar has data on, whether it's connected or not. Everything else names a server by
    /// <see cref="MonitorServerInfo.Id"/>. History and saved runs work for any of them, and a run needs one that's connected.
    /// </summary>
    IReadOnlyList<MonitorServerInfo> Servers();

    /// <summary>A server's latest figures, or null if nothing has come in from it since Quasar started.</summary>
    MonitorLatest? Latest(string server);

    /// <summary>A server's saved runs, newest first.</summary>
    IReadOnlyList<MonitorSavedRun> SavedRuns(string server);

    /// <summary>Moves a server to one-second collection for a few seconds. Call it again to keep that going.</summary>
    void KeepFresh(string server);

    /// <summary>The tools for one server, the same ones a server plugin gets. Everything that reads history works for a server that isn't connected.</summary>
    IMonitorTools For(string server);

    /// <summary>
    /// Asks for lists of costs from a new run, from history or from a saved run. Every source gives the
    /// same reply, and a normal "no" is a reply, not an exception. Cancelling stops the wait, and a
    /// run that has started still finishes and is saved.
    /// </summary>
    Task<MonitorReply> RequestAsync(MonitorRequest request, CancellationToken cancellationToken);

    /// <summary>A run finished on some server. Raised on Monitor's own queue, so don't hold it up.</summary>
    event Action<MonitorRunFinished>? RunFinished;

    /// <summary>Something happened on a server: a save, a restart, a change of plugins or mods, or lag.</summary>
    event Action<MonitorEvent>? EventRecorded;

    /// <summary>Lag began or ended on a server.</summary>
    event Action<MonitorIncident>? IncidentChanged;
}

/// <summary>Where a reply's figures come from.</summary>
public enum MonitorSource
{
    /// <summary>Reads a past range. This measures nothing, so it adds nothing to frames, and it's good for trends.</summary>
    History,

    /// <summary>Measures now. Accurate, and it adds cost to the server while it runs.</summary>
    Run,

    /// <summary>Reads a run already finished.</summary>
    SavedRun,
}

/// <summary>A list of what's costing time.</summary>
public enum MonitorList
{
    Grids,
    BlockTypes,
    BlockDefinitions,
    Players,
    Factions,
    Scripts,
    SessionComponents,
    GameLogic,
    Callbacks,
    EntityTypes,
    Mods,
    Clusters,

    /// <summary>RPCs from clients, by the method they run.</summary>
    Rpcs,

    /// <summary>RPCs the server sent, biggest first. These rows have <see cref="MonitorRow.BytesPerSecond"/> and no time.</summary>
    RpcsSent,
}

public enum MonitorReplyState
{
    Finished,

    /// <summary>A normal "no". <see cref="MonitorReply.Message"/> says why, and <see cref="MonitorReply.RetryAfter"/> when to try again.</summary>
    Refused,

    Failed,
}

/// <summary>How far a reply can be trusted.</summary>
public enum MonitorAccuracy
{
    /// <summary>Every call was timed. This is what settles a lag cause.</summary>
    EveryCall,

    /// <summary>Background totals, from one group of work at a time. Good for trends, not for proving a cause.</summary>
    Background,
}

public enum MonitorEventKind
{
    Save,
    Restart,
    Change,
    Lag,

    /// <summary>A run began or detail changed, either of which changes what frames cost.</summary>
    Measuring,

    /// <summary>One long frame just after a character spawned, while the game loaded voxel files. Not an alert. Since version 3.</summary>
    JoinHitch,

    /// <summary>Something's time moved between the game thread and worker threads, and stayed there. Not an alert. Since version 4.</summary>
    ThreadJump,
}

/// <summary>Which threads a row's time went on. Since version 4.</summary>
public enum MonitorThread
{
    /// <summary>No time to go by, or a list where it means nothing, such as clusters.</summary>
    Unknown = 0,

    /// <summary>The game thread, which is the frame. Only this time can set off lag alerts.</summary>
    Main = 1,

    /// <summary>Worker threads. This costs the frame only while the game thread waits for them.</summary>
    Worker = 2,

    /// <summary>Both, each with at least <see cref="MonitorThreads.MinorPercent"/>% of the time.</summary>
    Mixed = 3,
}

/// <summary>How Monitor decides a <see cref="MonitorThread"/>, the same everywhere. Since version 4.</summary>
public static class MonitorThreads
{
    /// <summary>A side with less of the time than this doesn't count, so a few stray calls don't make something mixed.</summary>
    public const int MinorPercent = 5;

    public static MonitorThread Of(double mainMs, double workerMs)
    {
        double main = mainMs > 0d ? mainMs : 0d;
        double worker = workerMs > 0d ? workerMs : 0d;
        double total = main + worker;

        if (total <= 0d)
            return MonitorThread.Unknown;

        if (worker * 100d < total * MinorPercent)
            return MonitorThread.Main;

        if (main * 100d < total * MinorPercent)
            return MonitorThread.Worker;

        return MonitorThread.Mixed;
    }
}

/// <summary>What to ask for. A request covers one server, so several servers take one request each.</summary>
public sealed record MonitorRequest
{
    /// <summary>Which server, for <see cref="IMonitorApi"/>. The tools fill this in themselves, because they're for one server.</summary>
    public string Server { get; init; } = string.Empty;

    public MonitorSource Source { get; init; }

    /// <summary>The lists wanted. Empty means all of them.</summary>
    public IReadOnlyCollection<MonitorList> Lists { get; init; } = [];

    /// <summary>For a run: how long to measure, from 1 to 600 seconds.</summary>
    public int Seconds { get; init; } = 10;

    /// <summary>For a run: only this grid and its blocks. Zero means every grid. A run like this can't be shared.</summary>
    public long GridId { get; init; }

    /// <summary>For a run: only blocks owned by this player, by name, identity id or sid/STEAMID. A run like this can't be shared.</summary>
    public string? Player { get; init; }

    /// <summary>For a run: only blocks owned by this faction's members, by tag, name or id. A run like this can't be shared.</summary>
    public string? Faction { get; init; }

    /// <summary>For a run: only blocks whose class name contains this. A run like this can't be shared.</summary>
    public string? BlockType { get; init; }

    /// <summary>For a run: who asked, for the log and the saved run. Required.</summary>
    public string RequestedBy { get; init; } = string.Empty;

    /// <summary>For history: where the range starts, in UTC.</summary>
    public DateTime From { get; init; }

    /// <summary>For history: where the range ends, in UTC.</summary>
    public DateTime To { get; init; }

    /// <summary>For a saved run: which one. Null means the newest.</summary>
    public string? RunId { get; init; }
}

/// <summary>The one reply to every request.</summary>
public sealed record MonitorReply
{
    public MonitorReplyState State { get; init; }

    public string? Message { get; init; }

    /// <summary>For a refusal: how long to wait, when that's known.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public string Server { get; init; } = string.Empty;

    public MonitorSource Source { get; init; }

    public MonitorAccuracy Accuracy { get; init; }

    /// <summary>The exact time covered, in UTC.</summary>
    public DateTime From { get; init; }

    public DateTime To { get; init; }

    /// <summary>How the frame went over that time. Null unless <see cref="State"/> is finished.</summary>
    public MonitorFrame? Frame { get; init; }

    /// <summary>What the average frame went on, in ms, by category such as Physics or Network. Each ms is in one category only.</summary>
    public IReadOnlyDictionary<string, double> PartsMs { get; init; } = new Dictionary<string, double>();

    /// <summary>Details only a run has. Null for history.</summary>
    public MonitorRunDetails? Run { get; init; }

    /// <summary>One entry per list asked for.</summary>
    public IReadOnlyList<MonitorListReply> Lists { get; init; } = [];

    /// <summary>The slowest single calls. Empty for history.</summary>
    public IReadOnlyList<MonitorSlowCall> SlowCalls { get; init; } = [];

    /// <summary>Timings across the window. Empty for a run.</summary>
    public IReadOnlyList<MonitorPoint> Timeline { get; init; } = [];

    /// <summary>World figures across the window. Empty for a run.</summary>
    public IReadOnlyList<MonitorWorldPoint> World { get; init; } = [];

    /// <summary>What happened in the window. Empty for a run.</summary>
    public IReadOnlyList<MonitorEvent> Events { get; init; } = [];
}

/// <summary>Figures for a stretch of time, or the same for one moment. Times are ms.</summary>
public sealed record MonitorFrame(double SimSpeed, double AverageFrameMs, double WorstFrameMs, double AvailableMs);

public sealed record MonitorRunDetails(string? RunId, double Seconds, long Frames, double TimingMsPerFrame, double Coverage, bool EndedEarly, string RequestedBy)
{
    /// <summary>Why some of this run reads low, because another plugin rewrote calls Monitor times. Null if none did. Since version 2.</summary>
    public string? LostSites { get; init; }

    /// <summary>
    /// Game thread time per frame spent waiting for worker threads, which is the only way worker time costs the
    /// frame. It includes handing the work out, so it can read slightly high. Since version 4.
    /// </summary>
    public double MainWaitMs { get; init; }

    /// <summary>Worker thread time per frame in the game's parallel updates, summed across workers. Since version 4.</summary>
    public double ParallelWorkerMs { get; init; }
}

/// <summary>One list in a reply.</summary>
public sealed record MonitorListReply
{
    public MonitorList List { get; init; }

    /// <summary>False when this source can't answer for this list. An empty available list really was empty.</summary>
    public bool Available { get; init; }

    /// <summary>Why it isn't available.</summary>
    public string? Why { get; init; }

    /// <summary>
    /// True when <see cref="MonitorRow.MainMs"/> is per frame. Clusters are false, because a cluster's time
    /// is for one step with all physics threads to itself.
    /// </summary>
    public bool PerFrame { get; init; } = true;

    public IReadOnlyList<MonitorRow> Rows { get; init; } = [];
}

/// <summary>
/// One row of a list, the same from every source. <see cref="Id"/> is there for grids, players, factions and
/// scripts, whose ids outlast a restart, so a chart line can follow one of them. <see cref="SeenShare"/> is 1 for
/// a run. From history, it's the share of background totals this row was in the top 20 of, and its times are
/// averaged over only those. Rows are costliest first, so a cluster's index is its place in the list.
/// </summary>
public sealed record MonitorRow(string Name, long? Id, double MainMs, double WorkerMs, double PeakMs, double CallsPerFrame, double SeenShare)
{
    /// <summary>
    /// Which threads its time went on, worked out by Monitor with <see cref="MonitorThreads.Of"/> from <see cref="MainMs"/>
    /// and <see cref="WorkerMs"/>. Unknown for clusters, and for rows with no time. Since version 4.
    /// </summary>
    public MonitorThread Thread { get; init; }

    /// <summary>For rows that count traffic, across everyone who got it. Zero for the rest.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>The blocks on a grid, or on all of a player's or faction's grids. Zero for the rest, and for a grid that's gone.</summary>
    public long Blocks { get; init; }

    /// <summary>The grids a player or faction owns, going by who owns most of each, or the grids in a cluster. Zero for the rest.</summary>
    public long Grids { get; init; }

    /// <summary>How far across a cluster reaches. Zero for the rest.</summary>
    public double SizeKm { get; init; }

    /// <summary>A cluster's costliest grids. Empty for the rest.</summary>
    public IReadOnlyList<MonitorRow> Members { get; init; } = [];
}

public sealed record MonitorSlowCall(double Ms, string Kind, string Name, bool MainThread);

public sealed record MonitorPoint(DateTime Time, double SimSpeed, double FrameP50, double FrameP95, double FrameP99, double FrameMax, double AvailableMs)
{
    /// <summary>KB sent a second.</summary>
    public double SentKb { get; init; }

    /// <summary>KB received a second.</summary>
    public double ReceivedKb { get; init; }
}

public sealed record MonitorWorldPoint(DateTime Time, long Players, long Grids, long Blocks, long Pcu, double PingMs)
{
    /// <summary>Grids a concealment plugin has stopped. These are counted in <see cref="Grids"/> too.</summary>
    public long ConcealedGrids { get; init; }

    /// <summary>Blocks on those grids. These are counted in <see cref="Blocks"/> too.</summary>
    public long ConcealedBlocks { get; init; }

    /// <summary>Grids that can't move. These are counted in <see cref="Grids"/> too.</summary>
    public long StaticGrids { get; init; }

    /// <summary>Grids that can move. These are counted in <see cref="Grids"/> too.</summary>
    public long DynamicGrids => Grids - StaticGrids;

    public long Asteroids { get; init; }

    public long Planets { get; init; }

    /// <summary>
    /// The server's share of the CPUs it's allowed to use, from 0 to 100, since the census before. That's the whole
    /// machine unless the server is pinned to some cores or limited by a container. Zero on the first one.
    /// </summary>
    public double CpuPercent { get; init; }

    /// <summary>Memory the server process is using.</summary>
    public double MemoryMb { get; init; }

    /// <summary>Of that, what the garbage collector is holding.</summary>
    public double ManagedMemoryMb { get; init; }

    /// <summary>How long the server had been up at <see cref="Time"/>, counted from when the world finished loading.</summary>
    public TimeSpan Uptime { get; init; }
}

/// <summary>What's crossing the network, a second.</summary>
public sealed record MonitorNetwork(double SentKb, double ReceivedKb, double RpcsFromClients);

public sealed record MonitorServerInfo(string Id, string Name, string Cluster, bool Connected, DateTime LastSeen);

public sealed record MonitorSavedRun(string Id, DateTime Time, double Seconds, double SimSpeed, double AverageFrameMs);

/// <summary>A server's latest figures, up to 30 seconds old unless it's being kept fresh.</summary>
public sealed record MonitorLatest(DateTime Time, MonitorFrame Frame, double FrameP99, IReadOnlyDictionary<string, double> PartsMs, MonitorWorldPoint? World)
{
    public MonitorNetwork? Network { get; init; }
}

public sealed record MonitorEvent(string Server, DateTime Time, MonitorEventKind Kind, string Text);

public sealed record MonitorRunFinished(string Server, string RunId);

/// <summary>Lag beginning or ending. <see cref="Closed"/> is null while it's still going.</summary>
public sealed record MonitorIncident(string Server, string Id, DateTime Opened, DateTime? Closed, string Reason, double LowestSim);
