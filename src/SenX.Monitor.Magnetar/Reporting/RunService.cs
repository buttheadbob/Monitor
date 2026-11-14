using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Sandbox;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Patching;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>
/// Lets Quasar start a measuring run and collect it afterwards. A run is rare and its rows carry
/// names, so these are plain JSON and not packed history. Requests arrive off the game thread, so
/// starting one hops onto it. The worker keeps the last few results, and every run that ends writes a
/// marker, so Quasar hears of it wherever it came from, even if it missed the answer to starting it.
/// </summary>
internal static class RunService
{
    private const int RowsPerList = 20;

    // Enough that a run finishing soon after another doesn't lose it before Quasar's next pull.
    private const int KeptRuns = 8;

    private static readonly FinishedRun?[] s_recent = new FinishedRun?[KeptRuns];

    private static long s_lastId;
    private static long s_runningId;

    // Made into JSON only if someone asks for it, off the game thread, because a big run makes a big string.
    private sealed class FinishedRun
    {
        private readonly RunResultResponse _result;
        private string? _json;

        public FinishedRun(long id, RunResultResponse result)
        {
            Id = id;
            _result = result;
        }

        public long Id { get; }

        public string Json => _json ??= JsonConvert.SerializeObject(_result);
    }

    /// <summary>
    /// Starts a run. <paramref name="started"/> is told its id on the game thread, before the run can
    /// possibly finish, which a continuation of the task can't promise.
    /// </summary>
    public static Task<RunStartResponse> StartAsync(RunStartRequest request, Action<long>? started = null)
    {
        if (request.Seconds < 1 || request.Seconds > CaptureArgs.MaxSeconds)
            return Task.FromResult(new RunStartResponse { Message = "Seconds must be from 1 to " + CaptureArgs.MaxSeconds + "." });

        return ServerApi.OnGameThreadAsync(() => Start(request, started), CancellationToken.None);
    }

    /// <summary>The state of a run, as JSON. Safe from any thread.</summary>
    public static string ResultJson(long runId)
    {
        // In this order, or a run finishing between the two reads would look like it had never been.
        bool running = runId == Volatile.Read(ref s_runningId);
        FinishedRun? finished = Volatile.Read(ref s_recent[runId & (KeptRuns - 1L)]);

        if (finished != null && finished.Id == runId)
            return finished.Json;

        return running ? JsonConvert.SerializeObject(new RunResultResponse { State = RunState.Running }) : Unknown();
    }

    private static string Unknown() => JsonConvert.SerializeObject(new RunResultResponse { State = RunState.Unknown });

    private static RunStartResponse Start(RunStartRequest request, Action<long>? onStarted)
    {
        long id = s_lastId + 1L;
        string? clusterProblem = request.Clusters ? PatchEngine.ClusterTurnProblem() : null;
        CaptureFilter? filter = LagRuns.BuildFilter(request.GridId, request.Player, request.Faction, noBlocks: false, request.BlockType, out string? problem);

        if (problem != null)
            return new RunStartResponse { Message = problem };

        CaptureStart started = Capture.TryStart(
            request.Seconds,
            filter,
            result => Finish(id, result, clusterProblem, request.RequestedBy),
            request.Clusters && clusterProblem == null);

        if (started != CaptureStart.Started)
            return new RunStartResponse { Message = "The run didn't start: " + started + ".", RetrySeconds = started == CaptureStart.Busy ? Math.Max(Capture.SecondsLeft, 1) : 0 };

        s_lastId = id;
        Volatile.Write(ref s_runningId, id);
        onStarted?.Invoke(id);
        ServerApi.Instance.NoteTriggeredRun(request.Seconds);

        return new RunStartResponse { Started = true, RunId = id, Message = clusterProblem };
    }

    /// <summary>Hands a chat command's run to Quasar and other plugins, so it isn't only in the chat. Game thread only.</summary>
    public static void Publish(CaptureResult result, string? clusterProblem, string requestedBy) =>
        Finish(++s_lastId, result, clusterProblem, requestedBy);

    /// <summary>Tells other plugins about a lag trip's run. Quasar gets it with the incident's evidence, so it isn't published here too.</summary>
    public static void NoteLagRun(RunResultResponse run) => ServerApi.Instance.OnRunFinished(++s_lastId, run);

    private static void Finish(long id, CaptureResult? result, string? clusterProblem, string requestedBy)
    {
        RunResultResponse response;

        try
        {
            response = result == null ? Failed() : LagReport.BuildRun(result, RowsPerList, clusterProblem);
            response.RequestedBy = requestedBy;
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not pack a run for Quasar.", exception);
            response = new RunResultResponse { State = RunState.Failed, Problem = "The run couldn't be totalled: " + exception.Message };
        }

        ServerApi.Instance.OnRunFinished(id, response);

        Volatile.Write(ref s_recent[id & (KeptRuns - 1L)], new FinishedRun(id, response));
        Volatile.Write(ref s_runningId, 0L);
        Markers.AddRunFinished((uint)id);
    }

    private static RunResultResponse Failed()
    {
        Exception fault = Capture.Fault ?? new InvalidOperationException("The run ended with no result.");

        MonitorLog.Error("Monitor's measurement failed.", fault);

        return new RunResultResponse { State = RunState.Failed, Problem = "The run failed: " + fault.Message };
    }
}
