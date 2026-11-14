using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SenX.Monitor.Api;

/// <summary>
/// The tools for one server, the same on every host. A server plugin gets these from
/// <see cref="MonitorServerApi.Current"/>, and a Quasar plugin from <see cref="IMonitorApi.For"/>.
/// A normal "no" is an answer, not an exception, and times are in UTC. Never wait on a task from
/// here on the game thread, because some answers need the game thread to finish.
/// </summary>
public interface IMonitorTools
{
    /// <summary>Which server these are for. A server plugin only ever knows its own, so this is empty on Magnetar and <c>local</c> on Torch.</summary>
    string Server { get; }

    /// <summary>
    /// The latest frame and world figures, or null until there are some. This reads memory, doesn't wait for
    /// anything being written, and doesn't reach the server, so it can be called as often as you like, on any thread.
    /// The frame figures are a second or two old on a server plugin and up to 30 seconds on Quasar, and the world
    /// figures are from the last census.
    /// </summary>
    MonitorLatest? Latest();

    /// <summary>
    /// Whether the server is online, when it started, and how far back history reaches. This doesn't wait either, apart
    /// from the first time for a server that hasn't sent anything since Quasar started, which has to be looked up.
    /// </summary>
    MonitorStatus Status();

    /// <summary>The same request and reply as <see cref="IMonitorApi.RequestAsync"/>, for this server.</summary>
    Task<MonitorReply> RequestAsync(MonitorRequest request, CancellationToken cancellationToken);

    /// <summary>Who's online, with what each one is costing the network, as of the last census, about 30 seconds ago. Empty for a server that's gone.</summary>
    Task<IReadOnlyList<MonitorPlayerNow>> PlayersAsync(CancellationToken cancellationToken);

    /// <summary>Each faction's size, as of the last census. Empty for a server that's gone.</summary>
    Task<IReadOnlyList<MonitorFactionNow>> FactionsAsync(CancellationToken cancellationToken);

    /// <summary>The costliest rows of a list, from the latest whole background total. These are for trends.</summary>
    Task<IReadOnlyList<MonitorRow>> TopAsync(MonitorList list, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Background totals across a range, oldest first, each with the server's figures from that moment. Monitor
    /// takes these by itself, so asking measures nothing. Rows keep their ids from one sample to the next, so a
    /// chart line can follow one grid. See <see cref="MonitorSample"/>. Empty lists means every list history keeps.
    /// </summary>
    /// <param name="top">How many rows of each list, from 1 to 20.</param>
    Task<IReadOnlyList<MonitorSample>> SamplesAsync(DateTime from, DateTime to, IReadOnlyCollection<MonitorList> lists, int top, CancellationToken cancellationToken);

    /// <summary>Frame timings, world figures and events across a range. Reads only, so it measures nothing.</summary>
    Task<MonitorHistory> HistoryAsync(DateTime from, DateTime to, CancellationToken cancellationToken);

    /// <summary>What was costing time across a range. Empty lists means all of them.</summary>
    Task<MonitorReply> CostsAsync(DateTime from, DateTime to, IReadOnlyCollection<MonitorList> lists, CancellationToken cancellationToken);

    /// <summary>A list's rows of this name over time, such as one mod. Two grids can share a name, and they'd both come back, so use the id for those.</summary>
    Task<IReadOnlyList<MonitorCostPoint>> CostHistoryAsync(MonitorList list, string name, DateTime from, DateTime to, CancellationToken cancellationToken);

    /// <summary>One row of a list over time, by <see cref="MonitorRow.Id"/>, such as one grid.</summary>
    Task<IReadOnlyList<MonitorCostPoint>> CostHistoryAsync(MonitorList list, long id, DateTime from, DateTime to, CancellationToken cancellationToken);

    /// <summary>
    /// One chart as a PNG file's bytes, to attach or save however you like. Monitor doesn't send it anywhere. It's
    /// the same data as Monitor's own charts, with times in UTC. Null means there's nothing to draw for that range,
    /// and a server plugin on Magnetar always gets null, because only Quasar and Torch hold the history. Since
    /// <see cref="IMonitorApi.Version"/> 2. Images are drawn one at a time, off the caller's thread.
    /// </summary>
    /// <param name="width">In pixels, from 320 to 1920.</param>
    /// <param name="height">In pixels, from 200 to 1080.</param>
    Task<byte[]?> ChartImageAsync(MonitorChart chart, DateTime from, DateTime to, int width, int height, CancellationToken cancellationToken);

    /// <summary>
    /// What happened across a range, newest first: saves, lag and runs. Through Quasar or on Torch, also restarts and
    /// changes of plugins or mods, which a Magnetar server plugin can't see because it restarts with the server.
    /// </summary>
    Task<IReadOnlyList<MonitorEvent>> EventsAsync(DateTime from, DateTime to, CancellationToken cancellationToken);

    /// <summary>Two stretches of history side by side, such as before and after a change. Empty lists means all of them.</summary>
    Task<MonitorComparison> CompareAsync(MonitorRange before, MonitorRange after, IReadOnlyCollection<MonitorList> lists, CancellationToken cancellationToken);

    /// <summary>
    /// Measures now, with every call timed, and answers when it's done. This settles a lag cause, and it costs
    /// the server while it runs, so plugins get one a minute. Cancelling only stops the wait.
    /// </summary>
    Task<MonitorReply> MeasureAsync(MonitorRunOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// The run going now, whoever started it, or null if there isn't one. Quasar only hears of a chat command's run or
    /// a lag trip's at its next pull, so through Quasar a short one can come and go unseen. Who asked is only known for
    /// a run from the API or Monitor's page.
    /// </summary>
    MonitorRunProgress? CurrentRun();

    /// <summary>Finished runs that can still be read, newest first. A Magnetar server plugin only has the latest.</summary>
    Task<IReadOnlyList<MonitorSavedRun>> SavedRunsAsync(CancellationToken cancellationToken);

    /// <summary>A finished run, by id or null for the newest. Empty lists means all of them.</summary>
    Task<MonitorReply> SavedRunAsync(string? id, IReadOnlyCollection<MonitorList> lists, CancellationToken cancellationToken);

    /// <summary>Lag noticed in the history kept, newest first. Empty on a Magnetar server plugin, because Quasar keeps these.</summary>
    Task<IReadOnlyList<MonitorIncident>> IncidentsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Methods that can be looked inside, by part of their name. A search takes a game frame, so they're spaced
    /// 10 seconds apart, and asking again sooner waits its turn. Empty if it can't be asked.
    /// </summary>
    Task<IReadOnlyList<MonitorMethod>> FindMethodsAsync(string text, CancellationToken cancellationToken);

    /// <summary>
    /// Times what a method calls, and answers when it's done. This rebuilds the method while the server runs, so
    /// it goes one at a time, plugins get one a minute, and it comes out by itself. <paramref name="seconds"/> is 1 to 120.
    /// Only the latest result is kept, so if a newer one finishes first, this comes back failed and says so.
    /// </summary>
    Task<MonitorInside> LookInsideAsync(string methodId, int seconds, string requestedBy, CancellationToken cancellationToken);

    /// <summary>Which methods other plugins have patched, and how. Empty if it can't be asked.</summary>
    Task<IReadOnlyList<MonitorPatchedMethod>> PatchesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Splits what up to 8 patched methods cost into plugin prefixes, the original and plugin postfixes. Methods are named
    /// as <see cref="MonitorPatchedMethod.Method"/> names them. The same limits apply as for looking inside.
    /// </summary>
    Task<MonitorAudit> AuditAsync(IReadOnlyCollection<string> methods, int seconds, string requestedBy, CancellationToken cancellationToken);

    /// <summary>
    /// Monitor kept a new sample: every 2 minutes, and every minute from when sim speed drops below 0.7 until it's been
    /// back above that for 5 minutes. These are the same samples <see cref="SamplesAsync"/> reads, with every list and all
    /// 20 rows. Raised off the game thread, and it only costs anything while someone's listening.
    /// </summary>
    event Action<MonitorSample>? SampleRecorded;

    /// <summary>
    /// A run finished, whoever started it, a lag trip included. A player's <c>!monitor test</c> isn't one of these,
    /// because it only times their own grid. Raised off the game thread.
    /// </summary>
    event Action<MonitorRunFinished>? RunFinished;

    /// <summary>
    /// Something happened: a save, lag, or a run starting. Through Quasar or on Torch, also a restart or a change of
    /// plugins or mods, which a server plugin can't see because it restarts with the server. Raised off the game thread.
    /// </summary>
    event Action<MonitorEvent>? EventRecorded;

    /// <summary>Lag began or ended. Never raised on a Magnetar server plugin, because Quasar keeps incidents.</summary>
    event Action<MonitorIncident>? IncidentChanged;
}

/// <summary>A stretch of time, in UTC.</summary>
public sealed record MonitorRange(DateTime From, DateTime To);

/// <summary>
/// How a server stands. <see cref="Connected"/> is whether it's online now, and <see cref="Started"/> is when the
/// world last finished loading, so uptime is the time since then. <see cref="HistoryReach"/> is how far back history
/// goes from here. <see cref="LagSince"/> is null on a Magnetar server plugin, because Quasar keeps incidents.
/// </summary>
public sealed record MonitorStatus(bool Connected, DateTime? Started, DateTime? LastData, bool RunGoing, DateTime? LagSince, TimeSpan HistoryReach);

public sealed record MonitorPlayerNow(string Name, long IdentityId, string Faction, double PingMs, double SentKbPerSecond, double ReceivedKbPerSecond, double RpcsPerSecond, double QueuedKb, long FailedSends);

/// <summary>A faction's size. <see cref="Id"/> is -1 for the row that holds grids in no faction.</summary>
public sealed record MonitorFactionNow(string Name, long Id, long OnlineMembers, long Grids, long Blocks, long Pcu);

/// <summary>
/// One background total, with the server's figures from that moment. These are for trends, because each
/// one measures a group of work at a time. <see cref="MonitorReply.Accuracy"/> says the same thing about a reply.
/// A run settles a lag cause. Clusters aren't in these, because timing them changes how physics is
/// scheduled, so that list comes back unavailable and only a run has them.
/// </summary>
/// <param name="Server">Which server. Empty on a server plugin.</param>
/// <param name="Time">When the total was taken.</param>
/// <param name="Frame">How frames went since the sample before. Null if no timings cover that stretch.</param>
/// <param name="PartsMs">What the average frame went on across that stretch, in ms.</param>
/// <param name="World">The last census before <paramref name="Time"/>, which has players, CPU, memory, grids, blocks and uptime. Null if there wasn't one.</param>
/// <param name="Lists">One entry per list asked for.</param>
public sealed record MonitorSample(string Server, DateTime Time, MonitorFrame? Frame, IReadOnlyDictionary<string, double> PartsMs, MonitorWorldPoint? World, IReadOnlyList<MonitorListReply> Lists)
{
    /// <summary>How much time that stretch really covers, which leaves out the server being away or paused.</summary>
    public double Seconds { get; init; }

    /// <summary>The frames simulated across it.</summary>
    public long Frames { get; init; }
}

/// <summary>A range's figures with no lists. <see cref="Frame"/> is null if nothing was collected across it.</summary>
public sealed record MonitorHistory(MonitorRange Range, MonitorFrame? Frame, IReadOnlyList<MonitorPoint> Timeline, IReadOnlyList<MonitorWorldPoint> World, IReadOnlyList<MonitorEvent> Events);

/// <summary>One background total for one row. A row outside a total's top rows has no point there.</summary>
public sealed record MonitorCostPoint(DateTime Time, double MainMs, double WorkerMs, double PeakMs, double CallsPerFrame)
{
    /// <summary>Which threads its time went on at this point. Since version 4.</summary>
    public MonitorThread Thread => MonitorThreads.Of(MainMs, WorkerMs);

    /// <summary>For rows that count traffic. Zero for the rest.</summary>
    public double BytesPerSecond { get; init; }
}

/// <summary>One row in two stretches. A null time means it wasn't in that stretch's top rows, not that it cost nothing. Rows join on <see cref="Id"/> where they have one.</summary>
public sealed record MonitorComparedRow(string Name, long? Id, double? BeforeMs, double? AfterMs)
{
    public double? ChangeMs => BeforeMs.HasValue && AfterMs.HasValue ? AfterMs - BeforeMs : null;
}

public sealed record MonitorComparedList(MonitorList List, IReadOnlyList<MonitorComparedRow> Rows);

public sealed record MonitorComparison(MonitorFrame? Before, MonitorFrame? After, IReadOnlyList<MonitorComparedList> Lists);

/// <summary>What to measure, and who asked.</summary>
public sealed record MonitorRunOptions
{
    /// <summary>Who asked, for the log and the saved run. Required.</summary>
    public string RequestedBy { get; init; } = string.Empty;

    /// <summary>How long to measure, from 1 to 600 seconds.</summary>
    public int Seconds { get; init; } = 10;

    /// <summary>The lists wanted. Empty means all of them, which includes timing physics clusters.</summary>
    public IReadOnlyCollection<MonitorList> Lists { get; init; } = [];

    /// <summary>Only this grid and its blocks. Zero means every grid.</summary>
    public long GridId { get; init; }

    /// <summary>Only blocks owned by this player, by name, identity id or sid/STEAMID.</summary>
    public string? Player { get; init; }

    /// <summary>Only blocks owned by this faction's members, by tag, name or id.</summary>
    public string? Faction { get; init; }

    /// <summary>Only blocks whose class name contains this.</summary>
    public string? BlockType { get; init; }
}

/// <summary>A run going now. <see cref="SecondsLeft"/> is of the measuring, and totalling it takes a little longer.</summary>
public sealed record MonitorRunProgress(string RequestedBy, int SecondsLeft);

/// <summary>A method that can be looked inside. <see cref="Owner"/> says whose code it is.</summary>
public sealed record MonitorMethod(string Id, string Name, string Owner);

/// <summary>One call a method makes. <see cref="Id"/> is empty for one with no code of its own to look inside.</summary>
public sealed record MonitorInsideRow(string Id, string Name, string Owner, double CallsPerFrame, double MainMs, double WorkerMs, double PeakMs)
{
    /// <summary>Which threads its time went on. Since version 4.</summary>
    public MonitorThread Thread => MonitorThreads.Of(MainMs, WorkerMs);
}

/// <summary>What a method was found to call. Times are per frame, apart from <see cref="PeakMs"/>.</summary>
public sealed record MonitorInside
{
    public MonitorReplyState State { get; init; }

    public string? Message { get; init; }

    public string Method { get; init; } = string.Empty;

    public string Owner { get; init; } = string.Empty;

    public int Seconds { get; init; }

    public double CallsPerFrame { get; init; }

    public double MainMs { get; init; }

    public double WorkerMs { get; init; }

    /// <summary>Main thread time not in anything it called.</summary>
    public double SelfMs { get; init; }

    public double PeakMs { get; init; }

    public double TimingMsPerFrame { get; init; }

    /// <summary>It stopped early, because timing this was costing the server too much.</summary>
    public bool EndedEarly { get; init; }

    /// <summary>Timing added enough that these figures read high.</summary>
    public bool Inflated { get; init; }

    public IReadOnlyList<MonitorInsideRow> Rows { get; init; } = [];
}

public sealed record MonitorPatchEntry(string Kind, string Plugin, string Patch, int Priority);

public sealed record MonitorPatchedMethod(string Method, IReadOnlyList<MonitorPatchEntry> Patches);

/// <summary>One audited method. <see cref="Problem"/> says why it couldn't be, if it couldn't.</summary>
public sealed record MonitorAuditRow(string Method, IReadOnlyList<string> Plugins, double CallsPerFrame, double PrefixMs, double OriginalMs, double PostfixMs, bool Transpiled, string? Problem);

public sealed record MonitorAudit
{
    public MonitorReplyState State { get; init; }

    public string? Message { get; init; }

    public int Seconds { get; init; }

    public IReadOnlyList<MonitorAuditRow> Rows { get; init; } = [];
}
