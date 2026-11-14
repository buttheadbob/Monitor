using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class MonitorRunsTests : IDisposable
{
    private const string Server = "server-1";

    private static readonly MonitorServer s_server = new(Server, "One", Server);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServers _servers = new();
    private readonly MonitorSettings _settings;
    private readonly MonitorRuns _runs;

    public MonitorRunsTests()
    {
        _settings = new MonitorSettings(_directory);
        _runs = new MonitorRuns(_servers, _directory, _settings, new FakeLifetime(), NullLogger<MonitorRuns>.Instance)
        {
            PollEvery = TimeSpan.FromMilliseconds(1),
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class FakeServers : IMonitorServers
    {
        public bool Refuse { get; set; }

        public RunResultResponse Result { get; set; } = new() { State = RunState.Finished, Seconds = 10d, SimSpeed = 0.9d, AverageFrameMs = 15d };

        public IReadOnlyList<MonitorServer> Connected() => [];

        public Task<DrillRootsResponse> DrillRootsAsync(string serverId, DrillRootsRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DrillStartResponse> StartDrillAsync(string serverId, DrillStartRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DrillResultResponse> DrillResultAsync(string serverId, DrillResultRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PatchListResponse> ListPatchesAsync(string serverId, PatchListRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AuditStartResponse> StartAuditAsync(string serverId, AuditStartRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AuditResultResponse> AuditResultAsync(string serverId, AuditResultRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IncidentResultResponse> IncidentResultAsync(string serverId, IncidentResultRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TriggerSetResponse> SetTriggersAsync(string serverId, TriggerSettings settings, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuntimeInfoResponse> RuntimeInfoAsync(string serverId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> PullHistoryAsync(string serverId, HistoryPullRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IdentityResponse> LookUpAsync(string serverId, IdentityRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RunStartResponse> StartRunAsync(string serverId, RunStartRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new RunStartResponse { Started = !Refuse, RunId = 1L, Message = Refuse ? "Busy." : null });

        public Task<RunResultResponse> RunResultAsync(string serverId, RunResultRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }

    private async Task RunAndWaitAsync()
    {
        await _runs.StartAsync(s_server, new RunStartRequest(), CancellationToken.None);

        for (int i = 0; i < 2000 && _runs.Running(Server); i++)
            await Task.Delay(5);

        Assert.False(_runs.Running(Server));
    }

    private string RunFile(string id) => Path.Combine(_directory, StateFiles.FolderFor(Server), "runs", id + ".json");

    [Fact]
    public async Task A_finished_run_is_saved()
    {
        await RunAndWaitAsync();

        SavedRunInfo run = Assert.Single(_runs.List(Server));

        Assert.Equal(0.9d, run.SimSpeed, 6);
        Assert.Equal(15d, _runs.Load(Server, run.Id)!.AverageFrameMs, 6);
    }

    [Fact]
    public async Task A_refused_run_says_why_and_isnt_left_running()
    {
        _servers.Refuse = true;

        string? note = await _runs.StartAsync(s_server, new RunStartRequest(), CancellationToken.None);

        Assert.Equal("Busy.", note);
        Assert.False(_runs.Running(Server));
    }

    [Fact]
    public async Task A_failed_run_says_so_and_saves_nothing()
    {
        _servers.Result = new RunResultResponse { State = RunState.Failed };

        await RunAndWaitAsync();

        Assert.NotNull(_runs.Problem(Server));
        Assert.Empty(_runs.List(Server));
    }

    [Fact]
    public async Task A_new_run_clears_the_last_runs_problem()
    {
        _servers.Result = new RunResultResponse { State = RunState.Failed };
        await RunAndWaitAsync();

        _servers.Result = new RunResultResponse { State = RunState.Finished };
        await RunAndWaitAsync();

        Assert.Null(_runs.Problem(Server));
    }

    [Fact]
    public async Task A_saved_run_can_be_deleted()
    {
        await RunAndWaitAsync();

        _runs.Delete(Server, _runs.List(Server)[0].Id);

        Assert.Empty(_runs.List(Server));
    }

    [Fact]
    public void Runs_past_retention_are_deleted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RunFile("x"))!);

        string old = DateTime.UtcNow.AddDays(-4).ToString("yyyyMMdd-HHmmss");
        string recent = DateTime.UtcNow.AddDays(-1).ToString("yyyyMMdd-HHmmss");
        File.WriteAllText(RunFile(old), "{}");
        File.WriteAllText(RunFile(recent), "{}");

        Assert.Equal(recent, Assert.Single(_runs.List(Server)).Id);
        Assert.False(File.Exists(RunFile(old)));
    }

    [Theory]
    [InlineData("../../settings")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("20260301")]
    public void Anything_but_a_run_id_never_reaches_a_path(string id)
    {
        // What the first of those would reach if it were ever put in a path. Nothing here would notice otherwise.
        string target = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(target, "{}");

        Assert.Null(_runs.Load(Server, id));

        _runs.Delete(Server, id);

        Assert.True(File.Exists(target));
    }

    [Fact]
    public void A_comparison_puts_the_biggest_change_first()
    {
        RunResultResponse before = Run(("Small", 1d), ("Big", 5d));
        RunResultResponse after = Run(("Small", 1.5d), ("Big", 9d));

        List<ComparedRow> rows = RunComparison.Compare(before, after, RunLists.Grids);

        Assert.Equal("Big", rows[0].Name);
        Assert.Equal(4d, rows[0].Change!.Value, 6);
    }

    [Fact]
    public void A_row_in_only_one_run_has_no_change_rather_than_a_wrong_one()
    {
        // Lists hold the top rows only, so a missing row isn't zero.
        List<ComparedRow> rows = RunComparison.Compare(Run(("Gone", 8d)), Run(("New", 3d), ("Both", 1d)), RunLists.Grids);

        ComparedRow gone = rows.Single(row => row.Name == "Gone");

        Assert.Null(gone.After);
        Assert.Null(gone.Change);
    }

    [Fact]
    public void Rows_with_a_change_come_before_rows_without_one()
    {
        RunResultResponse before = Run(("Both", 1d), ("Gone", 50d));
        RunResultResponse after = Run(("Both", 1.1d));

        Assert.Equal("Both", RunComparison.Compare(before, after, RunLists.Grids)[0].Name);
    }

    [Fact]
    public void A_comparison_only_looks_at_one_list()
    {
        RunResultResponse run = Run(("Ship", 1d));
        run.Rows.Add(new RunRow { List = RunLists.Players, Name = "Sam", MainMsPerFrame = 2d });

        Assert.Single(RunComparison.Compare(run, run, RunLists.Grids));
    }

    private static RunResultResponse Run(params (string Name, double Ms)[] rows)
    {
        var run = new RunResultResponse { State = RunState.Finished };

        foreach ((string name, double ms) in rows)
            run.Rows.Add(new RunRow { List = RunLists.Grids, Name = name, MainMsPerFrame = ms });

        return run;
    }
}
