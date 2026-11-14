using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Patching;
using static SenX.Monitor.Contracts.ChatFigure;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>
/// Readouts that need no run, through chat commands and the log. They run on demand or once a
/// minute, off every hot path, so ordinary strings are fine here.
/// </summary>
internal static unsafe class SpikeReport
{
    private const int PlayersInNet = 10;
    private const int EventsInNet = 5;
    private const int TrendBars = 24;
    private const int TrendSeconds = 3600;

    private static string Ms(double units) => (units / 10000d).ToString("0.000", CultureInfo.InvariantCulture);

    private static string N(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    public static ChatReport Status()
    {
        if (!FrameEngine.Started)
            return "Monitor is waiting for the game to be ready.";

        if (!FrameEngine.TryReadLatest(out RollupRecord row))
            return "Monitor has no data yet, try again in a second.";

        double frames = row.Frames > 0U ? row.Frames : 1d;
        var report = new ChatReport().Title(FrameEngine.Paused ? "Status, paused" : "Status");

        report.Facts(
            Fig("Sim speed", N(row.SimSpeed, "0.00")),
            Fig("Frames/s", N(row.Frames, "0")),
            Fig("Simulated", N(row.SimulatedFrames, "0")),
            Fig("Frame p50", Ms(row.FrameP50), "ms"),
            Fig("Frame p95", Ms(row.FrameP95), "ms"),
            Fig("Frame p99", Ms(row.FrameP99), "ms"),
            Fig("Frame max", Ms(row.FrameMax), "ms"));

        AddHistory(report);

        report.Section("Where the frame went");
        ChatTable parts = report.Table("Part", "ms/f");

        foreach (ScopeDefinition scope in ShownScopes())
        {
            double perFrame = row.ScopeUnits[scope.Id] / frames;
            if (perFrame >= 100d)
                AddScope(parts, scope, perFrame);
        }

        report.Section("Memory");
        report.Facts(
            Fig("GC mode", (GcProbe.IsServerGc ? "Server" : "Workstation") + ", " + GcProbe.LatencyMode),
            Fig("Gen 0/1/2", N(row.Gen0, "0") + " / " + N(row.Gen1, "0") + " / " + N(row.Gen2, "0")),
            Fig("Allocated", N(row.AllocatedKb, "0"), "KB/s"));

        AppendCensus(report);

        report.Section("Monitor");
        report.Facts(
            Fig("Own cost", Ms(row.ScopeUnits[Scope.Monitor] / frames), "ms/f"),
            Fig("Detail timing", N(DetailController.TimedNanosPerFrame / 1000000d, "0.000"), "ms/f"),
            Fig("Detail mode", DetailController.Mode.ToString()),
            Fig("Clock pair", N(Calibration.ClockPairNanos, "0"), "ns"),
            Fig("Overhead taken out", N(Calibration.ReadBiasNanos, "0"), "ns/call"),
            Fig("Timed call", N(Calibration.TimedCallNanos, "0"), "ns"));

        if (ActiveGroupNote() is { Length: > 0 } measuring)
            report.Note(measuring);

        if (Capture.Active)
            report.Note(FormattableString.Invariant($"Measuring everything, {Capture.SecondsLeft} s left."));

        if (Calibration.SlowClock)
            report.Warning("This machine's clock is slow.");

        return report;
    }

    // The same idea as a status card: sim speed and what's using the frame, over the last hour.
    private static void AddHistory(ChatReport report)
    {
        RecordRing rollups = FrameEngine.Rollups;
        long head = rollups.Head;
        long first = Math.Max(rollups.Oldest, head - TrendSeconds);
        long seconds = head - first;

        if (seconds < TrendBars)
            return;

        int partCount = FrameSplit.Names.Length;
        var sim = new double[TrendBars];
        var parts = new double[partCount][];
        var rows = new int[TrendBars];
        var scopes = new double[Scope.Count];

        for (int part = 0; part < partCount; part++)
            parts[part] = new double[TrendBars];

        for (int bar = 0; bar < TrendBars; bar++)
            sim[bar] = double.MaxValue;

        for (long sequence = first; sequence < head; sequence++)
        {
            RollupRecord row = default;
            if (!rollups.TryCopy(sequence, (byte*)&row))
                continue;

            int bar = (int)((sequence - first) * TrendBars / seconds);
            double frames = Math.Max(row.Frames, 1U);

            for (int scope = 1; scope < Scope.Count; scope++)
                scopes[scope] = row.ScopeUnits[scope] / frames / 10000d;

            double[] split = FrameSplit.Of(id => id < scopes.Length ? scopes[id] : 0d);

            for (int part = 0; part < partCount; part++)
                parts[part][bar] += split[part];

            // The worst second in each bar, because an average would hide a dip.
            sim[bar] = Math.Min(sim[bar], row.SimSpeed);
            rows[bar]++;
        }

        var trends = new List<ChatTrend>(partCount + 1);

        for (int bar = 0; bar < TrendBars; bar++)
            sim[bar] = rows[bar] == 0 ? 0d : sim[bar];

        trends.Add(new ChatTrend("Sim speed", sim, 0d, 1d, N(sim[TrendBars - 1], "0.00")));

        for (int part = 0; part < partCount; part++)
        {
            if (FrameSplit.Names[part] == FrameSplit.IdleName)
                continue;

            double[] values = parts[part];

            for (int bar = 0; bar < TrendBars; bar++)
                values[bar] /= Math.Max(rows[bar], 1);

            if (values.Max() >= 0.1d)
                trends.Add(new ChatTrend(FrameSplit.Names[part], values, 0d, FrameSplit.BudgetMs, N(values[TrendBars - 1] / FrameSplit.BudgetMs * 100d, "0") + "%"));
        }

        report.Trends("Last hour", trends.ToArray());
    }

    // In tree order, because a part includes the ones under it, and a flat list reads as more than the frame.
    private static IEnumerable<ScopeDefinition> ShownScopes() =>
        ScopeTable.All.Where(s => s.Parent == Scope.Update || s.Parent == Scope.Session || s.Parent == Scope.Components);

    private static void AddScope(ChatTable table, ScopeDefinition scope, double perFrame)
    {
        int depth = scope.Parent == Scope.Update ? 0 : scope.Parent == Scope.Session ? 1 : 2;
        table.Nested(depth, scope.Name, Ms(perFrame));
    }

    private static void AppendCensus(ChatReport report)
    {
        CensusRecord census = default;
        long latest = Census.Rows.Head - 1L;

        if (!Census.Rows.TryCopy(latest, (byte*)&census))
            return;

        report.Section("World");
        report.Facts(
            Fig("Players", N(census.OnlinePlayers, "0")),
            Fig("Factions", N(census.Factions, "0")),
            Fig("Ping", N(census.PingMeanTenths / 10d, "0.0") + " / " + N(census.PingWorst, "0"), "ms"),
            Fig("Grids", N(census.Grids, "0")),
            Fig("Blocks", N(census.Blocks, "N0")),
            Fig("PCU", N(census.Pcu, "N0")),
            Fig("Characters", N(census.Characters, "0")),
            Fig("Floating", N(census.FloatingObjects, "0")),
            Fig("Voxels", N(census.Voxels, "0")));
    }

    /// <summary>Best, worst and average sim speed from history. Instant, because the tree is always on.</summary>
    public static ChatReport Sim(string[] options)
    {
        if (!TryReadWindow(options, out Window window, out string? problem))
            return problem!;

        return new ChatReport()
            .Title("Sim speed")
            .Facts(
                Fig("Over", N(window.Seconds, "0"), "s"),
                Fig("Best", N(window.BestSim, "0.00")),
                Fig("Worst", N(window.WorstSim, "0.00")),
                Fig("Average", N(window.AverageSim, "0.00")),
                Fig("Worst frame", Ms(window.WorstFrame), "ms"));
    }

    /// <summary>Where frame time went, from history. Instant, because the tree is always on.</summary>
    public static ChatReport Frames(string[] options)
    {
        if (!TryReadWindow(options, out Window window, out string? problem))
            return problem!;

        double available = Math.Max(0d, (1000d / 60d * 10000d) - window.PerFrame(Scope.Frame));

        var report = new ChatReport()
            .Title("Frame time")
            .Facts(
                Fig("Over", N(window.Seconds, "0"), "s"),
                Fig("Sim speed", N(window.AverageSim, "0.00")),
                Fig("Left in a frame", Ms(available), "ms"));

        ChatTable parts = report.Table("Part", "ms/f");

        foreach (ScopeDefinition scope in ShownScopes())
        {
            double perFrame = window.PerFrame(scope.Id);
            if (perFrame >= 10d)
                AddScope(parts, scope, perFrame);
        }

        return report;
    }

    /// <summary>What's crossing the network and who for, from history. Instant, because counting never stops.</summary>
    public static ChatReport Net(string[] options)
    {
        if (!TryReadWindow(options, out Window window, out string? problem))
            return problem!;

        var report = new ChatReport()
            .Title("Network")
            .Facts(
                Fig("Over", N(window.Seconds, "0"), "s"),
                Fig("Sending", N(window.PerSecond(window.SentKb), "0.0"), "KB/s"),
                Fig("Receiving", N(window.PerSecond(window.ReceivedKb), "0.0"), "KB/s"),
                Fig("RPCs in", N(window.PerSecond(window.RpcsIn), "0.0"), "/s"));

        if (window.SentKb > 0L)
        {
            long other = Math.Max(0L, window.SentKb - window.StateSyncKb - window.EventsKb - window.ReplicationKb);

            report.Section("What's being sent");
            report.Facts(
                Fig("State sync", Share(window.StateSyncKb, window)),
                Fig("Events", Share(window.EventsKb, window)),
                Fig("Replication", Share(window.ReplicationKb, window)),
                Fig("Other", Share(other, window)));
        }

        if (NetTraffic.FailedSends > 0L)
            report.Warning(FormattableString.Invariant($"The network has refused {NetTraffic.FailedSends} packets since the server started."));

        AppendPlayers(report);
        LagReport.AddBackground(report, GroupDimension.RpcsSent, "Biggest events sent", EventsInNet);
        return report;
    }

    private static string Share(long part, Window window) => FormattableString.Invariant($"{part * 100d / window.SentKb:0}%");

    // From the last census pass, so these are averages over about 30 seconds. Names are for admins only.
    private static void AppendPlayers(ChatReport report)
    {
        CensusPlayer* players = stackalloc CensusPlayer[Census.MaxPlayers];
        CensusFaction* factions = stackalloc CensusFaction[Census.MaxFactions];

        if (!Census.TryCopyDetail(players, factions, out int count, out int _, out int _, out long _) || count == 0)
            return;

        var rows = new List<CensusPlayer>(count);

        for (int i = 0; i < count; i++)
            rows.Add(players[i]);

        report.Section("Players");
        ChatTable table = report.Table("Player", "Down", "Up", "RPC/s", "Ping", "Queue");

        foreach (CensusPlayer player in rows.OrderByDescending(row => row.SentBytesPerSecond).Take(PlayersInNet))
        {
            table.Row(
                LagReport.OwnerName(player.IdentityId),
                N(player.SentBytesPerSecond / 1024d, "0.0"),
                N(player.ReceivedBytesPerSecond / 1024d, "0.0"),
                N(player.RpcsPerSecond, "0"),
                N(player.PingMs, "0"),
                N(player.QueuedBytes / 1024d, "0.0"));
        }

        report.Note("Down and Up are KB/s, Ping is ms, Queue is KB.");
    }

    private static bool TryReadWindow(string[] options, out Window window, out string? problem)
    {
        window = new Window();
        problem = null;

        CaptureArgs? args = CaptureArgs.ParseWindow(options, out string? error);
        if (args == null)
        {
            problem = error;
            return false;
        }

        if (!FrameEngine.Started)
        {
            problem = "Monitor is waiting for the game to be ready.";
            return false;
        }

        RecordRing rollups = FrameEngine.Rollups;
        long head = rollups.Head;

        for (long sequence = Math.Max(rollups.Oldest, head - args.Seconds); sequence < head; sequence++)
        {
            RollupRecord row = default;
            if (rollups.TryCopy(sequence, (byte*)&row))
                window.Add(row);
        }

        if (window.Seconds == 0)
        {
            problem = "Monitor has no data yet, try again in a second.";
            return false;
        }

        return true;
    }

    // Adds up seconds of history for the commands that can answer without timing anything extra.
    private sealed class Window
    {
        private readonly long[] _scopes = new long[Scope.Count];
        private long _frames;
        private double _simTotal;

        public int Seconds { get; private set; }

        public double BestSim { get; private set; }

        public double WorstSim { get; private set; } = double.MaxValue;

        public double WorstFrame { get; private set; }

        public double AverageSim => Seconds == 0 ? 0d : _simTotal / Seconds;

        public long SentKb { get; private set; }

        public long ReceivedKb { get; private set; }

        public long StateSyncKb { get; private set; }

        public long EventsKb { get; private set; }

        public long ReplicationKb { get; private set; }

        public long RpcsIn { get; private set; }

        public double PerSecond(long total) => total / (double)Math.Max(Seconds, 1);

        public double PerFrame(int scope) => _scopes[scope] / (double)Math.Max(_frames, 1L);

        public void Add(in RollupRecord row)
        {
            SentKb += row.SentKb;
            ReceivedKb += row.ReceivedKb;
            StateSyncKb += row.StateSyncKb;
            EventsKb += row.EventsKb;
            ReplicationKb += row.ReplicationKb;
            RpcsIn += row.RpcsIn;

            double sim = row.SimSpeed;

            Seconds++;
            _simTotal += sim;
            _frames += row.Frames;
            BestSim = Math.Max(BestSim, sim);
            WorstSim = Math.Min(WorstSim, sim);
            WorstFrame = Math.Max(WorstFrame, row.FrameMax);

            RollupRecord copy = row;
            for (int scope = 1; scope < Scope.Count; scope++)
                _scopes[scope] += copy.ScopeUnits[scope];
        }
    }

    private static string ActiveGroupNote()
    {
        SiteGroup[] groups = DetailController.Groups;
        if (DetailController.Mode != DetailMode.Grouped || groups.Length == 0)
            return string.Empty;

        return FormattableString.Invariant(
            $"Measuring {groups[DetailController.ActiveGroup].Name}, {DetailController.FramesIntoTurn} of {DetailController.TurnFrames} frames in.");
    }

    public static ChatReport Sites()
    {
        SiteGroup[] groups = DetailController.Groups;
        if (groups.Length == 0)
            return "Detail hasn't started yet. It starts after a 30 second warm-up.";

        var report = new ChatReport().Title("Detail sites");

        for (int g = 0; g < groups.Length; g++)
        {
            bool active = DetailController.Mode == DetailMode.Grouped && g == DetailController.ActiveGroup;
            int shift = DetailController.ShiftOf(g);
            string sampling = shift == 0 ? "every call" : "1 call in " + (1 << shift);

            report.Section(groups[g].Name + (active ? ", measuring now" : string.Empty) + ", " + sampling);
            ChatTable table = report.Table("Site", "Calls/f", "Main", "Frame", "Work", ThreadTags.Header);

            foreach (int site in groups[g].Sites)
            {
                double frame = DetailController.FrameUnitsDuring(site);
                double main = DetailController.MainUnitsPerFrame(site);
                double share = frame > 0d ? main / frame * 100d : 0d;
                string nested = SiteGroups.IsNested(site) ? " (inside another site)" : string.Empty;

                table.Row(
                    SiteGroups.Name(site) + nested,
                    N(DetailController.CallsPerFrame(site), "0.0"),
                    Ms(main),
                    N(share, "0.0") + "%",
                    Ms(DetailController.WorkerUnitsPerFrame(site)),
                    ThreadTags.Tag(main, DetailController.WorkerUnitsPerFrame(site)));
            }
        }

        report.Note("Main and Work are ms/f. " + ThreadTags.Note);

        return report.Facts(
            Fig("Threads without a block", N(ThreadBlocks.Unattached, "0")),
            Fig("Keys named", N(Hooks.KeyNames.Count, "0")),
            Fig("Slot faults", N(Hooks.SlotMeta.Faults, "0")));
    }

    public static ChatReport Patches()
    {
        // Read once, so the figures can't disagree if something patches partway through.
        IReadOnlyList<SiteResult> results = PatchEngine.Results;

        int ok = results.Count(r => r.State == SiteState.Ok);
        int drifted = results.Count(r => r.State == SiteState.Drifted);
        int unavailable = results.Count(r => r.State == SiteState.Unavailable);
        int takenOver = results.Count(r => r.State == SiteState.TakenOver);
        int refused = results.Count(r => r.State == SiteState.Refused);

        var report = new ChatReport()
            .Title("Monitor's patches")
            .Facts(
                Fig("Methods patched", N(PatchEngine.PatchedMethods, "0")),
                Fig("Took", N(PatchEngine.LoadMilliseconds, "0"), "ms"),
                Fig("Sites ok", N(ok, "0")),
                Fig("Drifted", N(drifted, "0")),
                Fig("Unavailable", N(unavailable, "0")),
                Fig("Taken over", N(takenOver, "0")),
                Fig("Left alone", N(refused, "0")));

        if (PatchEngine.TakenOverNote(results) is { } note)
            report.Warning(note + ".");

        foreach (SiteResult result in results.Where(r => r.State != SiteState.Ok))
        {
            string who = result.State == SiteState.TakenOver ? "in a method rewritten by " + string.Join(", ", result.TakenBy) : result.Note;

            report.Item(FormattableString.Invariant($"{result.State}: {result.Target.Name} in {result.Caller} ({result.Patched}/{result.Target.Expected}) {who}").TrimEnd());
        }

        return report;
    }
}
