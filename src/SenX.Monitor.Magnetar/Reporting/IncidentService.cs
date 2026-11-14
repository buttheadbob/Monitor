using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Sandbox.Game;
using Sandbox.Game.World;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Patching;
using VRage.Game.ModAPI;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>
/// Gathers the evidence when lag is noticed. A marker goes out straight away, so Quasar can open an
/// incident, and the evidence is kept until it's collected. A run gives the cause of sustained lag.
/// A spike is over by the time a run could start, so its evidence is the frames and calls around it.
/// If Quasar is away, the oldest reports are dropped, and the worker carries on regardless.
/// </summary>
internal static unsafe class IncidentService
{
    private const int KeptReports = 8;
    private const int RowsPerList = 20;
    private const int WorstFrames = 20;

    // About 30 seconds at full speed. This has to end before a run starts, because timing every call
    // makes frames slower, and that isn't the lag being looked for.
    private const int FramesBefore = 1800;

    private static readonly ConcurrentDictionary<long, IncidentResultResponse> s_finished = new();
    private static readonly Queue<long> s_order = new();

    private static TriggerSettings s_settings = new();
    private static Trip? s_waiting;
    private static string? s_notice;
    private static long s_lastId;
    private static long s_runningId;

    private sealed class Trip
    {
        public long Id;
        public LagReason Reason;
        public double Value;
        public long FlightHead;
        public ulong Frame;
        public string? NoRun;
        public string? ClusterProblem;
    }

    public static void Start()
    {
        LagWatch.Tripped = OnTripped;
        LagWatch.JoinHitch = OnJoinHitch;
    }

    public static void Stop()
    {
        LagWatch.Tripped = null;
        LagWatch.JoinHitch = null;
    }

    // Recorded so the hitch has a reason on the charts, and told to nobody, because it isn't lag anyone can act on.
    private static void OnJoinHitch(double ms) =>
        MonitorLog.Info("Monitor noticed a join hitch: one frame took " + ((long)ms).ToString(CultureInfo.InvariantCulture)
                        + " ms just after a character spawned. That's the game loading voxel files, so there's no alert or run.");

    public static bool Enabled => LagWatch.Limits.Enabled;

    /// <summary>Takes new limits, from Quasar or a command.</summary>
    public static void Configure(TriggerSettings settings)
    {
        s_settings = settings;
        PlayerCommands.Configure(settings);

        LagWatch.Limits = new LagLimits
        {
            Enabled = settings.Enabled,
            SimBelow = settings.SimBelow,
            FrameP95AboveMs = settings.FrameP95AboveMs,
            Seconds = Math.Max(settings.Seconds, 1),
            SpikeMs = settings.SpikeMs,
            CooldownSeconds = Math.Max(settings.CooldownSeconds, 0),
            JoinGraceSeconds = Math.Max(settings.JoinGraceSeconds, 0),
        };
    }

    /// <summary>Turns noticing lag on or off, leaving the limits alone.</summary>
    public static void SetEnabled(bool enabled)
    {
        s_settings.Enabled = enabled;
        Configure(s_settings);
    }

    // At the end of a frame, on the game thread.
    private static void OnTripped(LagReason reason, double value)
    {
        var trip = new Trip
        {
            Id = ++s_lastId,
            Reason = reason,
            Value = value,
            FlightHead = FrameEngine.Flight.Head,
            Frame = FrameEngine.Frame,
        };

        Markers.AddLag((uint)trip.Id);
        MonitorLog.Info("Monitor noticed lag: " + reason + ".");
        s_notice = "Monitor noticed lag, and is gathering the evidence.";

        int seconds = Math.Min(s_settings.RunSeconds, CaptureArgs.MaxSeconds);

        if (reason == LagReason.Spike)
            trip.NoRun = "a spike is over before a run could start";
        else if (seconds < 1)
            trip.NoRun = "triggered runs are switched off";

        if (trip.NoRun != null)
        {
            s_waiting = trip;
            return;
        }

        bool clusters = s_settings.TimeClusters;

        if (clusters)
        {
            trip.ClusterProblem = PatchEngine.ClusterTurnProblem();
            clusters = trip.ClusterProblem == null;
        }

        CaptureStart started = Capture.TryStart(seconds, null, result => Finish(trip, result), clusters);

        if (started == CaptureStart.Started)
        {
            Volatile.Write(ref s_runningId, trip.Id);
            ServerApi.Instance.NoteTriggeredRun(seconds);
            return;
        }

        trip.NoRun = "the run didn't start: " + started;
        s_waiting = trip;
    }

    /// <summary>Reports a trip that has no run. Called each frame on the game thread, off the frame hook.</summary>
    public static void Step()
    {
        if (s_notice != null)
        {
            string notice = s_notice;
            s_notice = null;
            TellAdmins(notice);
        }

        Trip? trip = s_waiting;
        if (trip == null)
            return;

        s_waiting = null;
        Finish(trip, null);
    }

    // Admins are the ones who can act, and a notice in chat reaches them without Quasar open.
    private static void TellAdmins(string text)
    {
        if (!s_settings.TellAdmins || MySession.Static == null)
            return;

        try
        {
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player.IsRealPlayer && player.Identity != null && MySession.Static.GetUserPromoteLevel(player.Id.SteamId) >= MyPromoteLevel.Admin)
                    MyVisualScriptLogicProvider.SendChatMessage(text, "Monitor", player.Identity.IdentityId);
            }
        }
        catch (Exception exception)
        {
            MonitorLog.Warning("Monitor could not tell admins about lag: " + exception.Message);
        }
    }

    private static string Summary(IncidentResultResponse report)
    {
        RunRow? grid = report.Run?.Rows.Find(row => row.List == RunLists.Grids);

        if (grid != null)
            return "Monitor's evidence is in. The costliest grid was " + grid.Name + ", at " + (grid.MainMsPerFrame + grid.WorkerMsPerFrame).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms/f.";

        SlowCallRow? call = report.SlowCalls.Count > 0 ? report.SlowCalls[0] : null;

        return call == null
            ? "Monitor's evidence is in, on the Incidents tab."
            : "Monitor's evidence is in. The slowest call was " + call.Name + ", at " + call.Ms.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms.";
    }

    private static void Finish(Trip trip, CaptureResult? result)
    {
        try
        {
            var report = new IncidentResultResponse
            {
                State = RunState.Finished,
                Reason = trip.Reason.ToString(),
                Value = trip.Value,
                NoRun = trip.NoRun,
                Frames = FramesAround(trip),
            };

            if (result != null)
            {
                report.Run = LagReport.BuildRun(result, RowsPerList, trip.ClusterProblem);
                report.Run.RequestedBy = RunLists.LagTrip;
                report.SlowCalls = report.Run.SlowCalls;
                RunService.NoteLagRun(report.Run);
            }
            else
            {
                if (trip.NoRun == null)
                {
                    report.NoRun = "the run failed, and the details are in the server log";
                    MonitorLog.Error("The run after a lag trip failed.", Capture.Fault ?? new InvalidOperationException("The run ended with no result."));
                }

                var calls = new SlowestCalls();
                calls.Collect(Older(trip.Frame));
                report.SlowCalls = LagReport.NameSlowCalls(calls);
            }

            Keep(trip.Id, report);
            TellAdmins(Summary(report));
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not gather the evidence for a lag trip.", exception);
            Keep(trip.Id, new IncidentResultResponse { State = RunState.Failed });
        }

        Volatile.Write(ref s_runningId, 0L);
    }

    private static ulong Older(ulong frame) => frame > FramesBefore ? frame - FramesBefore : 0UL;

    // The worst frames just before the trip, and their breakdown. Reading the ring needs nothing held,
    // because the window is still in it. Should a long run outlast it, there are fewer frames to show.
    private static List<IncidentFrame> FramesAround(Trip trip)
    {
        RecordRing ring = FrameEngine.Flight;
        var worst = new List<FrameRecord>(WorstFrames + 1);
        FrameRecord row = default;

        for (long sequence = Math.Max(trip.FlightHead - FramesBefore, 0L); sequence < trip.FlightHead; sequence++)
        {
            if (!ring.TryCopy(sequence, (byte*)&row))
                continue;

            if (worst.Count == WorstFrames && row.BusyUnits <= worst[worst.Count - 1].BusyUnits)
                continue;

            int index = worst.Count;

            while (index > 0 && worst[index - 1].BusyUnits < row.BusyUnits)
                index--;

            worst.Insert(index, row);

            if (worst.Count > WorstFrames)
                worst.RemoveAt(WorstFrames);
        }

        return worst.Select(ToFrame).ToList();
    }

    private static IncidentFrame ToFrame(FrameRecord row)
    {
        var scopes = new uint[Scope.Count];

        for (int scope = 1; scope < Scope.Count; scope++)
            scopes[scope] = row.ScopeUnits[scope];

        int collection = (row.Flags & FrameFlags.Gen2) != 0 ? 2 : (row.Flags & FrameFlags.Gen1) != 0 ? 1 : (row.Flags & FrameFlags.Gen0) != 0 ? 0 : -1;

        return new IncidentFrame { Frame = (long)row.Frame, Ms = row.BusyUnits / 10000d, ScopeUnits = scopes, Collection = collection };
    }

    // A report holds a whole run, so it only becomes JSON when it's asked for, off the game thread. Lag is no time for a big string.
    private static void Keep(long id, IncidentResultResponse report)
    {
        s_finished[id] = report;
        s_order.Enqueue(id);

        while (s_order.Count > KeptReports)
            s_finished.TryRemove(s_order.Dequeue(), out _);
    }

    /// <summary>The state of a report, as JSON. Safe from any thread.</summary>
    public static string ResultJson(long id)
    {
        // In this order, or a report finishing between the two reads would look like it had never been.
        bool running = id == Volatile.Read(ref s_runningId);

        if (s_finished.TryGetValue(id, out IncidentResultResponse? report))
            return JsonConvert.SerializeObject(report);

        return JsonConvert.SerializeObject(new IncidentResultResponse { State = running ? RunState.Running : RunState.Unknown });
    }
}
