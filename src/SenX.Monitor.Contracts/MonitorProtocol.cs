namespace SenX.Monitor.Contracts;

/// <summary>Names shared by the Magnetar companion and the Quasar UI.</summary>
public static class MonitorProtocol
{
    public const string PluginId = "senx.monitor";

    public const int SchemaVersion = 1;

    public const string RuntimeInfoOperation = "runtime.info";

    /// <summary>Asks for history since a point. The answer is a byte array: see <see cref="HistoryReader"/>.</summary>
    public const string HistoryPullOperation = "history.pull";

    /// <summary>Starts a measuring run: see <see cref="RunStartRequest"/>.</summary>
    public const string RunStartOperation = "run.start";

    /// <summary>Asks how a run went: see <see cref="RunResultRequest"/>.</summary>
    public const string RunResultOperation = "run.result";

    /// <summary>Asks for the evidence from a lag trip: see <see cref="IncidentResultRequest"/>.</summary>
    public const string IncidentResultOperation = "incident.result";

    /// <summary>Sets what counts as lag: see <see cref="TriggerSettings"/>.</summary>
    public const string TriggerSetOperation = "trigger.set";

    /// <summary>Lists what other plugins patched: see <see cref="PatchListRequest"/>.</summary>
    public const string PatchListOperation = "patches.list";

    /// <summary>Starts measuring what patches cost: see <see cref="AuditStartRequest"/>.</summary>
    public const string AuditStartOperation = "audit.start";

    /// <summary>Asks how the last audit went: see <see cref="AuditResultRequest"/>.</summary>
    public const string AuditResultOperation = "audit.result";

    /// <summary>Lists methods to look inside: see <see cref="DrillRootsRequest"/>.</summary>
    public const string DrillRootsOperation = "drill.roots";

    /// <summary>Times what a method calls: see <see cref="DrillStartRequest"/>.</summary>
    public const string DrillStartOperation = "drill.start";

    /// <summary>Asks how the last drill-down went: see <see cref="DrillResultRequest"/>.</summary>
    public const string DrillResultOperation = "drill.result";

    /// <summary>Turns ids from history into names: see <see cref="IdentityRequest"/>.</summary>
    public const string IdentityLookupOperation = "identity.lookup";

    /// <summary>The most names one lookup answers, so a long list can't cost one game frame much.</summary>
    public const int MaxIdentityQueries = 256;
}

/// <summary>One id to name. <see cref="Representative"/> comes from the history row, and names types.</summary>
public sealed class IdentityQuery
{
    public HistoryDimension Kind { get; set; }

    public long Key { get; set; }

    public long Representative { get; set; }
}

public sealed class IdentityRequest
{
    public System.Collections.Generic.List<IdentityQuery> Queries { get; set; } = new System.Collections.Generic.List<IdentityQuery>();
}

public sealed class IdentityAnswer
{
    public HistoryDimension Kind { get; set; }

    public long Key { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class IdentityResponse
{
    public System.Collections.Generic.List<IdentityAnswer> Answers { get; set; } = new System.Collections.Generic.List<IdentityAnswer>();
}

public sealed class RunStartRequest
{
    /// <summary>Who asked, for the log and the saved run.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    public int Seconds { get; set; } = 10;

    /// <summary>Also times physics clusters. This changes how physics is scheduled during the run.</summary>
    public bool Clusters { get; set; } = true;

    /// <summary>Only this grid and its blocks. Zero means every grid.</summary>
    public long GridId { get; set; }

    /// <summary>Only blocks this player owns, by name, identity id or sid/STEAMID.</summary>
    public string? Player { get; set; }

    /// <summary>Only blocks this faction's members own, by tag, name or id.</summary>
    public string? Faction { get; set; }

    /// <summary>Only blocks whose class name contains this.</summary>
    public string? BlockType { get; set; }

    /// <summary>True when this limits what's counted, so a run like it can't serve anyone else.</summary>
    public bool Narrows => GridId != 0L || !string.IsNullOrWhiteSpace(Player) || !string.IsNullOrWhiteSpace(Faction) || !string.IsNullOrWhiteSpace(BlockType);
}

public sealed class RunStartResponse
{
    public bool Started { get; set; }

    public long RunId { get; set; }

    /// <summary>Why it didn't start, or why clusters won't be timed.</summary>
    public string? Message { get; set; }

    /// <summary>When another run is in the way, how long it has left to measure.</summary>
    public int RetrySeconds { get; set; }
}

public sealed class RunResultRequest
{
    public long RunId { get; set; }
}

public static class RunState
{
    public const string Unknown = "unknown";
    public const string Running = "running";
    public const string Finished = "finished";
    public const string Failed = "failed";
}

/// <summary>The names of the lists in a run's rows.</summary>
public static class RunLists
{
    /// <summary>Who a run that Monitor started by itself says asked for it.</summary>
    public const string LagTrip = "Monitor, because of lag";

    public const string Grids = "grids";
    public const string BlockTypes = "blocktypes";
    public const string Definitions = "blocks";
    public const string Players = "players";
    public const string Factions = "factions";
    public const string Scripts = "scripts";
    public const string Session = "session";
    public const string Logic = "logic";
    public const string Callbacks = "callbacks";
    public const string Rpcs = "rpcs";
    public const string RpcsSent = "rpcssent";
    public const string Entities = "entities";
    public const string Mods = "mods";
    public const string Clusters = "clusters";
}

/// <summary>One row of a run. A cluster's main time is per step alone, not per frame.</summary>
public sealed class RunRow
{
    public string List { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long Key { get; set; }

    public double MainMsPerFrame { get; set; }

    public double WorkerMsPerFrame { get; set; }

    public double PeakMs { get; set; }

    public double CallsPerFrame { get; set; }

    /// <summary>For rows that count traffic.</summary>
    public double BytesPerSecond { get; set; }

    /// <summary>A GPS line an admin can paste into the game, for grids.</summary>
    public string? Gps { get; set; }

    /// <summary>A cluster's grids, costliest first.</summary>
    public System.Collections.Generic.List<string>? Members { get; set; }

    /// <summary>A grid's name alone, as history has it. <see cref="Name"/> also says whose it is, for reports.</summary>
    public string? PlainName { get; set; }

    /// <summary>The blocks on a grid, or on all of a player's or faction's grids.</summary>
    public long Blocks { get; set; }

    /// <summary>The grids a player or faction owns, or the grids in a cluster.</summary>
    public long Grids { get; set; }

    /// <summary>How far across a cluster reaches.</summary>
    public double SizeKm { get; set; }

    /// <summary>A cluster's costliest grids, with their figures.</summary>
    public System.Collections.Generic.List<RunRow>? TopGrids { get; set; }
}

/// <summary>A run's result. The worker keeps only the last few, so Quasar stores what it wants to keep.</summary>
public sealed class RunResultResponse
{
    /// <summary>One of <see cref="RunState"/>.</summary>
    public string State { get; set; } = RunState.Unknown;

    public double Seconds { get; set; }

    public long Frames { get; set; }

    public double SimSpeed { get; set; }

    public double AverageFrameMs { get; set; }

    public double WorstFrameMs { get; set; }

    /// <summary>What timing every call added, summed across threads.</summary>
    public double TimingMsPerFrame { get; set; }

    /// <summary>Game thread time spent waiting for worker threads, which is the only way their time costs the frame.</summary>
    public double MainWaitMsPerFrame { get; set; }

    /// <summary>Worker thread time in the game's parallel updates, summed across workers.</summary>
    public double ParallelWorkerMsPerFrame { get; set; }

    /// <summary>The share of measured time that fitted in the tables. Below 1 means some rows read low.</summary>
    public double Coverage { get; set; }

    public bool EndedEarly { get; set; }

    /// <summary>Why clusters weren't timed, when they were asked for and weren't.</summary>
    public string? ClusterProblem { get; set; }

    /// <summary>Why some of this run reads low, because another plugin changed call sites Monitor times. Null if nothing did.</summary>
    public string? LostSites { get; set; }

    /// <summary>True when it timed clusters, so none in its rows means there were none.</summary>
    public bool ClustersTimed { get; set; }

    /// <summary>Why it failed, when it did.</summary>
    public string? Problem { get; set; }

    /// <summary>When its measuring was over, as UTC ticks.</summary>
    public long EndedUtcTicks { get; set; }

    /// <summary>What the average frame went on, in ms, by the names in <see cref="FrameSplit.Names"/>.</summary>
    public System.Collections.Generic.Dictionary<string, double> Parts { get; set; } = new System.Collections.Generic.Dictionary<string, double>();

    public System.Collections.Generic.List<RunRow> Rows { get; set; } = new System.Collections.Generic.List<RunRow>();

    /// <summary>Who started it, when that's known.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>The slowest single calls, from just before the run and during it. Slowest first.</summary>
    public System.Collections.Generic.List<SlowCallRow> SlowCalls { get; set; } = new System.Collections.Generic.List<SlowCallRow>();
}

/// <summary>One slow call. An average says what's expensive, and this says what spiked.</summary>
public sealed class SlowCallRow
{
    /// <summary>The frame it happened in, to match against <see cref="IncidentFrame.Frame"/>.</summary>
    public long Frame { get; set; }

    public double Ms { get; set; }

    /// <summary>What kind of call it was.</summary>
    public string Where { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool MainThread { get; set; }
}

public sealed class DrillRootsRequest
{
    /// <summary>Part of a name. Empty lists what there is.</summary>
    public string? Text { get; set; }
}

/// <summary>A method to start looking inside.</summary>
public sealed class DrillRoot
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Whose code it is: the game, a plugin or a mod.</summary>
    public string Owner { get; set; } = string.Empty;
}

public sealed class DrillRootsResponse
{
    public System.Collections.Generic.List<DrillRoot> Roots { get; set; } = new System.Collections.Generic.List<DrillRoot>();
}

public sealed class DrillStartRequest
{
    public string Id { get; set; } = string.Empty;

    public int Seconds { get; set; } = 10;
}

public sealed class DrillStartResponse
{
    public bool Started { get; set; }

    /// <summary>Which drill-down this is, to tell its result from a later one's.</summary>
    public long Number { get; set; }

    public string? Message { get; set; }
}

public sealed class DrillResultRequest
{
}

/// <summary>One thing a method calls. All its call sites in that method are added together.</summary>
public sealed class DrillRow
{
    /// <summary>What to ask for to look inside this. Empty when it has no code of its own to look at.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Owner { get; set; } = string.Empty;

    public double CallsPerFrame { get; set; }

    public double MainMs { get; set; }

    public double WorkerMs { get; set; }

    public double PeakMs { get; set; }
}

/// <summary>What a method spent its time on. Times are per frame, and workers are added up across threads.</summary>
public sealed class DrillResultResponse
{
    /// <summary>One of <see cref="RunState"/>.</summary>
    public string State { get; set; } = RunState.Unknown;

    /// <summary>Which drill-down this is the result of.</summary>
    public long Number { get; set; }

    public string Method { get; set; } = string.Empty;

    public string Owner { get; set; } = string.Empty;

    public int Seconds { get; set; }

    public long Frames { get; set; }

    /// <summary>Zero means it never ran, or the JIT inlined it into its caller. Drill into the caller instead.</summary>
    public double CallsPerFrame { get; set; }

    public double MainMs { get; set; }

    public double WorkerMs { get; set; }

    /// <summary>Main thread time not in anything it called, which is the method's own work.</summary>
    public double SelfMs { get; set; }

    public double PeakMs { get; set; }

    /// <summary>What timing all this added, across threads.</summary>
    public double TimingMsPerFrame { get; set; }

    /// <summary>True when it stopped early, because timing this was costing the server too much.</summary>
    public bool EndedEarly { get; set; }

    /// <summary>True when timing added enough that these figures read high.</summary>
    public bool Inflated { get; set; }

    /// <summary>Calls left out, because the method makes too many different ones to track.</summary>
    public int SkippedSites { get; set; }

    public System.Collections.Generic.List<DrillRow> Rows { get; set; } = new System.Collections.Generic.List<DrillRow>();
}

public static class PatchKinds
{
    public const string Prefix = "Prefix";
    public const string Postfix = "Postfix";
    public const string Transpiler = "Transpiler";
    public const string Finalizer = "Finalizer";
}

public sealed class PatchListRequest
{
    public int Skip { get; set; }
}

/// <summary>One patch on a method.</summary>
public sealed class PatchEntryRow
{
    /// <summary>One of <see cref="PatchKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The Harmony id it was applied under.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The assembly the patch's code lives in.</summary>
    public string Plugin { get; set; } = string.Empty;

    public string Patch { get; set; } = string.Empty;

    public int Priority { get; set; }
}

public sealed class PatchedMethodRow
{
    public string Method { get; set; } = string.Empty;

    public System.Collections.Generic.List<PatchEntryRow> Patches { get; set; } = new System.Collections.Generic.List<PatchEntryRow>();
}

/// <summary>A page of patched methods. Monitor's own patches aren't listed.</summary>
public sealed class PatchListResponse
{
    public int Total { get; set; }

    public System.Collections.Generic.List<PatchedMethodRow> Methods { get; set; } = new System.Collections.Generic.List<PatchedMethodRow>();

    /// <summary>Call sites Monitor can't time, because another plugin changed them. The same on every page.</summary>
    public System.Collections.Generic.List<LostSiteRow> LostSites { get; set; } = new System.Collections.Generic.List<LostSiteRow>();
}

/// <summary>A call Monitor would time, in a method another plugin rewrote first.</summary>
public sealed class LostSiteRow
{
    public string Site { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public int Patched { get; set; }

    public int Expected { get; set; }

    public string TakenBy { get; set; } = string.Empty;
}

public sealed class AuditStartRequest
{
    /// <summary>Methods as <see cref="PatchedMethodRow.Method"/> names them.</summary>
    public System.Collections.Generic.List<string> Methods { get; set; } = new System.Collections.Generic.List<string>();

    public int Seconds { get; set; } = 30;
}

public sealed class AuditStartResponse
{
    public bool Started { get; set; }

    /// <summary>Which audit this is, to tell its result from a later one's.</summary>
    public long Number { get; set; }

    public string? Message { get; set; }
}

public sealed class AuditResultRequest
{
}

/// <summary>What a patched method cost, per frame and summed across threads.</summary>
public sealed class AuditRow
{
    public string Method { get; set; } = string.Empty;

    public System.Collections.Generic.List<string> Plugins { get; set; } = new System.Collections.Generic.List<string>();

    public double CallsPerFrame { get; set; }

    public double PrefixMs { get; set; }

    public double OriginalMs { get; set; }

    public double PostfixMs { get; set; }

    /// <summary>True when a transpiler changed the method, so its cost is inside the original's.</summary>
    public bool Transpiled { get; set; }

    /// <summary>Why this method couldn't be audited.</summary>
    public string? Problem { get; set; }
}

public sealed class AuditResultResponse
{
    /// <summary>One of <see cref="RunState"/>.</summary>
    public string State { get; set; } = RunState.Unknown;

    /// <summary>Which audit this is the result of.</summary>
    public long Number { get; set; }

    public int Seconds { get; set; }

    public long Frames { get; set; }

    public System.Collections.Generic.List<AuditRow> Rows { get; set; } = new System.Collections.Generic.List<AuditRow>();
}

/// <summary>What counts as lag, and what the worker does about it. The defaults apply until Quasar says otherwise.</summary>
public sealed class TriggerSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Sim speed under this counts as a slow second.</summary>
    public double SimBelow { get; set; } = 0.9d;

    /// <summary>A frame p95 over this counts as a slow second. Zero is off.</summary>
    public double FrameP95AboveMs { get; set; }

    /// <summary>How many slow seconds in a row it takes.</summary>
    public int Seconds { get; set; } = 5;

    /// <summary>One frame over this counts at once. Zero is off.</summary>
    public double SpikeMs { get; set; } = 1000d;

    /// <summary>How long a triggered run lasts. Timing every call adds cost during lag, so it's short. Zero means no runs.</summary>
    public int RunSeconds { get; set; } = 5;

    /// <summary>The least time between two trips.</summary>
    public int CooldownSeconds { get; set; } = 900;

    /// <summary>How long after a character spawns a spike counts as a join hitch: recorded, with no alert or run. Zero is off.</summary>
    public int JoinGraceSeconds { get; set; } = 15;

    /// <summary>Also times physics clusters. Off by default, because it changes physics scheduling with nobody watching.</summary>
    public bool TimeClusters { get; set; }

    /// <summary>Tells admins in the game when lag is noticed, and what the evidence shows.</summary>
    public bool TellAdmins { get; set; } = true;

    /// <summary>Lets players read what their own grids cost, which costs the server nothing.</summary>
    public bool PlayerMine { get; set; } = true;

    /// <summary>Lets players time their own grid, which costs the whole server for its length.</summary>
    public bool PlayerTest { get; set; } = true;

    public int PlayerTestSeconds { get; set; } = 5;

    /// <summary>The least time between one player's tests.</summary>
    public int PlayerTestCooldownSeconds { get; set; } = 600;

    /// <summary>Tests are refused under this sim speed, so players can't add to lag.</summary>
    public double PlayerTestLowestSim { get; set; } = 0.8d;
}

public sealed class TriggerSetResponse
{
    public bool Applied { get; set; }
}

public sealed class IncidentResultRequest
{
    public long Id { get; set; }
}

/// <summary>A frame from just before a trip, with where its time went.</summary>
public sealed class IncidentFrame
{
    public long Frame { get; set; }

    public double Ms { get; set; }

    /// <summary>Time per tree scope, indexed as in <see cref="HistoryScopes"/>, in 100 ns units.</summary>
    public uint[] ScopeUnits { get; set; } = System.Array.Empty<uint>();

    /// <summary>The highest garbage collection generation in this frame, or -1 if there wasn't one.</summary>
    public int Collection { get; set; } = -1;
}

/// <summary>The evidence from one lag trip.</summary>
public sealed class IncidentResultResponse
{
    /// <summary>One of <see cref="RunState"/>.</summary>
    public string State { get; set; } = RunState.Unknown;

    public string Reason { get; set; } = string.Empty;

    /// <summary>The sim speed or frame time that set it off.</summary>
    public double Value { get; set; }

    /// <summary>The run, if there is one.</summary>
    public RunResultResponse? Run { get; set; }

    /// <summary>Why there's no run.</summary>
    public string? NoRun { get; set; }

    /// <summary>The worst frames just before it, worst first.</summary>
    public System.Collections.Generic.List<IncidentFrame> Frames { get; set; } = new System.Collections.Generic.List<IncidentFrame>();

    public System.Collections.Generic.List<SlowCallRow> SlowCalls { get; set; } = new System.Collections.Generic.List<SlowCallRow>();
}

/// <summary>Where a pull should start. Quasar passes back what the last batch told it.</summary>
public sealed class HistoryPullRequest
{
    public long FromRollup { get; set; }

    public long FromCensus { get; set; }

    public long FromDetail { get; set; }

    public long FromGroups { get; set; }

    public long FromMarkers { get; set; }
}

/// <summary>Asks for <see cref="RuntimeInfoResponse"/>. It takes no options.</summary>
public sealed class RuntimeInfoRequest
{
}

/// <summary>A plugin or mod. The id is what stays the same between restarts.</summary>
public sealed class RuntimeItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>Answer to <see cref="MonitorProtocol.RuntimeInfoOperation"/>.</summary>
public sealed class RuntimeInfoResponse
{
    public string MonitorVersion { get; set; } = string.Empty;

    public string Runtime { get; set; } = string.Empty;

    public bool ServerGc { get; set; }

    public string GcLatencyMode { get; set; } = string.Empty;

    /// <summary>The plugins this server started with. Null until they've been read, which isn't an empty list.</summary>
    public System.Collections.Generic.List<RuntimeItem>? Plugins { get; set; }

    /// <summary>The mods this server started with. Null until they've been read, which isn't an empty list.</summary>
    public System.Collections.Generic.List<RuntimeItem>? Mods { get; set; }
}
