using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Newtonsoft.Json;
using Sandbox.Game.World;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Hooks;
using SenX.Monitor.Magnetar.Reporting;
using SenX.Monitor.Magnetar.World;
using VRage.Game.Components;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>Called by rewritten code. Public, because generated code calls it.</summary>
public static class DrillShims
{
    public static void Record(long started, int site) => DrillDown.Record(Clock.Now() - started, site);
}

/// <summary>
/// Looks inside a method by timing every call it makes, with no help from whoever wrote it. It's one
/// patch on the method being looked at, not one on each thing it calls, so a method the JIT inlined
/// still counts. A rebuild takes a moment on the game thread, so this only runs when someone asks,
/// one method at a time, and comes out by itself. If the timing starts costing too much, it stops early.
/// </summary>
internal static partial class DrillDown
{
    public const int MaxSeconds = 120;

    internal const int MaxSites = 256;

    private const int MaxRootsListed = 200;
    private const int MaxKnown = 4096;
    private const int WholeMethod = 0;
    private const int ColumnsPerSite = 5;
    private const int MainColumn = 0;
    private const int WorkerColumn = 1;
    private const int MainCallsColumn = 2;
    private const int WorkerCallsColumn = 3;
    private const int PeakColumn = 4;

    // Past this the timing is hurting the server, however interesting the answer would be.
    private const double MostAddedMsPerFrame = 3d;
    private const ulong FramesBeforeJudging = 30UL;
    private const double InflatedShare = 0.1d;

    private static readonly string[] s_updateMethods =
    {
        "UpdateBeforeSimulation", "UpdateBeforeSimulation10", "UpdateBeforeSimulation100",
        "UpdateAfterSimulation", "UpdateAfterSimulation10", "UpdateAfterSimulation100",
        "UpdateOnceBeforeFrame", "Simulate",
    };

    private static readonly MethodInfo s_clockNow = typeof(Clock).GetMethod(nameof(Clock.Now))!;
    private static readonly MethodInfo s_record = typeof(DrillShims).GetMethod(nameof(DrillShims.Record))!;

    private static readonly long[] s_totals = new long[(MaxSites + 1) * ColumnsPerSite];
    private static readonly MethodBase?[] s_callees = new MethodBase?[MaxSites + 1];
    private static readonly Dictionary<MethodBase, int> s_sites = new();
    private static readonly Dictionary<string, MethodBase> s_known = new();

    private static MethodBase? s_patched;
    private static Action<DrillResultResponse>? s_done;
    private static string? s_resultJson;
    private static long s_number;
    private static string s_owner = string.Empty;
    private static string s_name = string.Empty;
    private static int s_seconds;
    private static int s_gameThread;
    private static int s_skipped;
    private static long s_deadline;
    private static ulong s_firstFrame;
    private static bool s_running;
    private static bool s_endedEarly;
    private static volatile bool s_open;

    /// <summary>True while one is going.</summary>
    public static bool Running => s_running;

    /// <summary>Things to start from, by part of their name. Game thread, because it reads session components.</summary>
    public static List<DrillRoot> Roots(string? text)
    {
        var methods = new List<MethodBase>();

        if (ServerInventory.PluginTypes is { } plugins)
            methods.AddRange(plugins.Select(PluginHooks.UpdateOf).Where(method => method != null)!);

        if (MySession.Static != null)
        {
            foreach (MySessionComponentBase component in MySession.Static.m_sessionComponents.Values)
                methods.AddRange(UpdatesOf(component.GetType()));
        }

        methods.AddRange(PatchList.PatchedByOthers());

        // Whatever timing has already seen, which is how a mod's game logic and callbacks get here.
        foreach (object meaning in KeyNames.Meanings())
        {
            if (meaning is Type type)
                methods.AddRange(UpdatesOf(type));
            else if (meaning is MethodBase method)
                methods.Add(method);
        }

        return methods
            .Where(Drillable)
            .Distinct()
            .Where(method => string.IsNullOrWhiteSpace(text) || FullNameOf(method).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(NameOf, StringComparer.Ordinal)
            .Take(MaxRootsListed)
            .Select(method => new DrillRoot { Id = Remember(method), Name = NameOf(method), Owner = OwnerOf(method) })
            .ToList();
    }

    private static string FullNameOf(MethodBase method) => (method.DeclaringType?.FullName ?? "?") + "." + method.Name;

    private static IEnumerable<MethodBase> UpdatesOf(Type type)
    {
        foreach (string name in s_updateMethods)
        {
            MethodInfo? method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

            // A class that doesn't override it would only show the empty base method.
            if (method != null && method.DeclaringType == type)
                yield return method;
        }
    }

    private static string NameOf(MethodBase method) => (method.DeclaringType?.Name ?? "?") + "." + method.Name;

    private static string OwnerOf(MethodBase method) => method.DeclaringType == null ? "Unknown" : LagReport.CodeOwner(method.DeclaringType);

    // A list can be reloaded and picked from later, so what's listed is remembered, and it's bounded.
    internal static string Remember(MethodBase method)
    {
        string id = PatchList.Describe(method);

        lock (s_known)
        {
            if (s_known.Count >= MaxKnown)
                s_known.Clear();

            s_known[id] = method;
        }

        return id;
    }

    private static bool Drillable(MethodBase method)
    {
        try
        {
            return !method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or ArgumentException or BadImageFormatException)
        {
            // A method built at runtime has no body to read, and one of those shouldn't fail a whole search.
            return false;
        }
    }

    // The body of an async or iterator method is in a generated class, not the method with its name.
    private static MethodBase BodyOf(MethodBase method)
    {
        StateMachineAttribute? machine = method.GetCustomAttribute<StateMachineAttribute>();

        return machine?.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? method;
    }

    /// <summary>Starts looking inside a method, on the game thread. <paramref name="done"/> is called on the game thread.</summary>
    public static DrillStartResponse Start(DrillStartRequest request, Action<DrillResultResponse>? done = null)
    {
        if (Worker.Closed)
            return new DrillStartResponse { Message = "Monitor has shut itself down. The server log says why." };

        if (s_running)
            return new DrillStartResponse { Message = "A drill-down is already going." };

        if (request.Seconds < 1 || request.Seconds > MaxSeconds)
            return new DrillStartResponse { Message = "A drill-down lasts from 1 to " + MaxSeconds + " seconds." };

        MethodBase? method;

        lock (s_known)
            s_known.TryGetValue(request.Id, out method);

        if (method == null)
            return new DrillStartResponse { Message = "That method isn't known. List them again." };

        if (!Drillable(method))
            return new DrillStartResponse { Message = "That method has no code of its own to look inside." };

        ClearSites();

        s_endedEarly = false;
        s_resultJson = null;
        s_done = done;
        s_seconds = request.Seconds;
        s_name = NameOf(method);
        s_owner = OwnerOf(method);
        s_gameThread = Environment.CurrentManagedThreadId;

        MethodBase body = BodyOf(method);

        try
        {
            Patch(body);
        }
        catch (Exception exception)
        {
            MonitorLog.Warning("Monitor could not look inside " + s_name + ": " + exception.Message);
            return new DrillStartResponse { Message = "That method can't be patched: " + exception.Message };
        }

        s_patched = body;
        s_firstFrame = FrameEngine.Frame;
        s_deadline = Clock.Now() + (s_seconds * Clock.Frequency);
        s_running = true;
        s_open = true;

        return new DrillStartResponse { Started = true, Number = ++s_number };
    }

    internal static void ClearSites()
    {
        Array.Clear(s_totals, 0, s_totals.Length);
        Array.Clear(s_callees, 0, s_callees.Length);
        s_sites.Clear();
        s_skipped = 0;
    }

    /// <summary>Ends a drill-down when it's due. Called every frame on the game thread.</summary>
    public static void Step()
    {
        if (!s_running)
            return;

        bool overBudget = CostingTooMuch();

        if (!overBudget && Clock.Now() < s_deadline)
            return;

        s_endedEarly = overBudget;
        Finish();
    }

    private static bool CostingTooMuch()
    {
        ulong frames = FrameEngine.Frame - s_firstFrame;

        if (frames < FramesBeforeJudging)
            return false;

        return TimingMsPerFrame(frames) > MostAddedMsPerFrame;
    }

    private static double TimingMsPerFrame(ulong frames)
    {
        long calls = 0L;

        for (int site = WholeMethod; site <= MaxSites; site++)
        {
            int at = site * ColumnsPerSite;
            calls += Volatile.Read(ref s_totals[at + MainCallsColumn]) + Volatile.Read(ref s_totals[at + WorkerCallsColumn]);
        }

        return NanosToMsPerFrame(calls * Calibration.TimedCallNanos, frames);
    }

    private static double NanosToMsPerFrame(double nanos, ulong frames) => nanos / Math.Max(frames, 1UL) / 1000000d;

    private static void Finish()
    {
        s_open = false;
        ulong frames = Math.Max(FrameEngine.Frame - s_firstFrame, 1UL);

        try
        {
            Unpatch(s_patched!);
        }
        catch (Exception exception)
        {
            MonitorLog.Warning("Monitor could not take out a drill-down: " + exception.Message);
        }

        s_running = false;
        s_patched = null;

        DrillResultResponse result;

        // Results come through the worker's update, and a failure there would shut all of Monitor down over one drill-down.
        try
        {
            result = Build(frames);
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not build a drill-down's result.", exception);
            result = new DrillResultResponse { State = RunState.Failed };
        }

        Publish(result);
    }

    private static void Publish(DrillResultResponse result)
    {
        result.Number = s_number;
        Volatile.Write(ref s_resultJson, JsonConvert.SerializeObject(result));

        Action<DrillResultResponse>? done = s_done;
        s_done = null;
        done?.Invoke(result);
    }

    private static DrillResultResponse Build(ulong frames)
    {
        var rows = new List<DrillRow>();
        long childMainCalls = 0L;
        long childWorkerCalls = 0L;
        double childMain = 0d;

        for (int site = 1; site <= MaxSites; site++)
        {
            int at = site * ColumnsPerSite;
            long mainCalls = s_totals[at + MainCallsColumn];
            long workerCalls = s_totals[at + WorkerCallsColumn];

            if (mainCalls + workerCalls == 0L || !(s_callees[site] is { } callee))
                continue;

            double main = Ms(at + MainColumn, mainCalls, frames);
            childMainCalls += mainCalls;
            childWorkerCalls += workerCalls;
            childMain += main;

            rows.Add(new DrillRow
            {
                Id = Drillable(callee) ? Remember(callee) : string.Empty,
                Name = NameOf(callee),
                Owner = OwnerOf(callee),
                CallsPerFrame = (mainCalls + workerCalls) / (double)frames,
                MainMs = main,
                WorkerMs = Ms(at + WorkerColumn, workerCalls, frames),
                PeakMs = Clock.ToUnits(s_totals[at + PeakColumn]) / 10000d,
            });
        }

        long wholeMainCalls = s_totals[MainCallsColumn];
        long wholeWorkerCalls = s_totals[WorkerCallsColumn];
        double wholeMain = WholeMs(MainColumn, wholeMainCalls, childMainCalls, frames);
        double timing = TimingMsPerFrame(frames);

        return new DrillResultResponse
        {
            State = RunState.Finished,
            Method = s_name,
            Owner = s_owner,
            Seconds = s_seconds,
            Frames = (long)frames,
            CallsPerFrame = (wholeMainCalls + wholeWorkerCalls) / (double)frames,
            MainMs = wholeMain,
            WorkerMs = WholeMs(WorkerColumn, wholeWorkerCalls, childWorkerCalls, frames),
            SelfMs = Math.Max(0d, wholeMain - childMain),
            PeakMs = Clock.ToUnits(s_totals[PeakColumn]) / 10000d,
            TimingMsPerFrame = timing,
            EndedEarly = s_endedEarly,
            Inflated = timing > wholeMain * InflatedShare,
            SkippedSites = s_skipped,
            Rows = rows.OrderByDescending(row => row.MainMs + row.WorkerMs).ToList(),
        };
    }

    private static double Ms(int column, long calls, ulong frames) =>
        Calibration.WithoutOverhead(Clock.ToUnits(s_totals[column]), calls) / frames / 10000d;

    // A method's whole time also holds the timing of every call inside it, which isn't work it did.
    private static double WholeMs(int column, long calls, long childCalls, ulong frames) =>
        Math.Max(0d, Ms(column, calls, frames) - NanosToMsPerFrame(childCalls * Calibration.TimedCallNanos, frames));

    /// <summary>The state of the latest drill-down, as JSON. Safe from any thread.</summary>
    public static string ResultJson()
    {
        string? json = Volatile.Read(ref s_resultJson);
        if (json != null)
            return json;

        return JsonConvert.SerializeObject(new DrillResultResponse { State = s_running ? RunState.Running : RunState.Unknown });
    }

    /// <summary>Takes out anything left in. Monitor shutting down must leave nothing behind.</summary>
    public static void RemoveAll()
    {
        bool wasRunning = s_running;

        s_open = false;
        s_running = false;
        s_patched = null;
        UnpatchAll();

        // Nothing will finish it now, and whoever asked is waiting for an answer.
        if (wasRunning)
            Publish(new DrillResultResponse { State = RunState.Failed });
    }

    /// <summary>Adds up one call. Runs on whichever thread made it, so totals are interlocked.</summary>
    internal static void Record(long ticks, int site)
    {
        if (!s_open)
            return;

        int at = site * ColumnsPerSite;
        bool main = Environment.CurrentManagedThreadId == s_gameThread;

        Interlocked.Add(ref s_totals[at + (main ? MainColumn : WorkerColumn)], ticks);
        Interlocked.Increment(ref s_totals[at + (main ? MainCallsColumn : WorkerCallsColumn)]);

        long peak = Volatile.Read(ref s_totals[at + PeakColumn]);

        while (ticks > peak)
        {
            long seen = Interlocked.CompareExchange(ref s_totals[at + PeakColumn], ticks, peak);

            if (seen == peak)
                break;

            peak = seen;
        }
    }

    // Which site a call belongs to, or -1 if it isn't worth timing.
    private static int SiteOf(MethodBase callee)
    {
        if (NotWorthTiming(callee))
            return -1;

        if (s_sites.TryGetValue(callee, out int site))
            return site;

        if (s_sites.Count >= MaxSites)
        {
            s_skipped++;
            return -1;
        }

        site = s_sites.Count + 1;
        s_sites[callee] = site;
        s_callees[site] = callee;
        return site;
    }

    // Property reads and the like on framework types are often called in tight loops, and timing them
    // would cost more than they do and only bury what matters.
    private static bool NotWorthTiming(MethodBase callee)
    {
        Type? type = callee.DeclaringType;

        if (type == null || type == typeof(object) || type == typeof(Clock) || type == typeof(DrillShims))
            return true;

        bool framework = type.Namespace != null && (type.Namespace == "System" || type.Namespace.StartsWith("System.", StringComparison.Ordinal));

        return framework && (callee.IsSpecialName || callee.IsConstructor);
    }
}
