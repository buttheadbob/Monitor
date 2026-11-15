using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>Who patched what. Only reads the patcher's records, so it costs nothing until it's asked.</summary>
internal static partial class PatchList
{
    private const int PageSize = 150;

    /// <summary>One page, so a server with thousands of patches can't send one huge string.</summary>
    public static PatchListResponse Page(int skip)
    {
        List<PatchedMethodRow> rows = Everything();

        return new PatchListResponse
        {
            Total = rows.Count,
            Methods = rows.Skip(Math.Max(skip, 0)).Take(PageSize).ToList(),
            LostSites = PatchEngine.Results
                .Where(result => result.State == SiteState.TakenOver)
                .Select(result => new LostSiteRow
                {
                    Site = result.Target.Name,
                    Method = result.Caller,
                    Patched = result.Patched,
                    Expected = result.Target.Expected,
                    TakenBy = string.Join(", ", result.TakenBy),
                })
                .ToList(),
        };
    }

    /// <summary>What other plugins patched, by plugin, for chat.</summary>
    public static ChatReport Summary()
    {
        List<PatchedMethodRow> rows = Everything();

        if (rows.Count == 0)
            return "No other plugin has patched anything.";

        var report = new ChatReport().Title(rows.Count + " methods patched by other plugins");
        ChatTable table = report.Table("Plugin", "Methods", "Patches");

        var plugins = rows
            .SelectMany(row => row.Patches.Select(patch => (row.Method, patch.Plugin)))
            .GroupBy(entry => entry.Plugin)
            .OrderByDescending(plugin => plugin.Count());

        foreach (var plugin in plugins)
            table.Row(plugin.Key, plugin.Select(entry => entry.Method).Distinct().Count().ToString(CultureInfo.InvariantCulture), plugin.Count().ToString(CultureInfo.InvariantCulture));

        return report;
    }

    /// <summary>A method's name, which is also how a page asks for it to be audited.</summary>
    public static string Describe(MethodBase method)
    {
        string parameters = string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name));
        return (method.DeclaringType?.FullName ?? "?") + "." + method.Name + "(" + parameters + ")";
    }

    /// <summary>Every patched method that isn't only patched by Monitor, sorted so paging is stable.</summary>
    public static IEnumerable<MethodBase> PatchedByOthers() => AllPatched().Where(method => Rows(method).Count > 0);

    private static List<PatchedMethodRow> Everything() => AllPatched()
        .Select(method => new PatchedMethodRow { Method = Describe(method), Patches = Rows(method) })
        .Where(row => row.Patches.Count > 0)
        .OrderBy(row => row.Method, StringComparer.Ordinal)
        .ToList();
}

/// <summary>
/// Splits what patched methods cost into plugin prefixes, the original and plugin postfixes. Adding a
/// shim rebuilds a method, which takes a moment on the game thread, so methods go in and
/// come out one at a time across frames, and only when someone asks. Code a transpiler wrote into a
/// method can't be told apart from the original, so those methods are flagged instead.
/// </summary>
internal static partial class PatchAudit
{
    public const int MaxMethods = 8;
    public const int MaxSeconds = 120;

    private const int FramesBetweenPatches = 30;
    private const int ColumnsPerMethod = 4;
    private const int PrefixColumn = 0;
    private const int OriginalColumn = 1;
    private const int PostfixColumn = 2;
    private const int CallsColumn = 3;

    private static readonly long[] s_totals = new long[MaxMethods * ColumnsPerMethod];
    private static readonly MethodBase?[] s_methods = new MethodBase?[MaxMethods];
    private static readonly string?[] s_problems = new string?[MaxMethods];

    private static State s_state;
    private static int s_count;
    private static int s_next;
    private static int s_wait;
    private static int s_seconds;
    private static long s_deadline;
    private static ulong s_firstFrame;
    private static ulong s_lastFrame;
    private static volatile bool s_open;
    private static string? s_resultJson;
    private static long s_number;

    private enum State
    {
        Idle,
        Applying,
        Measuring,
        Removing,
    }

    /// <summary>Starts an audit, on the game thread.</summary>
    public static AuditStartResponse Start(AuditStartRequest request)
    {
        if (Worker.Closed)
            return new AuditStartResponse { Message = "Monitor has shut itself down. The server log says why." };

        if (s_state != State.Idle)
            return new AuditStartResponse { Message = "An audit is already going." };

        if (request.Seconds < 1 || request.Seconds > MaxSeconds)
            return new AuditStartResponse { Message = "An audit lasts from 1 to " + MaxSeconds + " seconds." };

        var wanted = new HashSet<string>(request.Methods);
        List<MethodBase> found = PatchList.PatchedByOthers().Where(method => wanted.Contains(PatchList.Describe(method))).Take(MaxMethods).ToList();

        if (found.Count == 0)
            return new AuditStartResponse { Message = "None of those methods are patched any more." };

        Array.Clear(s_totals, 0, s_totals.Length);
        Array.Clear(s_methods, 0, s_methods.Length);
        Array.Clear(s_problems, 0, s_problems.Length);

        for (int index = 0; index < found.Count; index++)
            s_methods[index] = found[index];

        BeginPatching();
        s_count = found.Count;
        s_next = 0;
        s_wait = 0;
        s_seconds = request.Seconds;
        s_resultJson = null;
        s_state = State.Applying;

        return new AuditStartResponse { Started = true, Number = ++s_number };
    }

    /// <summary>Moves an audit along. Called every frame on the game thread, and nothing happens while there isn't one.</summary>
    public static void Step()
    {
        switch (s_state)
        {
            case State.Applying:
                if (Waiting())
                    return;

                if (s_next < s_count)
                    Apply(s_next++);
                else
                    Open();

                break;

            case State.Measuring:
                if (Clock.Now() >= s_deadline)
                    Close();

                break;

            case State.Removing:
                if (Waiting())
                    return;

                if (s_next < s_count)
                    Remove(s_next++);
                else
                    Finish();

                break;
        }
    }

    private static bool Waiting()
    {
        if (++s_wait < FramesBetweenPatches)
            return true;

        s_wait = 0;
        return false;
    }

    private static void Apply(int index)
    {
        try
        {
            Patch(s_methods[index]!);
        }
        catch (Exception exception)
        {
            s_problems[index] = exception.Message;
            MonitorLog.Warning("Monitor could not audit " + PatchList.Describe(s_methods[index]!) + ": " + exception.Message);
        }
    }

    private static void Open()
    {
        s_firstFrame = FrameEngine.Frame;
        s_deadline = Clock.Now() + (s_seconds * Clock.Frequency);
        s_open = true;
        s_state = State.Measuring;
    }

    private static void Close()
    {
        s_open = false;
        s_lastFrame = FrameEngine.Frame;
        s_next = 0;
        s_wait = 0;
        s_state = State.Removing;
    }

    private static void Remove(int index)
    {
        try
        {
            Unpatch(s_methods[index]!);
        }
        catch (Exception exception)
        {
            MonitorLog.Warning("Monitor could not remove an audit shim: " + exception.Message);
        }
    }

    private static void Finish()
    {
        double frames = Math.Max(s_lastFrame - s_firstFrame, 1UL);
        var response = new AuditResultResponse { State = RunState.Finished, Number = s_number, Seconds = s_seconds, Frames = (long)frames };

        // Results come through the worker's update, and a failure there would shut all of Monitor down over one audit.
        try
        {
            for (int index = 0; index < s_count; index++)
                response.Rows.Add(Row(index, frames));
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not build an audit's result.", exception);
            response = new AuditResultResponse { State = RunState.Failed, Number = s_number };
        }

        Volatile.Write(ref s_resultJson, JsonConvert.SerializeObject(response));
        s_state = State.Idle;
    }

    private static AuditRow Row(int index, double frames)
    {
        MethodBase method = s_methods[index]!;
        List<PatchEntryRow> patches = PatchList.Rows(method);
        int at = index * ColumnsPerMethod;
        long calls = s_totals[at + CallsColumn];

        double Ms(int column) => Calibration.WithoutOverhead(Clock.ToUnits(s_totals[at + column]), calls) / frames / 10000d;

        return new AuditRow
        {
            Method = PatchList.Describe(method),
            Plugins = patches.Select(patch => patch.Plugin).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToList(),
            CallsPerFrame = calls / frames,
            PrefixMs = Ms(PrefixColumn),
            OriginalMs = Ms(OriginalColumn),
            PostfixMs = Ms(PostfixColumn),
            Transpiled = patches.Any(patch => patch.Kind == PatchKinds.Transpiler),
            Problem = s_problems[index],
        };
    }

    /// <summary>Adds up one call. Runs on whichever thread called the method, so its totals are interlocked.</summary>
    internal static void Record(MethodBase method, long prefix, long original, long postfix)
    {
        if (!s_open)
            return;

        int index = IndexOf(method);

        if (index >= 0)
            Record(index, prefix, original, postfix);
    }

    /// <summary>Adds up one call to a method already found.</summary>
    internal static void Record(int index, long prefix, long original, long postfix)
    {
        if (!s_open)
            return;

        int at = index * ColumnsPerMethod;
        Interlocked.Add(ref s_totals[at + PrefixColumn], prefix);
        Interlocked.Add(ref s_totals[at + OriginalColumn], original);
        Interlocked.Add(ref s_totals[at + PostfixColumn], postfix);
        Interlocked.Increment(ref s_totals[at + CallsColumn]);
    }

    private static int IndexOf(MethodBase method)
    {
        for (int index = 0; index < s_count; index++)
        {
            if (s_methods[index]?.MethodHandle == method.MethodHandle)
                return index;
        }

        return -1;
    }

    /// <summary>How the audit is going, as JSON. Safe from any thread.</summary>
    public static string ResultJson()
    {
        string? json = Volatile.Read(ref s_resultJson);
        if (json != null)
            return json;

        string state = s_state == State.Idle ? RunState.Unknown : RunState.Running;
        return JsonConvert.SerializeObject(new AuditResultResponse { State = state });
    }

    /// <summary>Takes out anything an unfinished audit left in. Monitor shutting down must leave nothing behind.</summary>
    public static void RemoveAll()
    {
        bool wasRunning = s_state != State.Idle;

        s_open = false;
        s_state = State.Idle;
        UnpatchAll();

        // Nothing will finish it now, and whoever asked is waiting for an answer.
        if (wasRunning)
            Volatile.Write(ref s_resultJson, JsonConvert.SerializeObject(new AuditResultResponse { State = RunState.Failed, Number = s_number }));
    }
}
