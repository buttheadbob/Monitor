using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SenX.Monitor.Api;

namespace SenX.Monitor.Contracts;

/// <summary>What every host's tools have in common, so their answers can't drift apart.</summary>
public static class ToolBuilder
{
    /// <summary>The least time between two costly things a plugin can ask for. They cost the whole server.</summary>
    public static readonly TimeSpan PluginGap = TimeSpan.FromSeconds(60);

    /// <summary>The least time between two searches for a method, because each one takes a game frame.</summary>
    public static readonly TimeSpan FindGap = TimeSpan.FromSeconds(10);

    public const int MaxInsideSeconds = 120;
    public const int MaxAuditSeconds = 120;

    private const string NeedsAName = "This needs to say who asked for it.";
    private const string Overtaken = "A newer one has finished since, and only the latest result is kept. Ask again.";

    private static readonly TimeSpan s_soon = TimeSpan.FromSeconds(5);

    /// <summary>How long until something ends. One past its end is still being totalled, so that's a short wait, never a negative one.</summary>
    public static TimeSpan RetryAfter(DateTime ends, DateTime now) => ends > now ? ends - now : s_soon;

    public static MonitorRequest CostsRequest(string server, DateTime from, DateTime to, IReadOnlyCollection<MonitorList> lists) => new()
    {
        Server = server,
        Source = MonitorSource.History,
        Lists = lists,
        From = from,
        To = to,
    };

    public static MonitorRequest RunRequest(string server, MonitorRunOptions options) => new()
    {
        Server = server,
        Source = MonitorSource.Run,
        Lists = options.Lists,
        Seconds = options.Seconds,
        GridId = options.GridId,
        Player = options.Player,
        Faction = options.Faction,
        BlockType = options.BlockType,
        RequestedBy = options.RequestedBy,
    };

    public static MonitorRequest SavedRunRequest(string server, string? id, IReadOnlyCollection<MonitorList> lists) => new()
    {
        Server = server,
        Source = MonitorSource.SavedRun,
        RunId = id,
        Lists = lists,
    };

    /// <summary>Why a costly request can't go ahead, or null if it can.</summary>
    public static string? InspectProblem(string requestedBy, int seconds, int mostSeconds, DateTime nextAllowed, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(requestedBy))
            return NeedsAName;

        if (seconds < 1 || seconds > mostSeconds)
            return "Seconds must be from 1 to " + mostSeconds + ".";

        if (now < nextAllowed)
            return "Plugins can only do this once a minute. Try again in " + Math.Ceiling((nextAllowed - now).TotalSeconds) + " s.";

        return null;
    }

    public static MonitorMethod ToMethod(DrillRoot root) => new(root.Id, root.Name, root.Owner);

    public static MonitorInside InsideRefused(string message) => new() { State = MonitorReplyState.Refused, Message = message };

    public static MonitorInside InsideFailed(string message) => new() { State = MonitorReplyState.Failed, Message = message };

    public static MonitorInside ToInside(DrillResultResponse result, long number) => result.Number != number ? InsideFailed(Overtaken) : new()
    {
        State = result.State == RunState.Finished ? MonitorReplyState.Finished : MonitorReplyState.Failed,
        Method = result.Method,
        Owner = result.Owner,
        Seconds = result.Seconds,
        CallsPerFrame = result.CallsPerFrame,
        MainMs = result.MainMs,
        WorkerMs = result.WorkerMs,
        SelfMs = result.SelfMs,
        PeakMs = result.PeakMs,
        TimingMsPerFrame = result.TimingMsPerFrame,
        EndedEarly = result.EndedEarly,
        Inflated = result.Inflated,
        Rows = result.Rows.Select(row => new MonitorInsideRow(row.Id, row.Name, row.Owner, row.CallsPerFrame, row.MainMs, row.WorkerMs, row.PeakMs)).ToList(),
    };

    public static MonitorPatchedMethod ToPatched(PatchedMethodRow row) =>
        new(row.Method, row.Patches.Select(patch => new MonitorPatchEntry(patch.Kind, patch.Plugin, patch.Patch, patch.Priority)).ToList());

    public static MonitorAudit AuditRefused(string message) => new() { State = MonitorReplyState.Refused, Message = message };

    public static MonitorAudit AuditFailed(string message) => new() { State = MonitorReplyState.Failed, Message = message };

    public static MonitorAudit ToAudit(AuditResultResponse result, long number) => result.Number != number ? AuditFailed(Overtaken) : new()
    {
        State = result.State == RunState.Finished ? MonitorReplyState.Finished : MonitorReplyState.Failed,
        Seconds = result.Seconds,
        Rows = result.Rows.Select(row => new MonitorAuditRow(row.Method, row.Plugins, row.CallsPerFrame, row.PrefixMs, row.OriginalMs, row.PostfixMs, row.Transpiled, row.Problem)).ToList(),
    };

    /// <summary>Two replies for the same lists, joined by name, so it works the same from any host.</summary>
    public static MonitorComparison Compare(MonitorReply before, MonitorReply after)
    {
        var lists = new List<MonitorComparedList>();

        foreach (MonitorListReply list in before.Lists)
        {
            if (!list.Available)
                continue;

            MonitorListReply? other = after.Lists.FirstOrDefault(candidate => candidate.List == list.List);

            Dictionary<(long?, string), MonitorRow> first = ByIdentity(list.Rows);
            Dictionary<(long?, string), MonitorRow> second = ByIdentity(other?.Rows ?? new List<MonitorRow>());

            List<MonitorComparedRow> rows = first.Keys.Union(second.Keys)
                .Select(identity => Joined(identity, first, second))
                .OrderByDescending(row => Math.Abs(row.ChangeMs ?? row.AfterMs ?? row.BeforeMs ?? 0d))
                .ToList();

            lists.Add(new MonitorComparedList(list.List, rows));
        }

        return new MonitorComparison(before.Frame, after.Frame, lists);
    }

    // Two grids can share a name, so anything with an id is told apart by it.
    private static Dictionary<(long?, string), MonitorRow> ByIdentity(IEnumerable<MonitorRow> rows)
    {
        var known = new Dictionary<(long?, string), MonitorRow>();

        foreach (MonitorRow row in rows)
            known[(row.Id, row.Id.HasValue ? string.Empty : row.Name)] = row;

        return known;
    }

    private static MonitorComparedRow Joined((long? Id, string Name) identity, Dictionary<(long?, string), MonitorRow> first, Dictionary<(long?, string), MonitorRow> second)
    {
        first.TryGetValue(identity, out MonitorRow? was);
        second.TryGetValue(identity, out MonitorRow? now);

        return new MonitorComparedRow((was ?? now)!.Name, identity.Id, Cost(was), Cost(now));
    }

    private static double? Cost(MonitorRow? row) => row == null ? null : row.MainMs + row.WorkerMs;

    /// <summary>Reads something until it's stopped running.</summary>
    public static async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> running, TimeSpan every, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(every, cancellationToken).ConfigureAwait(false);

            T result = await read().ConfigureAwait(false);

            if (!running(result))
                return result;
        }
    }
}
