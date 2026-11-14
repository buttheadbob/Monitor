using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>A saved run in a list, without its rows.</summary>
public sealed record SavedRunInfo(string Id, DateTime Time, double Seconds, double SimSpeed, double AverageFrameMs)
{
    public string RequestedBy { get; init; } = string.Empty;

    /// <summary>True for a run Monitor started by itself, which shouldn't take over the page when it arrives.</summary>
    public bool ByLagTrip => RequestedBy == RunLists.LagTrip;
}

/// <summary>One row compared between two runs. No value means it wasn't in that run's top rows, which isn't zero.</summary>
public sealed record ComparedRow(string Name, double? Before, double? After)
{
    public double? Change => Before.HasValue && After.HasValue ? After - Before : null;
}

/// <summary>Compares what two runs found.</summary>
public static class RunComparison
{
    /// <summary>One list from both runs, biggest change first. Rows only one run has come last.</summary>
    public static List<ComparedRow> Compare(RunResultResponse before, RunResultResponse after, string list)
    {
        Dictionary<string, double> Totals(RunResultResponse run) => run.Rows
            .Where(row => row.List == list)
            .GroupBy(row => row.Name)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.MainMsPerFrame + row.WorkerMsPerFrame));

        return Join(Totals(before), Totals(after));
    }

    /// <summary>
    /// Lines up two sets of figures, biggest change first. Rows only one set has come last. Sets are keyed by
    /// name unless <paramref name="nameOf"/> says what a key's name is.
    /// </summary>
    public static List<ComparedRow> Join(Dictionary<string, double> first, Dictionary<string, double> second, Func<string, string>? nameOf = null)
    {
        return first.Keys.Union(second.Keys)
            .Select(key => new ComparedRow(
                nameOf == null ? key : nameOf(key),
                first.TryGetValue(key, out double was) ? was : null,
                second.TryGetValue(key, out double now) ? now : null))
            .OrderByDescending(row => row.Change.HasValue)
            .ThenByDescending(row => Math.Abs(row.Change ?? 0d))
            .ThenByDescending(row => row.After ?? row.Before ?? 0d)
            .ToList();
    }
}

/// <summary>How a run asked for by a plugin turned out. A refusal says why, and a failure has no result.</summary>
public sealed record RunOutcome(RunResultResponse? Result, string? RunId, string? Refused, TimeSpan? RetryAfter, string? Problem);

/// <summary>
/// Starts runs and keeps their results. Quasar follows a run itself, so closing the page doesn't lose
/// it, and results stay for as long as the retention setting says. The worker keeps only the latest few.
/// </summary>
public sealed class MonitorRuns(
    IMonitorServers servers,
    string directory,
    MonitorSettings settings,
    IHostApplicationLifetime lifetime,
    ILogger<MonitorRuns> logger)
{
    private const string IdFormat = "yyyyMMdd-HHmmss";

    // Long enough for the worker to total a big world after the measuring ends.
    private static readonly TimeSpan s_grace = TimeSpan.FromMinutes(2);

    // Timing every call costs the server, so plugins can't keep asking. A trip counts too.
    private static readonly TimeSpan s_pluginGap = TimeSpan.FromSeconds(60);

    // Over the channel's own limit, so this is for a server that took the request and never answered it.
    private static readonly TimeSpan s_startLimit = TimeSpan.FromSeconds(45);

    private readonly object _saveGate = new();
    private readonly ConcurrentDictionary<string, ActiveRun> _running = new();
    private readonly ConcurrentDictionary<string, string> _problems = new();
    private readonly ConcurrentDictionary<string, DateTime> _nextPluginRun = new();
    private readonly ConcurrentDictionary<string, DateTime> _otherRuns = new();
    private readonly ConcurrentDictionary<(string Server, long RunId), bool> _followed = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> _finished = new();

    private sealed record ActiveRun(RunStartRequest Request, string RequestedBy, DateTime Ends)
    {
        public TaskCompletionSource<RunOutcome> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Raised when a run starts or ends, on whatever thread it happened.</summary>
    public event Action? Changed;

    /// <summary>Raised when a run has been saved, with its server and id.</summary>
    public event Action<string, string>? Saved;

    /// <summary>How often this checks on a run. A test shortens it.</summary>
    internal TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(1);

    public bool Running(string serverId) => _running.ContainsKey(serverId);

    /// <summary>Who started the run going on this server and when its measuring ends, if one is going. Nobody's name is known for a run started elsewhere.</summary>
    public (string RequestedBy, DateTime Ends)? Current(string serverId)
    {
        if (_running.TryGetValue(serverId, out ActiveRun? run))
            return (run.RequestedBy, run.Ends);

        return _otherRuns.TryGetValue(serverId, out DateTime ends) && ends > DateTime.UtcNow ? (string.Empty, ends) : null;
    }

    /// <summary>
    /// Says the worker began a run. One started here is already known. A chat command's or a lag trip's is
    /// only heard of at the next pull, so a short one can be over by then.
    /// </summary>
    public void NoteRunStarted(string serverId, DateTime time, int seconds)
    {
        DateTime ends = time.AddSeconds(seconds);
        DateTime next = ends + s_pluginGap;

        _otherRuns[serverId] = ends;
        _nextPluginRun.AddOrUpdate(serverId, next, (_, was) => was > next ? was : next);
    }

    /// <summary>Why the last run on this server has no result, if it hasn't.</summary>
    public string? Problem(string serverId) => _problems.GetValueOrDefault(serverId);

    /// <summary>Starts a run and follows it in the background. Returns a note for the admin, if there is one.</summary>
    public Task<string?> StartAsync(MonitorServer server, RunStartRequest request, CancellationToken cancellationToken, string requestedBy = "Monitor's page") =>
        BeginAsync(server, new ActiveRun(request, requestedBy, DateTime.UtcNow + TimeSpan.FromSeconds(request.Seconds)), forPlugin: false).WaitAsync(cancellationToken);

    // This doesn't take the caller's token. Once the request has gone the worker can start, so this has to stay to find out and follow the run.
    private async Task<string?> BeginAsync(MonitorServer server, ActiveRun active, bool forPlugin)
    {
        string history = server.HistoryId;

        if (!_running.TryAdd(history, active))
            return "A run is already going on this server.";

        _problems.TryRemove(history, out _);
        active.Request.RequestedBy = active.RequestedBy;
        logger.LogInformation("Monitor is measuring {Server} for {Seconds} s, for {RequestedBy}.", server.Name, active.Request.Seconds, active.RequestedBy);

        try
        {
            Task<RunStartResponse> asking = servers.StartRunAsync(server.Id, active.Request, lifetime.ApplicationStopping);

            if (await Task.WhenAny(asking, Task.Delay(s_startLimit, lifetime.ApplicationStopping)).ConfigureAwait(false) != asking)
                throw new TimeoutException("The server didn't answer.");

            RunStartResponse started = await asking.ConfigureAwait(false);

            if (!started.Started)
            {
                string message = started.Message ?? "The run didn't start.";
                _running.TryRemove(history, out _);
                active.Done.TrySetResult(new RunOutcome(null, null, message, started.RetrySeconds > 0 ? TimeSpan.FromSeconds(started.RetrySeconds) : null, null));
                return message;
            }

            if (forPlugin)
                _nextPluginRun[history] = active.Ends + s_pluginGap;

            _followed[(history, started.RunId)] = true;
            Changed?.Invoke();

            // Not tied to the page's token, because this must outlive the page.
            _ = Task.Run(() => FollowAsync(server, active, started.RunId));

            return started.Message == null ? null : "Clusters won't be timed, because " + started.Message + ".";
        }
        catch (Exception exception)
        {
            _running.TryRemove(history, out _);
            active.Done.TrySetResult(new RunOutcome(null, null, null, null, exception.Message));
            throw;
        }
    }

    /// <summary>
    /// Runs on a plugin's behalf and waits for the result. Every run measures everything, so a request
    /// joins one already going when that gives it what it wants. A normal "no" comes back as an outcome.
    /// </summary>
    public async Task<RunOutcome> RunAsync(MonitorServer server, RunStartRequest request, string requestedBy, CancellationToken cancellationToken)
    {
        string history = server.HistoryId;
        DateTime now = DateTime.UtcNow;

        if (_running.TryGetValue(history, out ActiveRun? going))
        {
            if (CanShare(going.Request, request))
                return await going.Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return new RunOutcome(null, null, "A run that can't be shared is going on this server.", ToolBuilder.RetryAfter(going.Ends, now), null);
        }

        if (_nextPluginRun.TryGetValue(history, out DateTime next) && now < next)
            return new RunOutcome(null, null, "Runs from plugins are limited, because each one costs the server.", next - now, null);

        var mine = new ActiveRun(request, requestedBy, now + TimeSpan.FromSeconds(request.Seconds));
        string? note = await BeginAsync(server, mine, forPlugin: true).WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_running.TryGetValue(history, out ActiveRun? started))
        {
            // Another request can get in between the check above and this one starting.
            if (!ReferenceEquals(started, mine) && !CanShare(started.Request, request))
                return new RunOutcome(null, null, "A run that can't be shared is going on this server.", ToolBuilder.RetryAfter(started.Ends, DateTime.UtcNow), null);

            return await started.Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Refused, or already over. A trip or chat command having the worker busy says how long it has left.
        return mine.Done.Task.IsCompleted
            ? await mine.Done.Task.ConfigureAwait(false)
            : new RunOutcome(null, null, note ?? "The run didn't start.", null, null);
    }

    /// <summary>Keeps the run a lag trip made, so it's on the page with the others and other plugins hear of it.</summary>
    public void SaveLagRun(string serverId, RunResultResponse run)
    {
        if (Save(serverId, run) != null)
            Changed?.Invoke();
    }

    /// <summary>Says Monitor just ran something by itself, so plugins wait their turn after it too.</summary>
    public void NoteTriggeredRun(string serverId, TimeSpan length) => _nextPluginRun[serverId] = DateTime.UtcNow + length + s_pluginGap;

    private static bool CanShare(RunStartRequest going, RunStartRequest wanted) =>
        !going.Narrows && !wanted.Narrows && (going.Clusters || !wanted.Clusters);

    /// <summary>Says the worker finished a run. It says so for every run, wherever it came from.</summary>
    public void NoteRunFinished(string serverId, long runId) =>
        _finished.GetOrAdd(serverId, _ => new ConcurrentQueue<long>()).Enqueue(runId);

    /// <summary>
    /// Saves the finished runs nobody here was following, so a chat command's run is on the page with the others, and
    /// so is one whose start timed out. One that can't be read stays for the next pull.
    /// </summary>
    public async Task CollectFinishedRunsAsync(MonitorServer server, CancellationToken cancellationToken)
    {
        string history = server.HistoryId;

        if (!_finished.TryGetValue(history, out ConcurrentQueue<long>? waiting))
            return;

        while (waiting.TryPeek(out long runId))
        {
            if (!_followed.TryRemove((history, runId), out _))
            {
                RunResultResponse result = await servers.RunResultAsync(server.Id, new RunResultRequest { RunId = runId }, cancellationToken).ConfigureAwait(false);

                if (result.State == RunState.Finished)
                {
                    Save(history, result);
                    Changed?.Invoke();
                }
            }

            waiting.TryDequeue(out _);
        }
    }

    /// <summary>Forgets a restarted server's runs, because its run numbers have started again.</summary>
    public void ForgetRuns(string serverId)
    {
        _finished.TryRemove(serverId, out _);

        foreach ((string Server, long RunId) key in _followed.Keys)
        {
            if (key.Server == serverId)
                _followed.TryRemove(key, out _);
        }
    }

    private async Task FollowAsync(MonitorServer server, ActiveRun active, long runId)
    {
        string history = server.HistoryId;
        CancellationToken stopping = lifetime.ApplicationStopping;
        DateTime giveUp = active.Ends + s_grace;
        RunOutcome outcome = new(null, null, null, null, "The server never finished that run.");

        try
        {
            while (DateTime.UtcNow < giveUp)
            {
                await Task.Delay(PollEvery, stopping).ConfigureAwait(false);

                RunResultResponse result = await servers.RunResultAsync(server.Id, new RunResultRequest { RunId = runId }, stopping).ConfigureAwait(false);

                if (result.State == RunState.Running)
                    continue;

                if (result.State == RunState.Finished)
                {
                    result.RequestedBy = active.RequestedBy;
                    string? id = Save(history, result);
                    outcome = new RunOutcome(result, id, null, null, null);
                }
                else
                {
                    _problems[history] = result.Problem ?? "The run failed. The details are in the server log.";
                    outcome = new RunOutcome(null, null, null, null, _problems[history]);
                }

                return;
            }

            _problems[history] = "The server never finished that run.";
        }
        catch (OperationCanceledException)
        {
            // Quasar is shutting down.
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            // A server restarting mid-run ends up here.
            _problems[history] = "The server stopped answering: " + exception.Message;
            outcome = new RunOutcome(null, null, null, null, _problems[history]);
        }
        finally
        {
            _running.TryRemove(history, out _);
            active.Done.TrySetResult(outcome);
            Changed?.Invoke();
        }
    }

    /// <summary>This server's saved runs, newest first. Runs past the retention setting are deleted on the way.</summary>
    public List<SavedRunInfo> List(string serverId)
    {
        Prune(serverId);

        string folder = Folder(serverId);
        if (!Directory.Exists(folder))
            return [];

        var runs = new List<SavedRunInfo>();

        foreach (string path in Directory.GetFiles(folder, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(path);

            if (!TryTimeOf(id, out DateTime time))
                continue;

            RunResultResponse? run = Read(path);
            if (run != null)
                runs.Add(new SavedRunInfo(id, time, run.Seconds, run.SimSpeed, run.AverageFrameMs) { RequestedBy = run.RequestedBy });
        }

        return runs.OrderByDescending(run => run.Time).ToList();
    }

    /// <summary>Deletes runs past the retention setting, going only by their names, so it reads nothing.</summary>
    public void Prune(string serverId)
    {
        string folder = Folder(serverId);

        if (!Directory.Exists(folder))
            return;

        DateTime oldest = DateTime.UtcNow - settings.Retention;

        foreach (string path in Directory.GetFiles(folder, "*.json"))
        {
            if (TryTimeOf(Path.GetFileNameWithoutExtension(path), out DateTime time) && time < oldest)
                TryDelete(path);
        }

        // What a crash left half written. Each run's name is new, so nothing would ever replace these.
        foreach (string path in Directory.GetFiles(folder, "*" + StateFiles.Unfinished))
        {
            if (File.GetLastWriteTimeUtc(path) < oldest)
                TryDelete(path);
        }
    }

    private static bool TryTimeOf(string id, out DateTime time) =>
        DateTime.TryParseExact(id, IdFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);

    public RunResultResponse? Load(string serverId, string id)
    {
        string? path = PathOf(serverId, id);
        return path != null && File.Exists(path) ? Read(path) : null;
    }

    public void Delete(string serverId, string id)
    {
        string? path = PathOf(serverId, id);
        if (path != null)
            TryDelete(path);
    }

    private string Folder(string serverId) =>
        Path.Combine(directory, StateFiles.FolderFor(serverId), "runs");

    // Ids reach this from the page, so anything that isn't an id never touches a path.
    private string? PathOf(string serverId, string id) =>
        DateTime.TryParseExact(id, IdFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? Path.Combine(Folder(serverId), id + ".json")
            : null;

    private string? Save(string serverId, RunResultResponse result)
    {
        try
        {
            string folder = Folder(serverId);
            Directory.CreateDirectory(folder);
            string id;

            // A page's run and a lag run are saved from different threads, and either could take the other's name.
            lock (_saveGate)
            {
                DateTime time = DateTime.UtcNow;
                id = time.ToString(IdFormat, CultureInfo.InvariantCulture);

                // Ids go by the second, and several lag runs can arrive together after Quasar has been away.
                while (File.Exists(Path.Combine(folder, id + ".json")))
                {
                    time = time.AddSeconds(1d);
                    id = time.ToString(IdFormat, CultureInfo.InvariantCulture);
                }

                StateFiles.Write(Path.Combine(folder, id + ".json"), JsonSerializer.Serialize(result));
            }

            Saved?.Invoke(serverId, id);
            return id;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Monitor could not save a run.");
            _problems[serverId] = "The run finished, but it couldn't be saved.";
            return null;
        }
    }

    private RunResultResponse? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<RunResultResponse>(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Monitor could not read a saved run.");
            return null;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Monitor could not delete a saved run.");
        }
    }
}
