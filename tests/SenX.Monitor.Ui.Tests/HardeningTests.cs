using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

/// <summary>What a damaged file, a bad request or a hostile name must not be able to do to the host.</summary>
public sealed class HardeningTests : IDisposable
{
    private const string Server = "server-1";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedServers _servers = new();

    public HardeningTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private HistoryStore NewStore() => new(_directory, new MonitorSettings(_directory), NullLogger<HistoryStore>.Instance);

    private string ServerFolder(string id)
    {
        string folder = Path.Combine(_directory, StateFiles.FolderFor(id));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed class FailingServers(ScriptedServers inner) : IMonitorServers
    {
        public IReadOnlyList<MonitorServer> Connected() => throw new InvalidOperationException("Broken on purpose.");

        public Task<RuntimeInfoResponse> RuntimeInfoAsync(string serverId, CancellationToken cancellationToken) => inner.RuntimeInfoAsync(serverId, cancellationToken);

        public Task<byte[]> PullHistoryAsync(string serverId, HistoryPullRequest request, CancellationToken cancellationToken) => inner.PullHistoryAsync(serverId, request, cancellationToken);

        public Task<IdentityResponse> LookUpAsync(string serverId, IdentityRequest request, CancellationToken cancellationToken) => inner.LookUpAsync(serverId, request, cancellationToken);

        public Task<RunStartResponse> StartRunAsync(string serverId, RunStartRequest request, CancellationToken cancellationToken) => inner.StartRunAsync(serverId, request, cancellationToken);

        public Task<RunResultResponse> RunResultAsync(string serverId, RunResultRequest request, CancellationToken cancellationToken) => inner.RunResultAsync(serverId, request, cancellationToken);

        public Task<DrillRootsResponse> DrillRootsAsync(string serverId, DrillRootsRequest request, CancellationToken cancellationToken) => inner.DrillRootsAsync(serverId, request, cancellationToken);

        public Task<DrillStartResponse> StartDrillAsync(string serverId, DrillStartRequest request, CancellationToken cancellationToken) => inner.StartDrillAsync(serverId, request, cancellationToken);

        public Task<DrillResultResponse> DrillResultAsync(string serverId, DrillResultRequest request, CancellationToken cancellationToken) => inner.DrillResultAsync(serverId, request, cancellationToken);

        public Task<PatchListResponse> ListPatchesAsync(string serverId, PatchListRequest request, CancellationToken cancellationToken) => inner.ListPatchesAsync(serverId, request, cancellationToken);

        public Task<AuditStartResponse> StartAuditAsync(string serverId, AuditStartRequest request, CancellationToken cancellationToken) => inner.StartAuditAsync(serverId, request, cancellationToken);

        public Task<AuditResultResponse> AuditResultAsync(string serverId, AuditResultRequest request, CancellationToken cancellationToken) => inner.AuditResultAsync(serverId, request, cancellationToken);

        public Task<IncidentResultResponse> IncidentResultAsync(string serverId, IncidentResultRequest request, CancellationToken cancellationToken) => inner.IncidentResultAsync(serverId, request, cancellationToken);

        public Task<TriggerSetResponse> SetTriggersAsync(string serverId, TriggerSettings settings, CancellationToken cancellationToken) => inner.SetTriggersAsync(serverId, settings, cancellationToken);
    }

    [Fact]
    public async Task A_failure_in_collection_doesnt_end_the_service()
    {
        var settings = new MonitorSettings(_directory);
        HistoryStore store = NewStore();
        var failing = new FailingServers(_servers);
        var baselines = new MonitorBaselines(store, settings, NullLogger<MonitorBaselines>.Instance);
        var incidents = new MonitorIncidents(failing, _directory, settings, baselines, NullLogger<MonitorIncidents>.Instance);
        var runs = new MonitorRuns(failing, _directory, settings, new StoppedLifetime(), NullLogger<MonitorRuns>.Instance);
        using var collector = new MonitorCollector(failing, store, incidents, runs, settings, NullLogger<MonitorCollector>.Instance);

        await collector.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2.5d));

        // A service that faults takes the whole host with it, and this is the state that would show it.
        Assert.False(collector.ExecuteTask!.IsCompleted);

        await collector.StopAsync(CancellationToken.None);
    }

    private sealed class StoppedLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    [Fact]
    public void A_known_server_file_with_no_id_is_skipped()
    {
        File.WriteAllText(Path.Combine(ServerFolder("broken"), "server.json"), "{\"LastSeen\":\"2026-01-01T00:00:00Z\"}");

        Assert.Empty(NewStore().KnownServers());
    }

    [Fact]
    public void A_position_with_times_that_cant_be_is_ignored()
    {
        File.WriteAllText(Path.Combine(ServerFolder(Server), "position.json"), JsonSerializer.Serialize(new CollectorPosition { LastSample = -5L, LagUntil = long.MaxValue, PendingSample = long.MinValue }));

        Assert.Equal(-5L, NewStore().Position(Server).LastSample);
    }

    [Theory]
    [InlineData("EU-1", "EU_1")]
    [InlineData("a/b", "a\\b")]
    [InlineData("x.y", "x y")]
    public void Ids_that_differ_only_in_punctuation_get_their_own_folders(string first, string second)
    {
        Assert.NotEqual(StateFiles.FolderFor(first), StateFiles.FolderFor(second));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../../etc")]
    [InlineData("C:\\Windows")]
    [InlineData("")]
    public void A_folder_is_always_one_name_inside_the_data_folder(string id)
    {
        string folder = StateFiles.FolderFor(id);

        Assert.NotEmpty(folder);
        Assert.Equal(folder, Path.GetFileName(folder));
        Assert.DoesNotContain("..", folder, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_id_keeps_its_name()
    {
        Assert.Equal("server1", StateFiles.FolderFor("server1"));
    }

    [Fact]
    public void Writing_a_state_file_replaces_it_and_leaves_nothing_behind()
    {
        string path = Path.Combine(_directory, "state.json");

        StateFiles.Write(path, "one");
        StateFiles.Write(path, "two");

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Damaged_settings_are_kept_and_nothing_gets_deleted_because_of_them()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{ \"RetentionDays\": 7, \"Webho");

        var settings = new MonitorSettings(_directory);

        Assert.Equal(MonitorSettings.MaxRetentionDays, settings.RetentionDays);
        Assert.Single(Directory.GetFiles(_directory, "settings.json.damaged-*"));
        Assert.NotNull(settings.TakeProblem());
        Assert.Null(settings.TakeProblem());
    }

    [Theory]
    [InlineData("{\"RetentionDays\":3,\"Triggers\":null}", 3)]
    [InlineData("{\"RetentionDays\":3,\"Webhooks\":null}", 3)]
    public void Missing_parts_of_settings_get_their_defaults(string json, int days)
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), json);

        var settings = new MonitorSettings(_directory);

        Assert.Equal(days, settings.RetentionDays);
        Assert.NotNull(settings.Triggers);
        Assert.Null(settings.TakeProblem());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"RetentionDays\":0}")]
    [InlineData("{\"RetentionDays\":3,\"Web")]
    public void Settings_that_cant_be_right_count_as_damage(string json)
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), json);

        // Asked first, the way the collector does, so finding out can't depend on something else having read them.
        Assert.NotNull(new MonitorSettings(_directory).TakeProblem());
        Assert.Single(Directory.GetFiles(_directory, "settings.json.damaged-*"));
    }

    [Fact]
    public void Settings_that_cant_be_read_for_now_arent_saved_over()
    {
        string path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{\"RetentionDays\":2}");

        // Something else holding the file open isn't damage, and defaults mustn't replace what's in it.
        var settings = new MonitorSettings(_directory);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(MonitorSettings.MaxRetentionDays, settings.RetentionDays);
            settings.RetentionDays = 5;
        }

        Assert.Equal("{\"RetentionDays\":2}", File.ReadAllText(path));
        Assert.NotNull(settings.TakeProblem());
    }

    [Fact]
    public void A_webhook_edited_into_the_file_is_still_checked()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{\"RetentionDays\":3,\"DefaultWebhook\":\"http://evil.example/x\",\"Webhooks\":{\"a\":\"https://evil.example/y\"}}");

        var settings = new MonitorSettings(_directory);

        Assert.Null(settings.WebhookFor("a"));
    }

    [Fact]
    public void Old_folders_nothing_claims_are_cleared_and_recent_ones_stay()
    {
        string oldFolder = ServerFolder("gone");
        string recentFolder = ServerFolder("recent");
        string notOurs = ServerFolder("other");

        File.WriteAllText(Path.Combine(oldFolder, "position.json"), "{}");
        File.WriteAllText(Path.Combine(recentFolder, "position.json"), "{}");
        File.WriteAllText(Path.Combine(notOurs, "keep.txt"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(oldFolder, "position.json"), DateTime.UtcNow.AddDays(-30d));
        File.SetLastWriteTimeUtc(Path.Combine(notOurs, "keep.txt"), DateTime.UtcNow.AddDays(-30d));

        NewStore().PruneQuietServers();

        Assert.False(Directory.Exists(oldFolder));
        Assert.True(Directory.Exists(recentFolder));
        Assert.True(Directory.Exists(notOurs));
    }

    [Fact]
    public void A_state_file_leaves_nothing_half_written()
    {
        string path = Path.Combine(_directory, "state.json");

        StateFiles.Write(path, "é");

        Assert.Equal("é", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, "*" + StateFiles.Unfinished));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_trigger_that_isnt_a_number_can_still_be_saved(double value)
    {
        var settings = new MonitorSettings(_directory);

        settings.Triggers = new TriggerSettings { SimBelow = value, FrameP95AboveMs = value, SpikeMs = value, PlayerTestLowestSim = value };

        Assert.True(double.IsFinite(settings.Triggers.SimBelow));
        Assert.True(double.IsFinite(settings.Triggers.FrameP95AboveMs));
        Assert.True(double.IsFinite(settings.Triggers.SpikeMs));
        Assert.True(double.IsFinite(settings.Triggers.PlayerTestLowestSim));
        Assert.True(double.IsFinite(new MonitorSettings(_directory).Triggers.SimBelow));
    }

    [Fact]
    public void An_id_from_a_request_has_to_be_a_real_server()
    {
        _servers.Servers.Add(new MonitorServer("node-1", "One", Server));

        HistoryStore store = NewStore();

        Assert.True(BoardRequest.Known(store, _servers, Server));
        Assert.True(BoardRequest.Known(store, _servers, string.Empty));
        Assert.True(BoardRequest.Known(store, _servers, null));
        Assert.False(BoardRequest.Known(store, _servers, "made-up"));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    public void A_time_from_a_request_cant_throw(long seconds)
    {
        _ = BoardRequest.Moment(seconds);
    }

    [Fact]
    public void Long_reads_are_turned_down_when_too_many_are_going()
    {
        DateTime to = DateTime.UtcNow;
        DateTime from = to.AddDays(-3d);
        string? innermost = null;

        string? outer = BoardRequest.Limited(from, to, () => BoardRequest.Limited(from, to, () =>
        {
            innermost = BoardRequest.Limited(from, to, () => "third");
            return "second";
        }) ?? "second");

        Assert.Equal("second", outer);
        Assert.Null(innermost);
        Assert.Equal("next", BoardRequest.Limited(from, to, () => "next"));
    }

    [Fact]
    public void A_short_read_is_never_turned_down()
    {
        DateTime to = DateTime.UtcNow;

        Assert.Equal("short", BoardRequest.Limited(to.AddMinutes(-5d), to, () => "short"));
    }

    [Fact]
    public void Old_runs_go_without_anyone_opening_the_tab()
    {
        var settings = new MonitorSettings(_directory);
        var runs = new MonitorRuns(_servers, _directory, settings, new StoppedLifetime(), NullLogger<MonitorRuns>.Instance);
        string folder = Path.Combine(ServerFolder(Server), "runs");
        Directory.CreateDirectory(folder);

        string oldRun = Path.Combine(folder, DateTime.UtcNow.AddDays(-30d).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".json");
        string newRun = Path.Combine(folder, DateTime.UtcNow.AddHours(-1d).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".json");
        File.WriteAllText(oldRun, "{}");
        File.WriteAllText(newRun, "{}");

        runs.Prune(Server);

        Assert.False(File.Exists(oldRun));
        Assert.True(File.Exists(newRun));
    }

    [Fact]
    public void Half_written_run_files_are_cleared_too()
    {
        var settings = new MonitorSettings(_directory);
        var runs = new MonitorRuns(_servers, _directory, settings, new StoppedLifetime(), NullLogger<MonitorRuns>.Instance);
        string folder = Path.Combine(ServerFolder(Server), "runs");
        Directory.CreateDirectory(folder);

        string stale = Path.Combine(folder, "20260101-000000.json" + StateFiles.Unfinished);
        File.WriteAllText(stale, "{");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30d));

        runs.Prune(Server);

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void A_name_can_only_be_text_in_a_discord_alert()
    {
        var incident = new StoredIncident { Opened = DateTime.UtcNow, LowestSim = 0.5d, WorstFrameMs = 90d };

        incident.Evidence.Add(new IncidentEvidence
        {
            Report = new IncidentResultResponse
            {
                SlowCalls = [new SlowCallRow { Name = "[Open report](https://evil.example)", Ms = 5d }],
            },
        });

        WebhookEmbed embed = MonitorWebhooks.Compose(WebhookEvent.EvidenceReady, "Main", incident);

        Assert.DoesNotContain("](", string.Concat(embed.Fields.Select(field => field.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_chart_with_one_moment_is_nothing_to_draw()
    {
        var chart = new ChartData("c", " ms", false, null, 0d, [5d, 5d], [new ChartLine("A", "monitor-c-0", [1d, 2d])]);

        Assert.Null(ChartImage.Render(chart, "Title", 640, 320, []));
    }

    [Fact]
    public void Times_out_of_order_dont_break_a_chart_image()
    {
        var chart = new ChartData("c", " ms", false, null, 0d, [5d, 3d, 9d], [new ChartLine("A", "monitor-c-0", [1d, 2d, 3d])]);

        Assert.NotNull(ChartImage.Render(chart, "Title", 640, 320, []));
    }
}
