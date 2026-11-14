using System;
using System.Collections.Generic;
using System.Linq;
using SenX.Monitor.Api;

namespace SenX.Monitor.Contracts;

/// <summary>Turns Monitor's own results into the public reply. Both APIs use this, so their replies can't drift apart.</summary>
public static class ReplyBuilder
{
    /// <summary>Why history can't answer for a list.</summary>
    public const string NotInHistory = "Background totals don't keep this list. Ask for a run.";

    /// <summary>The most rows a reply has for a list.</summary>
    public const int RowsPerList = 20;

    private const int RowsOfMods = 40;

    // Stands for nobody, or for no faction, so it isn't anyone's id.
    private const long NoOne = -1L;

    /// <summary>Every list, for a request that asks for all of them.</summary>
    public static readonly IReadOnlyList<MonitorList> AllLists = (MonitorList[])Enum.GetValues(typeof(MonitorList));

    /// <summary>Every list history keeps, for samples that ask for all of them.</summary>
    public static readonly IReadOnlyList<MonitorList> ListsInHistory = AllLists.Where(list => HistoryDimensionOf(list) != null).ToList();

    public static IReadOnlyCollection<MonitorList> ListsFor(MonitorRequest request) => request.Lists.Count == 0 ? AllLists : request.Lists;

    public static MonitorReply Refused(MonitorRequest request, string message, TimeSpan? retryAfter = null) =>
        new() { State = MonitorReplyState.Refused, Message = message, RetryAfter = retryAfter, Server = request.Server, Source = request.Source };

    public static MonitorReply Failed(MonitorRequest request, string message) =>
        new() { State = MonitorReplyState.Failed, Message = message, Server = request.Server, Source = request.Source };

    public static MonitorReply FromRun(MonitorRequest request, RunResultResponse run, string? runId, DateTime ended) => new()
    {
        State = MonitorReplyState.Finished,
        Server = request.Server,
        Source = request.Source,
        Accuracy = MonitorAccuracy.EveryCall,
        From = EndOf(run, ended).AddSeconds(-run.Seconds),
        To = EndOf(run, ended),
        Frame = new MonitorFrame(run.SimSpeed, run.AverageFrameMs, run.WorstFrameMs, Math.Max(0d, FrameSplit.BudgetMs - run.AverageFrameMs)),
        PartsMs = run.Parts,
        Run = new MonitorRunDetails(runId, run.Seconds, run.Frames, run.TimingMsPerFrame, run.Coverage, run.EndedEarly, run.RequestedBy) { LostSites = run.LostSites, MainWaitMs = run.MainWaitMsPerFrame, ParallelWorkerMs = run.ParallelWorkerMsPerFrame },
        Lists = ListsFor(request).Select(list => RunList(run, list)).ToList(),
        SlowCalls = run.SlowCalls.Select(call => new MonitorSlowCall(call.Ms, call.Where, call.Name, call.MainThread)).ToList(),
    };

    // A run saved before it said when it ended falls back on when it was saved.
    private static DateTime EndOf(RunResultResponse run, DateTime saved) =>
        run.EndedUtcTicks != 0L ? new DateTime(run.EndedUtcTicks, DateTimeKind.Utc) : saved;

    private static MonitorListReply RunList(RunResultResponse run, MonitorList list)
    {
        string name = RunListName(list);
        List<RunRow> rows = run.Rows.Where(row => row.List == name).ToList();

        if (list == MonitorList.Clusters && rows.Count == 0 && !run.ClustersTimed)
            return new MonitorListReply { List = list, Available = false, Why = run.ClusterProblem ?? "This run didn't time clusters." };

        return new MonitorListReply
        {
            List = list,
            Available = true,
            PerFrame = list != MonitorList.Clusters,
            Rows = rows.Select(row => ToRow(list, row)).ToList(),
        };
    }

    private static MonitorRow ToRow(MonitorList list, RunRow row)
    {
        var result = new MonitorRow(row.PlainName ?? row.Name, StableId(list, row.Key), row.MainMsPerFrame, row.WorkerMsPerFrame, row.PeakMs, row.CallsPerFrame, 1d)
        {
            BytesPerSecond = row.BytesPerSecond,
            Blocks = row.Blocks,
            Grids = row.Grids,
            SizeKm = row.SizeKm,

            // A cluster's time is one step with every physics thread to itself, so which thread it's on means nothing.
            Thread = list == MonitorList.Clusters ? MonitorThread.Unknown : MonitorThreads.Of(row.MainMsPerFrame, row.WorkerMsPerFrame),
        };

        // Only a cluster has these. Any other row keeps the shared empty list, so it still equals the same row from elsewhere.
        return row.TopGrids == null ? result : result with { Members = row.TopGrids.Select(grid => ToRow(MonitorList.Grids, grid)).ToList() };
    }

    /// <summary>The most rows a reply gives this list. Mods get more, because a server can run a lot of them and each should get its line.</summary>
    public static int MostRows(MonitorList list) => list == MonitorList.Mods ? RowsOfMods : RowsPerList;

    /// <summary>An id, where it outlasts a restart.</summary>
    public static long? StableId(MonitorList list, long key) =>
        key != 0L && key != NoOne && (list == MonitorList.Grids || list == MonitorList.Players || list == MonitorList.Factions || list == MonitorList.Scripts) ? key : (long?)null;

    private static string RunListName(MonitorList list)
    {
        switch (list)
        {
            case MonitorList.Grids:
                return RunLists.Grids;
            case MonitorList.BlockTypes:
                return RunLists.BlockTypes;
            case MonitorList.BlockDefinitions:
                return RunLists.Definitions;
            case MonitorList.Players:
                return RunLists.Players;
            case MonitorList.Factions:
                return RunLists.Factions;
            case MonitorList.Scripts:
                return RunLists.Scripts;
            case MonitorList.SessionComponents:
                return RunLists.Session;
            case MonitorList.GameLogic:
                return RunLists.Logic;
            case MonitorList.Callbacks:
                return RunLists.Callbacks;
            case MonitorList.Rpcs:
                return RunLists.Rpcs;
            case MonitorList.RpcsSent:
                return RunLists.RpcsSent;
            case MonitorList.EntityTypes:
                return RunLists.Entities;
            case MonitorList.Mods:
                return RunLists.Mods;
            default:
                return RunLists.Clusters;
        }
    }

    /// <summary>The list history keeps for this one, or null if it doesn't.</summary>
    public static HistoryDimension? HistoryDimensionOf(MonitorList list)
    {
        switch (list)
        {
            case MonitorList.Grids:
                return HistoryDimension.Grids;
            case MonitorList.BlockTypes:
                return HistoryDimension.BlockTypes;
            case MonitorList.BlockDefinitions:
                return HistoryDimension.Definitions;
            case MonitorList.Players:
                return HistoryDimension.Owners;
            case MonitorList.Factions:
                return HistoryDimension.Factions;
            case MonitorList.Scripts:
                return HistoryDimension.Scripts;
            case MonitorList.SessionComponents:
                return HistoryDimension.Session;
            case MonitorList.GameLogic:
                return HistoryDimension.Logic;
            case MonitorList.Callbacks:
                return HistoryDimension.Callbacks;
            case MonitorList.Rpcs:
                return HistoryDimension.Rpcs;
            case MonitorList.RpcsSent:
                return HistoryDimension.RpcsSent;
            case MonitorList.EntityTypes:
                return HistoryDimension.EntityTypes;
            case MonitorList.Mods:
                return HistoryDimension.Mods;
            default:
                return null;
        }
    }
}
