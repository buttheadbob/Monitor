using System;
using System.Collections.Generic;

namespace SenX.Monitor.Core;

/// <summary>What detail measurement is doing. The tree runs in every mode.</summary>
public enum DetailMode
{
    /// <summary>All detail sites closed.</summary>
    Off,

    /// <summary>
    /// One group of related sites at a time, taking turns, kept cheap by sampling if a group costs
    /// too much. The default, for background numbers. Groups are measured at different moments, so
    /// compare them by share of the frame. To find what is causing lag, run a lag-finding command:
    /// see <see cref="Capture"/>.
    /// </summary>
    Grouped,

    /// <summary>
    /// Every call timed all the time. Lag-finding commands switch to this by themselves for their
    /// run, and an admin can also hold it on full time.
    /// </summary>
    Everything,
}

/// <summary>
/// Opens detail sites, and banks what they recorded. In Grouped mode a turn is counted in simulated
/// frames and every call inside it is timed. Runs on the game thread at the end of each frame,
/// when workers are idle, so all open sites always see the same whole frames.
/// </summary>
public static unsafe class DetailController
{
    /// <summary>Frames in a turn. At least 100, so every Update100 entity is seen in its turn.</summary>
    public const int DefaultTurnFrames = 120;

    private static readonly long[] s_framesMeasured = new long[Gates.Capacity];
    private static readonly long[] s_periodBase = new long[Gates.Capacity];
    private static readonly long[] s_lastSamples = new long[Gates.Capacity];
    private static readonly long[] s_lastTicks = new long[Gates.Capacity];
    private static readonly double[] s_callsPerFrame = new double[Gates.Capacity];
    private static readonly double[] s_unitsPerFrame = new double[Gates.Capacity];
    private static readonly long[] s_lastMainSamples = new long[Gates.Capacity];
    private static readonly long[] s_lastMainTicks = new long[Gates.Capacity];
    private static readonly double[] s_mainUnitsPerFrame = new double[Gates.Capacity];
    private static readonly double[] s_workerUnitsPerFrame = new double[Gates.Capacity];
    private static readonly double[] s_frameUnitsDuring = new double[Gates.Capacity];

    private static SiteGroup[] s_groups = Array.Empty<SiteGroup>();
    private static int[] s_sites = Array.Empty<int>();
    private static byte[] s_groupShift = Array.Empty<byte>();
    private static DetailMode s_mode = DetailMode.Off;
    private static int s_active;
    private static int s_turnFrames;
    private static long s_turnBusyUnits;
    private static double s_timedNanosPerFrame;
    private static bool s_held;
    private static bool s_suspended;

    /// <summary>
    /// The mode an admin chose, or the default. A measuring run overrides it only while it runs,
    /// then this mode comes back.
    /// </summary>
    public static DetailMode Mode => s_mode;

    /// <summary>True when background detail is measuring something.</summary>
    public static bool Running => s_mode != DetailMode.Off && s_sites.Length > 0;

    /// <summary>True when every site has been measured since the last period started.</summary>
    internal static bool EveryGroupMeasured
    {
        get
        {
            foreach (int site in s_sites)
            {
                if (s_framesMeasured[site] == s_periodBase[site])
                    return false;
            }

            return true;
        }
    }

    /// <summary>True once the patched sites are known, so detail can run.</summary>
    public static bool Ready => s_sites.Length > 0;

    public static int TurnFrames { get; set; } = DefaultTurnFrames;

    /// <summary>
    /// The most one group's turn may cost, as a fraction of a 60 Hz frame. A group over this is
    /// sampled on its next turn. Doesn't apply to <see cref="DetailMode.Everything"/>, which is asked for on purpose.
    /// </summary>
    public static double GroupCeiling { get; set; } = 0.02;

    /// <summary>The groups in use: only those with a site that was really patched.</summary>
    public static SiteGroup[] Groups => s_groups;

    public static int ActiveGroup => s_active;

    public static int FramesIntoTurn => s_turnFrames;

    /// <summary>The sampling a group uses on its turn. Zero means every call is timed.</summary>
    public static int ShiftOf(int group) => s_groupShift[group];

    /// <summary>Calls per frame from the site's latest turn.</summary>
    public static double CallsPerFrame(int site) => s_callsPerFrame[site];

    /// <summary>Time per frame from the site's latest turn, in 100 ns units, with clock overhead removed.</summary>
    public static double UnitsPerFrame(int site) => s_unitsPerFrame[site];

    /// <summary>
    /// The part of <see cref="UnitsPerFrame"/> on the game thread. This is what holds up a frame.
    /// </summary>
    public static double MainUnitsPerFrame(int site) => s_mainUnitsPerFrame[site];

    /// <summary>
    /// The part of <see cref="UnitsPerFrame"/> on worker threads, summed across them. Several
    /// threads run at once, so this is CPU time and doesn't add up to frame time.
    /// </summary>
    public static double WorkerUnitsPerFrame(int site) => s_workerUnitsPerFrame[site];

    /// <summary>
    /// How long frames took while this site was measured. Gives each number its context: the
    /// share of the frame stays comparable between groups measured at different moments.
    /// </summary>
    public static double FrameUnitsDuring(int site) => s_frameUnitsDuring[site];

    /// <summary>What timing the open sites cost per frame, from the latest banking.</summary>
    public static double TimedNanosPerFrame => s_timedNanosPerFrame;

    /// <summary>Total frames this site has been measured for.</summary>
    public static long FramesMeasured(int site) => s_framesMeasured[site];

    /// <summary>
    /// Sets which sites exist. Only sites that were really patched can ever open, and each group
    /// keeps only those, so a site a game update removed costs nothing and breaks nothing.
    /// </summary>
    public static void Configure(IReadOnlyCollection<int> patchedSites)
    {
        var groups = new List<SiteGroup>();
        var sites = new List<int>();

        foreach (SiteGroup group in SiteGroups.All)
        {
            var kept = new List<int>();
            foreach (int site in group.Sites)
            {
                if (!Contains(patchedSites, site))
                    continue;

                kept.Add(site);
                if (!sites.Contains(site))
                    sites.Add(site);
            }

            if (kept.Count > 0)
                groups.Add(new SiteGroup(group.Name, kept.ToArray()));
        }

        Gates.CloseAll();
        s_groups = groups.ToArray();
        s_sites = sites.ToArray();
        s_groupShift = new byte[s_groups.Length];
        s_active = 0;
        s_turnFrames = 0;
        s_turnBusyUnits = 0L;
        s_held = false;
        s_suspended = false;
        s_mode = DetailMode.Off;
    }

    public static void SetMode(DetailMode mode)
    {
        if (mode != s_mode)
            Markers.NoteDetailChanged(mode);

        // A running measurement owns the gates. The new mode applies when it finishes.
        if (s_suspended)
        {
            s_mode = mode;
            return;
        }

        Finish();
        s_mode = mode;
        s_held = false;
        s_turnFrames = 0;
        s_turnBusyUnits = 0L;
        OpenCurrent();
    }

    /// <summary>Called once per frame from the frame commit. Allocates nothing.</summary>
    public static void OnFrame(bool simulated, bool paused, long busyUnits)
    {
        if (s_suspended || s_mode == DetailMode.Off || s_sites.Length == 0)
            return;

        if (paused)
        {
            if (!s_held)
            {
                Finish();
                s_held = true;
                s_turnFrames = 0;
                s_turnBusyUnits = 0L;
            }

            return;
        }

        if (s_held)
        {
            s_held = false;
            s_turnFrames = 0;
            s_turnBusyUnits = 0L;
            OpenCurrent();
        }

        if (!simulated)
            return;

        s_turnFrames++;
        s_turnBusyUnits += busyUnits;
        if (s_turnFrames < TurnFrames)
            return;

        Finish();
        s_turnFrames = 0;
        s_turnBusyUnits = 0L;

        if (s_mode == DetailMode.Grouped)
            s_active = (s_active + 1) % s_groups.Length;

        OpenCurrent();
    }

    /// <summary>
    /// Works out how much to scale each site and phase up by, so a site measured for part of a period
    /// reads the same as if it had been measured for all of it. A site never measured scales to zero.
    /// </summary>
    internal static void PeriodScales(long periodFrames, double* phaseScale, double* siteScale)
    {
        for (int site = 0; site < Gates.Capacity; site++)
            siteScale[site] = 0d;

        foreach (int site in s_sites)
        {
            long measured = s_framesMeasured[site] - s_periodBase[site];
            siteScale[site] = measured > 0L ? periodFrames / (double)measured : 0d;
        }

        // These two phases each come from two sites, and a game update could leave only one patched.
        phaseScale[Phase.Before] = siteScale[Site.EntityBefore] > 0d ? siteScale[Site.EntityBefore] : siteScale[Site.ParallelBefore];
        phaseScale[Phase.After] = siteScale[Site.EntityAfter] > 0d ? siteScale[Site.EntityAfter] : siteScale[Site.ParallelAfter];
        phaseScale[Phase.Once] = siteScale[Site.EntityOnce];
        phaseScale[Phase.Simulate] = siteScale[Site.EntitySimulate];
        phaseScale[Phase.Every10] = siteScale[Site.EntityBefore10];
        phaseScale[Phase.Every100] = siteScale[Site.EntityBefore100];
        phaseScale[Phase.Other] = siteScale[Site.Script];

        MarkPeriod();
    }

    /// <summary>Starts a new period of frames measured.</summary>
    internal static void MarkPeriod() => Array.Copy(s_framesMeasured, s_periodBase, s_framesMeasured.Length);

    /// <summary>Hands the gates to a measuring run, keeping what has been measured so far.</summary>
    internal static void Suspend()
    {
        Finish();
        s_suspended = true;
    }

    /// <summary>Times every call on every site. Only a measuring run uses this.</summary>
    internal static void OpenAll()
    {
        foreach (int site in s_sites)
            Gates.Open(site, 0);
    }

    internal static void CloseAll()
    {
        foreach (int site in s_sites)
            Gates.Close(site);
    }

    /// <summary>
    /// Takes the gates back from a measuring run and restores the chosen mode. The run's numbers
    /// are skipped, because this controller's next report should cover only its own frames.
    /// </summary>
    internal static void Resume()
    {
        Rebase();
        s_suspended = false;
        s_held = false;
        s_turnFrames = 0;
        s_turnBusyUnits = 0L;
        OpenCurrent();
    }

    // Counters only rise and banking takes differences, so this makes the next difference start here.
    private static void Rebase()
    {
        if (!ThreadBlocks.Ready)
            return;

        ThreadBlock* main = ThreadBlocks.At(0);
        int blocks = ThreadBlocks.Count;

        foreach (int site in s_sites)
        {
            long samples = 0L;
            long ticks = 0L;

            for (int b = 0; b < blocks; b++)
            {
                ThreadBlock* block = ThreadBlocks.At(b);
                samples += block->SiteSamples[site];
                ticks += block->SiteTicks[site];
            }

            s_lastSamples[site] = samples;
            s_lastTicks[site] = ticks;
            s_lastMainSamples[site] = main->SiteSamples[site];
            s_lastMainTicks[site] = main->SiteTicks[site];
        }
    }

    /// <summary>The smallest shift that brings a cost under a ceiling, or the coarsest allowed.</summary>
    public static int ShiftFor(double cost, double ceiling)
    {
        int shift = 0;
        while (cost > ceiling && shift < Gates.MaxShift)
        {
            cost /= 2d;
            shift++;
        }

        return shift;
    }

    private static void OpenCurrent()
    {
        if (s_mode == DetailMode.Everything)
        {
            foreach (int site in s_sites)
                Gates.Open(site, 0);
        }
        else if (s_mode == DetailMode.Grouped && s_groups.Length > 0)
        {
            int shift = s_groupShift[s_active];
            foreach (int site in s_groups[s_active].Sites)
                Gates.Open(site, shift);
        }
    }

    // Closes whatever is open and banks what it recorded.
    private static void Finish()
    {
        if (s_mode == DetailMode.Everything)
        {
            Bank(s_sites, 0);
            foreach (int site in s_sites)
                Gates.Close(site);
        }
        else if (s_mode == DetailMode.Grouped && s_groups.Length > 0)
        {
            SiteGroup group = s_groups[s_active];
            double nanos = Bank(group.Sites, s_groupShift[s_active]);

            foreach (int site in group.Sites)
                Gates.Close(site);

            if (s_turnFrames > 0)
                s_groupShift[s_active] = (byte)ShiftFor(nanos, GroupCeiling * 1e9 / 60d);
        }
    }

    // Returns what the sites would cost per frame if every call were timed.
    private static double Bank(int[] sites, int shift)
    {
        if (!ThreadBlocks.Ready || s_turnFrames == 0)
            return 0d;

        double frames = s_turnFrames;
        double frameUnits = s_turnBusyUnits / frames;
        double fullCost = 0d;
        double timedCost = 0d;
        int blocks = ThreadBlocks.Count;

        foreach (int site in sites)
        {
            long samples = 0L;
            long ticks = 0L;

            for (int b = 0; b < blocks; b++)
            {
                ThreadBlock* block = ThreadBlocks.At(b);
                samples += block->SiteSamples[site];
                ticks += block->SiteTicks[site];
            }

            // Block 0 belongs to the game thread, everything else ran on a worker.
            ThreadBlock* main = ThreadBlocks.At(0);
            long mainSamples = main->SiteSamples[site] - s_lastMainSamples[site];
            long mainTicks = main->SiteTicks[site] - s_lastMainTicks[site];
            s_lastMainSamples[site] += mainSamples;
            s_lastMainTicks[site] += mainTicks;

            long allSamples = samples - s_lastSamples[site];
            long allTicks = ticks - s_lastTicks[site];
            s_lastSamples[site] = samples;
            s_lastTicks[site] = ticks;

            double mainTime = Calibration.WithoutOverhead(Clock.ToUnits(mainTicks), mainSamples << shift);
            double workerTime = Calibration.WithoutOverhead(Clock.ToUnits(allTicks - mainTicks), (allSamples - mainSamples) << shift);

            s_mainUnitsPerFrame[site] = mainTime / frames;
            s_workerUnitsPerFrame[site] = workerTime / frames;
            s_unitsPerFrame[site] = (mainTime + workerTime) / frames;
            s_callsPerFrame[site] = (allSamples << shift) / frames;
            s_frameUnitsDuring[site] = frameUnits;
            s_framesMeasured[site] += s_turnFrames;

            fullCost += s_callsPerFrame[site] * Calibration.TimedCallNanos;
            timedCost += allSamples / frames * Calibration.TimedCallNanos;
        }

        s_timedNanosPerFrame = timedCost;
        return fullCost;
    }

    private static bool Contains(IReadOnlyCollection<int> values, int value)
    {
        foreach (int candidate in values)
        {
            if (candidate == value)
                return true;
        }

        return false;
    }

    /// <summary>Forgets everything recorded so far. Call after thread blocks are cleared.</summary>
    public static void ResetCounters()
    {
        Array.Clear(s_lastSamples, 0, s_lastSamples.Length);
        Array.Clear(s_lastTicks, 0, s_lastTicks.Length);
        Array.Clear(s_lastMainSamples, 0, s_lastMainSamples.Length);
        Array.Clear(s_lastMainTicks, 0, s_lastMainTicks.Length);
        Array.Clear(s_framesMeasured, 0, s_framesMeasured.Length);
        Array.Clear(s_periodBase, 0, s_periodBase.Length);
        Array.Clear(s_callsPerFrame, 0, s_callsPerFrame.Length);
        Array.Clear(s_unitsPerFrame, 0, s_unitsPerFrame.Length);
        Array.Clear(s_mainUnitsPerFrame, 0, s_mainUnitsPerFrame.Length);
        Array.Clear(s_workerUnitsPerFrame, 0, s_workerUnitsPerFrame.Length);
        Array.Clear(s_frameUnitsDuring, 0, s_frameUnitsDuring.Length);
        s_timedNanosPerFrame = 0d;
    }
}
