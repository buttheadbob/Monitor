using System;

namespace SenX.Monitor.Core;

/// <summary>Where a measuring run has got to.</summary>
public enum CaptureState
{
    Idle,
    Requested,
    Closing,
    Clearing,
    Measuring,
    Settling,
    Rolling,
}

/// <summary>What happened when a measuring run was asked for.</summary>
public enum CaptureStart
{
    Started,

    /// <summary>Detail hasn't started yet.</summary>
    NotReady,

    /// <summary>Another run is in progress.</summary>
    Busy,

    /// <summary>The server is paused, so nothing would be measured.</summary>
    Paused,
}

/// <summary>Limits a run's grid and owner totals. Read only, once the run has finished measuring.</summary>
public sealed class CaptureFilter
{
    /// <summary>Only this grid and its blocks. Zero means every grid.</summary>
    public long GridId { get; set; }

    /// <summary>Only these owners, sorted. Null means every owner.</summary>
    public long[]? Owners { get; set; }

    /// <summary>Only the grids' own updates, leaving out their blocks.</summary>
    public bool GridsOnly { get; set; }

    /// <summary>Only blocks of these classes, as sorted class keys. Null means every block.</summary>
    public long[]? BlockTypes { get; set; }

    internal unsafe bool Accepts(EntitySlot* slot)
    {
        if (BlockTypes != null && Array.BinarySearch(BlockTypes, EntityKeys.TypeOf(slot->DefinitionKey)) < 0)
            return false;

        if (GridId != 0L && slot->GridId != GridId)
            return false;

        if (GridsOnly && slot->Id != slot->GridId)
            return false;

        return Owners == null || Array.BinarySearch(Owners, slot->OwnerId) >= 0;
    }
}

/// <summary>A site's totals across all threads, with clock overhead removed.</summary>
public struct SiteTotal
{
    public long MainUnits;
    public long WorkerUnits;
    public long Calls;
    public long OverflowUnits;
}

/// <summary>
/// Everything one measuring run found. All totals come from the same frames with every call timed.
/// The object is reused by the next run, so read it before starting another.
/// </summary>
public sealed unsafe class CaptureResult
{
    private readonly long[] _scopes = new long[Scope.Count];
    private readonly SiteTotal[] _sites = new SiteTotal[Site.Count];

    internal CaptureResult()
    {
        Grids = new RollupTable(1 << 15);
        BlockTypes = new RollupTable(1 << 10);
        Definitions = new RollupTable(1 << 12);
        Owners = new RollupTable(1 << 13);
        Factions = new RollupTable(1 << 12);
        Scripts = new RollupTable(1 << 13);
        EntityTypes = new RollupTable(1 << 9);
        Session = new RollupTable(1 << 9);
        Logic = new RollupTable(1 << 11);
        Callbacks = new RollupTable(1 << 12);
        Plugins = new RollupTable(1 << 8);
        Rpcs = new RollupTable(1 << 11);
        RpcsSent = new RollupTable(1 << 11);
        Mods = new RollupTable(1 << 9);
        ModParts = new RollupTable(1 << 11);
        Clusters = new RollupTable(1 << 12);
    }

    // Called by the sweep too, which reports over its own frames.

    public RollupTable Grids { get; }

    public RollupTable BlockTypes { get; }

    public RollupTable Definitions { get; }

    public RollupTable Owners { get; }

    /// <summary>Owners totalled by faction. Sweeps fill this, and runs work it out for their report.</summary>
    public RollupTable Factions { get; }

    public RollupTable Scripts { get; }

    public RollupTable EntityTypes { get; }

    public RollupTable Session { get; }

    public RollupTable Logic { get; }

    public RollupTable Callbacks { get; }

    /// <summary>Each plugin's own Update.</summary>
    public RollupTable Plugins { get; }

    /// <summary>RPCs from clients, by the method they run.</summary>
    public RollupTable Rpcs { get; }

    /// <summary>
    /// RPCs the server sent, by the method they run. Not a timing: the main column is bytes and the
    /// calls column is events, so read it with <see cref="BytesPerSecond"/>.
    /// </summary>
    public RollupTable RpcsSent { get; }

    /// <summary>The seconds <see cref="RpcsSent"/> covers.</summary>
    public double TrafficSeconds { get; internal set; }

    /// <summary>Bytes over the stretch <see cref="RpcsSent"/> covers, as a rate.</summary>
    public double BytesPerSecond(long bytes) => TrafficSeconds > 0d ? bytes / TrafficSeconds : 0d;

    /// <summary>Time by mod or plugin. Sweeps fill this, and runs work it out for their report.</summary>
    public RollupTable Mods { get; }

    /// <summary>What's inside each mod. A row's representative is its mod.</summary>
    public RollupTable ModParts { get; }

    /// <summary>Physics clusters timed alone. Empty unless the run asked for them.</summary>
    public RollupTable Clusters { get; }

    /// <summary>The slowest single calls just before the run and during it.</summary>
    public SlowestCalls Slowest { get; } = new SlowestCalls();

    /// <summary>True when this run timed physics clusters.</summary>
    public bool ClustersTimed { get; internal set; }

    public long Frames { get; internal set; }

    public long SimulatedFrames { get; internal set; }

    public long ElapsedUnits { get; internal set; }

    /// <summary>Time simulated frames spent working, excluding the wait for the next frame.</summary>
    public long BusyUnits { get; internal set; }

    public uint WorstFrameUnits { get; internal set; }

    /// <summary>True when the server paused during the run, so it stopped early.</summary>
    public bool EndedEarly { get; internal set; }

    /// <summary>Calls lost because more threads recorded than there are blocks.</summary>
    public long UnattachedCalls { get; internal set; }

    public long ScopeUnits(int scope) => _scopes[scope];

    public SiteTotal SiteTotal(int site) => _sites[site - Site.First];

    public double SimSpeed =>
        ElapsedUnits == 0L ? 0d : SimulatedFrames / (ElapsedUnits / (double)Clock.UnitsPerSecond * 60d);

    /// <summary>Turns a total into a per-frame figure, over simulated frames.</summary>
    public double PerFrame(long units) => units / (double)Math.Max(SimulatedFrames, 1L);

    public double AverageFrameUnits => PerFrame(BusyUnits);

    /// <summary>Worker thread time in the game's parallel updates, which hold every other call workers make there.</summary>
    public long ParallelWorkerUnits => SiteTotal(Site.ParallelBefore).WorkerUnits + SiteTotal(Site.ParallelAfter).WorkerUnits;

    /// <summary>
    /// Game thread time spent waiting for worker threads, which is the only way their time costs the frame. It's the
    /// task wait, plus each parallel update's game thread time less the work the game thread did there itself. Only
    /// a run has it, because background detail times the parallel updates for part of the time. It includes the game
    /// thread handing the work out, so it can read slightly high.
    /// </summary>
    public long MainWaitUnits => MainWait(
        ScopeUnits(Scope.TaskWait),
        ScopeUnits(Scope.BeforeParallel),
        SiteTotal(Site.ParallelBefore).MainUnits,
        ScopeUnits(Scope.AfterParallel),
        SiteTotal(Site.ParallelAfter).MainUnits);

    internal static long MainWait(long taskWait, long before, long beforeOwnWork, long after, long afterOwnWork) =>
        taskWait + Math.Max(0L, before - beforeOwnWork) + Math.Max(0L, after - afterOwnWork);

    /// <summary>What timing every call cost per frame during the run, summed across threads.</summary>
    public double TimingNanosPerFrame
    {
        get
        {
            long calls = 0L;
            foreach (SiteTotal site in _sites)
                calls += site.Calls;

            return PerFrame(calls) * Calibration.TimedCallNanos;
        }
    }

    /// <summary>The share of measured time that landed in a row. Below 1 means a table filled up.</summary>
    public double Coverage
    {
        get
        {
            long placed = 0L;
            long lost = 0L;

            foreach (SiteTotal site in _sites)
            {
                placed += site.MainUnits + site.WorkerUnits;
                lost += site.OverflowUnits;
            }

            lost += Grids.LostUnits + BlockTypes.LostUnits + Definitions.LostUnits + Owners.LostUnits + Scripts.LostUnits
                    + EntityTypes.LostUnits + Session.LostUnits + Logic.LostUnits + Callbacks.LostUnits + Clusters.LostUnits + Plugins.LostUnits + Rpcs.LostUnits;

            return placed == 0L ? 1d : Math.Max(0d, 1d - (lost / (double)placed));
        }
    }

    internal void Reset()
    {
        Grids.Clear();
        BlockTypes.Clear();
        Definitions.Clear();
        Owners.Clear();
        Factions.Clear();
        Scripts.Clear();
        EntityTypes.Clear();
        Session.Clear();
        Logic.Clear();
        Callbacks.Clear();
        Plugins.Clear();
        Rpcs.Clear();
        RpcsSent.Clear();
        TrafficSeconds = 0d;
        Mods.Clear();
        ModParts.Clear();
        Clusters.Clear();
        Slowest.Clear();
        ClustersTimed = false;
        Array.Clear(_scopes, 0, _scopes.Length);
        Array.Clear(_sites, 0, _sites.Length);
        Frames = 0L;
        SimulatedFrames = 0L;
        ElapsedUnits = 0L;
        BusyUnits = 0L;
        WorstFrameUnits = 0U;
        EndedEarly = false;
        UnattachedCalls = 0L;
    }

    internal void AddFrame(FrameRecord* record, bool simulated)
    {
        Frames++;

        for (int scope = 1; scope < Scope.Count; scope++)
            _scopes[scope] += record->ScopeUnits[scope];

        if (!simulated)
            return;

        SimulatedFrames++;
        BusyUnits += record->BusyUnits;
        if (record->BusyUnits > WorstFrameUnits)
            WorstFrameUnits = record->BusyUnits;
    }

    /// <summary>
    /// Totals every thread's tables and clears them. Runs with all gates closed, so nothing is still
    /// being written. A run passes no scales, because it times everything for its whole length. A
    /// sweep passes them, so sites measured for part of a period read as if measured for all of it.
    /// </summary>
    internal void Roll(CaptureFilter? filter, double* phaseScale, double* siteScale)
    {
        int blocks = ThreadBlocks.Count;

        for (int b = 0; b < blocks; b++)
        {
            ThreadBlock* block = ThreadBlocks.At(b);
            bool main = b == 0;

            RollEntities(block, main, filter, phaseScale);
            RollKeys(block, main, siteScale);
            RollSites(block, main);
        }
    }

    private void RollEntities(ThreadBlock* block, bool main, CaptureFilter? filter, double* phaseScale)
    {
        OpenTable<EntitySlot> table = block->Entities;
        int capacity = table.Capacity;

        for (int i = 0; i < capacity; i++)
        {
            EntitySlot* slot = table.At(i);
            if (slot->Id == 0L)
                continue;

            if (filter == null || filter.Accepts(slot))
                RollEntity(slot, main, phaseScale);

            // Writing only used slots leaves the rest of a big table out of the cache.
            *slot = default;
        }
    }

    private void RollEntity(EntitySlot* slot, bool main, double* phaseScale)
    {
        // Script runs happen inside their block's own update, so adding them would count them twice.
        double scriptScale = phaseScale == null ? 1d : phaseScale[Phase.Other];
        long script = (long)(Clock.ToUnits(slot->PhaseTicks[Phase.Other]) * scriptScale);
        if (script > 0L)
            Scripts.Add(slot->Id, main ? script : 0L, main ? 0L : script, 0L, slot->MaxUnits, slot->Id);

        long ticks = 0L;
        double scaledTicks = 0d;

        for (int phase = 0; phase < Phase.Other; phase++)
        {
            long phaseTicks = slot->PhaseTicks[phase];
            ticks += phaseTicks;
            scaledTicks += phaseScale == null ? phaseTicks : phaseTicks * phaseScale[phase];
        }

        if (ticks == 0L)
            return;

        double scale = scaledTicks / ticks;
        long units = (long)(Calibration.WithoutOverhead(Clock.ToUnits(ticks), slot->Samples) * scale);
        long calls = (long)(slot->Samples * scale);
        long mainUnits = main ? units : 0L;
        long workerUnits = main ? 0L : units;
        uint max = slot->MaxUnits;
        long id = slot->Id;
        long typeKey = EntityKeys.TypeOf(slot->DefinitionKey);
        bool onGrid = slot->GridId != 0L;
        bool isBlock = onGrid && id != slot->GridId;

        if (onGrid)
        {
            Grids.Add(slot->GridId, mainUnits, workerUnits, calls, max, id);
            Owners.Add(slot->OwnerId, mainUnits, workerUnits, calls, max, id);
        }

        if (isBlock)
        {
            BlockTypes.Add(typeKey, mainUnits, workerUnits, calls, max, id);
            Definitions.Add(slot->DefinitionKey, mainUnits, workerUnits, calls, max, id);
        }

        EntityTypes.Add(isBlock ? EntityKeys.Blocks : typeKey, mainUnits, workerUnits, calls, max, id);
    }

    private void RollKeys(ThreadBlock* block, bool main, double* siteScale)
    {
        OpenTable<KeySlot> table = block->Keys;
        int capacity = table.Capacity;

        for (int i = 0; i < capacity; i++)
        {
            KeySlot* slot = table.At(i);
            if (slot->Key == 0L)
                continue;

            int site = (int)slot->Scope;
            RollupTable? target = TableFor(site);

            if (target != null)
            {
                double scale = siteScale == null ? 1d : siteScale[site];
                long units = (long)(Calibration.WithoutOverhead(Clock.ToUnits(slot->ScaledTicks), slot->Samples) * scale);
                long calls = (long)(slot->Samples * scale);
                target.Add(SiteKeys.ValueOf(slot->Key), main ? units : 0L, main ? 0L : units, calls, slot->MaxUnits, 0L);
            }

            *slot = default;
        }
    }

    private RollupTable? TableFor(int site)
    {
        switch (site)
        {
            case Site.SessionBefore:
            case Site.SessionSimulate:
            case Site.SessionAfter:
                return Session;
            case Site.GameLogic:
            case Site.EntityLogic:
            case Site.CompositeLogic:
                return Logic;
            case Site.GridCallback:
            case Site.InvokeLater:
                return Callbacks;
            case Site.ParallelBefore:
            case Site.ParallelAfter:
                return EntityTypes;
            case Site.PhysicsCluster:
                return Clusters;
            case Site.PluginUpdate:
                return Plugins;
            case Site.Rpc:
                return Rpcs;
            default:
                return null;
        }
    }

    private void RollSites(ThreadBlock* block, bool main)
    {
        for (int site = Site.First; site <= Site.Last; site++)
        {
            long calls = block->SiteSamples[site];
            long units = (long)Calibration.WithoutOverhead(Clock.ToUnits(block->SiteTicks[site]), calls);
            ref SiteTotal total = ref _sites[site - Site.First];

            if (main)
                total.MainUnits += units;
            else
                total.WorkerUnits += units;

            total.Calls += calls;
            total.OverflowUnits += Clock.ToUnits(block->OverflowTicks[site]);
        }
    }
}

/// <summary>
/// One timed run. This is what lag-finding commands use: it switches detail to Everything by itself,
/// measures for a set time, then puts back the mode an admin chose. Each run starts from clean
/// tables, so every total covers exactly the same frames. One run at a time.
///
/// Steps run at the end of a frame on the game thread, when workers are idle. Clearing and totalling
/// touch a lot of memory, so they run on a low priority thread while the game carries on.
/// </summary>
public static unsafe class Capture
{
    // A worker could still be finishing a call when a gate closes. Two frame ends let it finish.
    private const int SettleFrames = 2;

    // Slow calls just before a run are the ones that show what started it. About a minute at full speed.
    private const ulong SlowCallFramesBefore = 3600UL;

    private static readonly Action s_prepare = Prepare;
    private static readonly Action s_total = Total;

    private static CaptureState s_state;
    private static CaptureResult? s_result;
    private static CaptureFilter? s_filter;
    private static Action<CaptureResult?>? s_done;
    private static Exception? s_fault;
    private static long s_started;
    private static long s_deadline;
    private static long s_unattachedAtStart;
    private static int s_seconds;
    private static int s_wait;
    private static bool s_finished;
    private static bool s_clusters;

    /// <summary>Runs background steps on the calling thread, so tests control when they happen.</summary>
    internal static bool Inline
    {
        get => Background.Inline;
        set => Background.Inline = value;
    }

    /// <summary>True when no run is in progress and any finished one has been collected.</summary>
    public static bool Idle => s_state == CaptureState.Idle && !s_finished;

    public static CaptureState State => s_state;

    /// <summary>True from the moment a run is asked for until its totals are ready.</summary>
    public static bool Active => s_state != CaptureState.Idle;

    /// <summary>What stopped the last run, if anything.</summary>
    public static Exception? Fault => s_fault;

    /// <summary>Set when Monitor has shut itself down, so no run can start.</summary>
    public static bool Closed { get; set; }

    /// <summary>Seconds until the current run finishes measuring.</summary>
    public static int SecondsLeft
    {
        get
        {
            switch (s_state)
            {
                case CaptureState.Requested:
                case CaptureState.Closing:
                case CaptureState.Clearing:
                    return s_seconds;
                case CaptureState.Measuring:
                    return (int)Math.Max(0L, (s_deadline - Clock.Now()) / Clock.Frequency);
                default:
                    return 0;
            }
        }
    }

    /// <summary>
    /// Asks for a run. <paramref name="done"/> is called from <see cref="Deliver"/> with the totals, or
    /// null on failure. <paramref name="clusters"/> also times physics clusters, which changes how
    /// physics is scheduled, so pass it only when the results are wanted and it's known to be safe.
    /// </summary>
    public static CaptureStart TryStart(int seconds, CaptureFilter? filter, Action<CaptureResult?> done, bool clusters = false)
    {
        // Nothing would ever move a run on, and whoever asked would wait for ever.
        if (Closed)
            return CaptureStart.NotReady;

        if (s_state != CaptureState.Idle || s_finished)
            return CaptureStart.Busy;

        if (!DetailController.Ready)
            return CaptureStart.NotReady;

        if (FrameEngine.Paused)
            return CaptureStart.Paused;

        s_seconds = seconds;
        s_filter = filter;
        s_clusters = clusters;
        s_done = done;
        s_fault = null;
        s_state = CaptureState.Requested;
        return CaptureStart.Started;
    }

    /// <summary>Gives up on a run that nothing will now finish, so whoever asked isn't left waiting. Call on the game thread.</summary>
    public static void Abandon()
    {
        if (s_state == CaptureState.Idle && !s_finished)
            return;

        Action<CaptureResult?>? done = s_done;
        s_done = null;
        s_finished = false;
        s_state = CaptureState.Idle;
        s_fault ??= new InvalidOperationException("Monitor shut itself down during the run.");
        done?.Invoke(null);
    }

    /// <summary>Hands a finished run to whoever asked for it. Call on the game thread, off the frame hook.</summary>
    public static void Deliver()
    {
        if (!s_finished)
            return;

        Action<CaptureResult?>? done = s_done;
        s_finished = false;
        s_done = null;
        done?.Invoke(s_fault == null ? s_result : null);
    }

    /// <summary>Called at the end of every frame while <see cref="Active"/>. Allocates nothing while measuring.</summary>
    internal static void OnFrame(FrameRecord* record, bool simulated, bool paused)
    {
        switch (s_state)
        {
            case CaptureState.Requested:
                // A background total has the gates for a few frames, so this waits for it.
                if (Sweep.Busy)
                    break;

                DetailController.Suspend();
                s_wait = SettleFrames;
                s_state = CaptureState.Closing;
                break;

            case CaptureState.Closing:
                if (--s_wait <= 0)
                {
                    s_state = CaptureState.Clearing;
                    Background.Start(s_prepare);
                }

                break;

            case CaptureState.Clearing:
                if (Background.Done)
                {
                    s_fault = Background.Fault;

                    if (s_fault != null)
                        Finish();
                    else
                        Open();
                }

                break;

            case CaptureState.Measuring:
                s_result!.AddFrame(record, simulated);
                if (paused)
                    s_result.EndedEarly = true;

                long now = Clock.Now();
                if (paused || now >= s_deadline)
                {
                    DetailController.CloseAll();
                    Gates.Close(Site.PhysicsCluster);
                    s_result.ElapsedUnits = Clock.ToUnits(now - s_started);
                    s_wait = SettleFrames;
                    s_state = CaptureState.Settling;
                }

                break;

            case CaptureState.Settling:
                if (--s_wait <= 0)
                {
                    s_state = CaptureState.Rolling;
                    Background.Start(s_total);
                }

                break;

            case CaptureState.Rolling:
                if (Background.Done)
                {
                    s_fault = Background.Fault;
                    Finish();
                }

                break;
        }
    }

    private static void Open()
    {
        ThreadBlocks.BeginPeriod();
        s_unattachedAtStart = ThreadBlocks.Unattached;
        s_started = Clock.Now();
        s_deadline = s_started + (s_seconds * Clock.Frequency);
        DetailController.OpenAll();

        if (s_clusters)
        {
            ClusterTurns.Reset();
            Gates.Open(Site.PhysicsCluster, 0);
        }

        // Bytes are always counted, so a run's are what's new from here on.
        NetTraffic.TakeSentRpcs(null);

        s_result!.ClustersTimed = s_clusters;
        Markers.AddRunStarted(s_seconds);
        s_state = CaptureState.Measuring;
    }

    private static void Finish()
    {
        DetailController.Resume();
        Sweep.Restart();
        s_filter = null;
        s_state = CaptureState.Idle;
        s_finished = true;
    }

    /// <summary>The one set of totals. Runs and sweeps take turns, so they can share it.</summary>
    internal static CaptureResult SharedResult() => s_result ??= new CaptureResult();

    private static void Prepare()
    {
        CaptureResult result = SharedResult();
        ulong frame = FrameEngine.Frame;

        result.Reset();
        result.Slowest.Collect(frame > SlowCallFramesBefore ? frame - SlowCallFramesBefore : 0UL);
        ThreadBlocks.ClearAll();
    }

    private static void Total()
    {
        s_result!.UnattachedCalls = ThreadBlocks.Unattached - s_unattachedAtStart;
        s_result.Slowest.Collect(0UL);
        s_result.Roll(s_filter, null, null);
        s_result.TrafficSeconds = NetTraffic.TakeSentRpcs(s_result.RpcsSent);
    }

    /// <summary>Ends a measuring run on the next frame instead of waiting for its deadline.</summary>
    internal static void ExpireForTests() => s_deadline = 0L;

    internal static void ResetForTests()
    {
        s_state = CaptureState.Idle;
        s_filter = null;
        s_done = null;
        s_fault = null;
        s_finished = false;
        Background.ResetForTests();
    }
}
