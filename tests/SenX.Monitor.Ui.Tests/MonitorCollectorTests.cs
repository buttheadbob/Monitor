using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

/// <summary>Drives the collector against the real worker packing, so both ends of a pull are real.</summary>
public sealed class MonitorCollectorTests : IDisposable
{
    private const string Server = "server-1";

    private static readonly MonitorServer s_server = new(Server, "One", Server);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServers _servers = new();
    private readonly HistoryStore _store;
    private readonly MonitorCollector _collector;
    private readonly MonitorSettings _settings;
    private readonly MonitorIncidents _incidents;
    private readonly MonitorRuns _runs;

    public MonitorCollectorTests()
    {
        MonitorCore.Initialize();
        FrameEngine.Start();
        HistoryWriter.StartedUtcTicks = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;

        _store = new HistoryStore(_directory, new MonitorSettings(_directory), NullLogger<HistoryStore>.Instance);
        _settings = new MonitorSettings(_directory);
        _incidents = NewIncidents(_servers);
        _runs = NewRuns(_servers);
        _collector = new MonitorCollector(_servers, _store, _incidents, _runs, _settings, NullLogger<MonitorCollector>.Instance);
    }

    // Other tests leave rows in the shared rings, so this brings the store up to date before a test writes its own.
    private async Task<int> CatchUpAsync()
    {
        for (int i = 0; i < 100 && _store.Position(Server).NextRollup < FrameEngine.Rollups.Head; i++)
            await _collector.PullAsync(s_server, CancellationToken.None);

        _servers.GroupsFrom = Sweep.Rows.Head;
        return _servers.Lookups;
    }

    [Fact]
    public async Task A_thread_jump_asks_the_server_for_no_name()
    {
        int lookups = await CatchUpAsync();
        long since = FrameEngine.UnitsSinceStart(Clock.Now());

        Sweep.WriteForTests(new GroupRecord
        {
            UnitsSinceStart = since,
            Dimension = GroupDimension.ThreadJumps,
            Key = 8801L,
            Representative = 8801L,
            WorkerUnits = 500U,
            Blocks = (uint)GroupDimension.Grids,
            Grids = ((uint)ThreadUse.Main << 8) | (uint)ThreadUse.Worker,
        });

        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Equal(lookups, _servers.Lookups);
        StoredMarker jump = Assert.Single(_store.Markers(Server, DateTime.MinValue, DateTime.MaxValue), marker => marker.Kind == StoredMarkerKind.ThreadJump);
        Assert.Equal("Grid #8801 moved from the game thread to worker threads", jump.Text);
    }

    [Fact]
    public async Task A_thread_jump_takes_the_name_its_totals_own_row_brought()
    {
        int lookups = await CatchUpAsync();
        long since = FrameEngine.UnitsSinceStart(Clock.Now());

        Sweep.WriteForTests(
            new GroupRecord { UnitsSinceStart = since, Dimension = GroupDimension.Grids, Key = 8802L, Representative = 8802L, WorkerUnits = 500U },
            new GroupRecord
            {
                UnitsSinceStart = since,
                Dimension = GroupDimension.ThreadJumps,
                Key = 8802L,
                Representative = 8802L,
                WorkerUnits = 500U,
                Blocks = (uint)GroupDimension.Grids,
                Grids = ((uint)ThreadUse.Main << 8) | (uint)ThreadUse.Worker,
            });

        await _collector.PullAsync(s_server, CancellationToken.None);

        // One lookup, for the grid's own row, which a total always asks for.
        Assert.Equal(lookups + 1, _servers.Lookups);
        StoredMarker jump = Assert.Single(_store.Markers(Server, DateTime.MinValue, DateTime.MaxValue), marker => marker.Kind == StoredMarkerKind.ThreadJump);
        Assert.Equal("Grid Name 8802 moved from the game thread to worker threads", jump.Text);
    }

    [Fact]
    public async Task A_pull_while_the_world_loads_isnt_a_restart()
    {
        await _collector.PullAsync(s_server, CancellationToken.None);
        long started = _store.Position(Server).StartedUtcTicks;

        HistoryWriter.StartedUtcTicks = 0L;
        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.NotEqual(0L, started);
        Assert.Equal(started, _store.Position(Server).StartedUtcTicks);
    }

    private MonitorRuns NewRuns(IMonitorServers servers) =>
        new(servers, _directory, new MonitorSettings(_directory), new NoLifetime(), NullLogger<MonitorRuns>.Instance);

    private sealed class NoLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private MonitorIncidents NewIncidents(IMonitorServers servers) => new(
        servers,
        _directory,
        new MonitorSettings(_directory),
        new MonitorBaselines(_store, new MonitorSettings(_directory), NullLogger<MonitorBaselines>.Instance),
        NullLogger<MonitorIncidents>.Instance);

    public void Dispose()
    {
        _collector.Dispose();

        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed unsafe class FakeServers : IMonitorServers
    {
        private readonly byte[] _buffer = new byte[HistoryWriter.MaxBytes];

        public int Lookups { get; private set; }

        /// <summary>Where group rows start for a pull. Unset, none are sent, because other tests share the ring.</summary>
        public long? GroupsFrom { get; set; }

        public List<string> Asked { get; } = [];

        public IReadOnlyList<MonitorServer> Connected() => [s_server];

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
            Task.FromResult(Incident);

        public Task<TriggerSetResponse> SetTriggersAsync(string serverId, TriggerSettings settings, CancellationToken cancellationToken)
        {
            TriggersSent++;
            return Task.FromResult(new TriggerSetResponse { Applied = true });
        }

        public IncidentResultResponse Incident { get; set; } = new();

        public Dictionary<long, RunResultResponse> Runs { get; } = [];

        public int TriggersSent { get; private set; }

        public RuntimeInfoResponse Runtime { get; set; } = new() { Plugins = [], Mods = [] };

        public Task<RuntimeInfoResponse> RuntimeInfoAsync(string serverId, CancellationToken cancellationToken) => Task.FromResult(Runtime);

        public Task<byte[]> PullHistoryAsync(string serverId, HistoryPullRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(serverId);

            fixed (byte* output = _buffer)
            {
                long groups = GroupsFrom is { } from ? Math.Max(from, request.FromGroups) : Sweep.Rows.Head;
                int length = HistoryWriter.Write(request.FromRollup, request.FromCensus, request.FromDetail, output, _buffer.Length, groups, request.FromMarkers);
                return Task.FromResult(_buffer.AsSpan(0, length).ToArray());
            }
        }

        public Task<IdentityResponse> LookUpAsync(string serverId, IdentityRequest request, CancellationToken cancellationToken)
        {
            Lookups++;
            var response = new IdentityResponse();

            foreach (IdentityQuery query in request.Queries)
                response.Answers.Add(new IdentityAnswer { Kind = query.Kind, Key = query.Key, Name = "Name " + query.Key });

            return Task.FromResult(response);
        }

        public Task<RunStartResponse> StartRunAsync(string serverId, RunStartRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RunResultResponse> RunResultAsync(string serverId, RunResultRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Runs.TryGetValue(request.RunId, out RunResultResponse? run) ? run : new RunResultResponse());
    }

    private static unsafe void Seconds(int count)
    {
        for (int second = 0; second < count; second++)
        {
            TreeCounters.Calls[Scope.Components] = 1L;
            FrameEngine.OnFrameEnd(Clock.Now());
            FrameEngine.CommitSecondNow();
        }
    }

    [Fact]
    public async Task A_pull_collects_everything_new()
    {
        Seconds(5);

        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Equal(FrameEngine.Rollups.Head, _store.Position(Server).NextRollup);
        Assert.NotNull(_store.Latest(Server));
    }

    [Fact]
    public async Task A_second_pull_carries_on_from_the_first()
    {
        Seconds(3);
        await _collector.PullAsync(s_server, CancellationToken.None);

        long position = _store.Position(Server).NextRollup;
        Seconds(2);
        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Equal(position + 2L, _store.Position(Server).NextRollup);
    }

    [Fact]
    public async Task A_long_backlog_is_collected_in_one_go()
    {
        Seconds(2000);

        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Equal(FrameEngine.Rollups.Head, _store.Position(Server).NextRollup);
    }

    [Fact]
    public async Task A_restarted_server_starts_positions_again()
    {
        Seconds(3);
        await _collector.PullAsync(s_server, CancellationToken.None);

        HistoryWriter.StartedUtcTicks += TimeSpan.TicksPerDay;
        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Equal(HistoryWriter.StartedUtcTicks, _store.Position(Server).StartedUtcTicks);
    }

    [Fact]
    public async Task A_replaced_cluster_node_carries_on_its_slots_history()
    {
        const string Slot = "cluster/slot-1";

        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddMinutes(-10).Ticks;
        Seconds(3);
        await _collector.PullAsync(new MonitorServer("node-1", "Slot 1", Slot), CancellationToken.None);

        int kept = _store.Points(Slot, DateTime.UtcNow.AddMinutes(-20), DateTime.UtcNow, 100000).Count;

        HistoryWriter.StartedUtcTicks += TimeSpan.TicksPerDay;
        await _collector.PullAsync(new MonitorServer("node-2", "Slot 1", Slot), CancellationToken.None);

        Assert.Equal(["node-1", "node-2"], _servers.Asked.Distinct());
        Assert.Equal(HistoryWriter.StartedUtcTicks, _store.Position(Slot).StartedUtcTicks);
        Assert.True(kept > 0);
        Assert.True(_store.Latest(Slot) != null);
    }

    private List<StoredMarker> MarkersOn(string history) =>
        _store.Markers(history, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

    [Fact]
    public async Task Saves_come_over_with_the_history()
    {
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
        Seconds(1);
        Markers.NoteSave(succeeded: true);
        Markers.Flush();

        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Equal(Markers.Rows.Head, _store.Position(Server).NextMarkers);
        Assert.Contains(MarkersOn(Server), marker => marker.Kind == StoredMarkerKind.Save);
    }

    [Fact]
    public async Task A_start_is_marked_once_however_many_pulls_follow()
    {
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
        Seconds(1);

        await _collector.PullAsync(s_server, CancellationToken.None);
        Seconds(1);
        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Single(MarkersOn(Server), marker => marker.Kind == StoredMarkerKind.Restart);
    }

    [Fact]
    public async Task A_server_still_starting_up_gets_asked_again()
    {
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
        _servers.Runtime = new RuntimeInfoResponse();
        Seconds(1);

        await _collector.PullAsync(s_server, CancellationToken.None);
        Assert.DoesNotContain(MarkersOn(Server), marker => marker.Kind == StoredMarkerKind.Restart);

        _servers.Runtime = new RuntimeInfoResponse { Plugins = [], Mods = [] };
        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Single(MarkersOn(Server), marker => marker.Kind == StoredMarkerKind.Restart);
    }

    [Fact]
    public async Task A_restart_is_compared_with_the_start_before_it()
    {
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-2).Ticks;
        _servers.Runtime = new RuntimeInfoResponse { Plugins = [], Mods = [new RuntimeItem { Id = "1", Name = "Mod One" }] };
        Seconds(1);
        await _collector.PullAsync(s_server, CancellationToken.None);

        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
        _servers.Runtime = new RuntimeInfoResponse { Plugins = [], Mods = [] };
        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Contains(MarkersOn(Server), marker => marker.Text == "Mod removed: Mod One");
    }

    [Fact]
    public async Task Limits_go_to_a_server_once_per_start()
    {
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
        Seconds(1);

        await _collector.PullAsync(s_server, CancellationToken.None);
        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Equal(1, _servers.TriggersSent);
    }

    [Fact]
    public async Task A_restarted_server_gets_its_limits_again()
    {
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-2).Ticks;
        Seconds(1);
        await _collector.PullAsync(s_server, CancellationToken.None);

        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Equal(2, _servers.TriggersSent);
    }

    [Fact]
    public async Task Changed_limits_go_out_on_the_next_pull()
    {
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
        Seconds(1);
        await _collector.PullAsync(s_server, CancellationToken.None);

        _settings.Triggers = new TriggerSettings { Seconds = 9 };
        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.Equal(2, _servers.TriggersSent);
    }

    [Fact]
    public async Task A_run_started_in_chat_reaches_the_runs_tab()
    {
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
        _servers.Runs[3L] = new RunResultResponse { State = RunState.Finished, RequestedBy = "Sam" };
        Seconds(1);
        Markers.AddRunFinished(3U);

        await _collector.PullAsync(s_server, CancellationToken.None);

        SavedRunInfo saved = Assert.Single(_runs.List(Server));
        Assert.Equal("Sam", _runs.Load(Server, saved.Id)!.RequestedBy);
    }

    [Fact]
    public async Task A_lag_trip_opens_an_incident_and_marks_the_charts()
    {
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
        Seconds(1);
        Markers.AddLag(7U);

        await _collector.PullAsync(s_server, CancellationToken.None);

        Assert.NotNull(_incidents.OpenSince(Server));
        Assert.Contains(MarkersOn(Server), marker => marker.Kind == StoredMarkerKind.Lag);
    }

    [Fact]
    public async Task A_restart_closes_an_open_incident()
    {
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-2).Ticks;
        Seconds(1);
        Markers.AddLag(7U);
        await _collector.PullAsync(s_server, CancellationToken.None);

        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
        await _collector.PullAsync(s_server, CancellationToken.None);

        // A real restart empties the rings. These are shared between tests, so the trip is read again as a new one, and only the first matters here.
        Assert.Equal("Server restarted", _incidents.List(Server)[^1].ClosedBy);
    }

    [Fact]
    public async Task Bytes_it_cant_read_are_an_error_and_not_a_silent_gap()
    {
        var broken = new BrokenServers();
        using var collector = new MonitorCollector(broken, _store, NewIncidents(broken), NewRuns(broken), _settings, NullLogger<MonitorCollector>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() => collector.PullAsync(s_server, CancellationToken.None));
    }

    private sealed class BrokenServers : IMonitorServers
    {
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
            Task.FromResult(new byte[200]);

        public Task<IdentityResponse> LookUpAsync(string serverId, IdentityRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RunStartResponse> StartRunAsync(string serverId, RunStartRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RunResultResponse> RunResultAsync(string serverId, RunResultRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
