using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SenX.Monitor.Api;
using SenX.Monitor.Components;
using SenX.Monitor.Components.Charts;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class ThreadTrackingTests : IDisposable
{
    private const string Server = "server-1";
    private const long Grid = 4242L;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _started = DateTime.UtcNow.AddHours(-1);
    private readonly MonitorSettings _settings;
    private readonly HistoryStore _store;

    public ThreadTrackingTests()
    {
        _settings = new MonitorSettings(_directory);
        _store = new HistoryStore(_directory, _settings, NullLogger<HistoryStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // Names come from the list a thing is in, so a jump's name has to be asked for there.
    private static string Name(HistoryDimension kind, long key) => kind == HistoryDimension.Grids ? "Miner" : "wrong list " + kind;

    // A total with one grid in it, and that grid's jump from the game thread to workers.
    private HistoryBatch TotalWithJump()
    {
        long at = TimeSpan.FromMinutes(5).Ticks;
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, GroupFirst = 0L, GroupHead = 2L };

        batch.Groups.Add(new HistoryGroup { Sequence = 0L, UnitsSinceStart = at, Dimension = HistoryDimension.Grids, Key = Grid, MainUnits = 0U, WorkerUnits = 20000U });
        batch.Groups.Add(new HistoryGroup
        {
            Sequence = 1L,
            UnitsSinceStart = at,
            Dimension = HistoryDimension.ThreadJumps,
            Key = Grid,
            WorkerUnits = 20000U,
            Blocks = (uint)HistoryDimension.Grids,
            Grids = ((uint)MonitorThread.Main << 8) | (uint)MonitorThread.Worker,
        });

        return batch;
    }

    [Fact]
    public void A_jump_becomes_a_marker_named_from_its_own_list()
    {
        _store.Add(Server, TotalWithJump(), Name);

        StoredMarker marker = Assert.Single(_store.Markers(Server, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
        Assert.Equal(StoredMarkerKind.ThreadJump, marker.Kind);
        Assert.Equal(_started.AddMinutes(5), marker.Time);
        Assert.Equal("Grid Miner moved from the game thread to worker threads", marker.Text);
    }

    [Fact]
    public void A_jump_shows_its_id_until_a_normal_row_brings_the_name()
    {
        string? known = null;
        HistoryBatch first = TotalWithJump();
        first.Groups.RemoveAt(0);

        _store.Add(Server, first, Name, (_, _) => known);
        Assert.Equal("Grid #4242 moved from the game thread to worker threads", Assert.Single(_store.Markers(Server, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow)).Text);

        known = "Miner";
        var later = new HistoryBatch { StartedUtcTicks = _started.Ticks, GroupFirst = 2L, GroupHead = 3L };
        later.Groups.Add(new HistoryGroup { UnitsSinceStart = TimeSpan.FromMinutes(6).Ticks, Dimension = HistoryDimension.Grids, Key = Grid, WorkerUnits = 20000U });
        _store.Add(Server, later, Name, (_, _) => known);

        Assert.Equal("Grid Miner moved from the game thread to worker threads", Assert.Single(_store.Markers(Server, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow)).Text);
    }

    [Fact]
    public void A_total_says_how_many_jumps_it_didnt_list()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, GroupHead = 1L };
        batch.Groups.Add(new HistoryGroup { UnitsSinceStart = TimeSpan.FromMinutes(5).Ticks, Dimension = HistoryDimension.ThreadJumps, Key = -1L, MaxUnits = 12U });

        _store.Add(Server, batch, Name);

        StoredMarker marker = Assert.Single(_store.Markers(Server, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
        Assert.Equal(StoredMarkerKind.ThreadJump, marker.Kind);
        Assert.Equal("12 more thread changes not listed", marker.Text);
    }

    [Fact]
    public async Task The_thread_cell_renders_its_word_and_bar()
    {
        string html = await RenderAsync<ThreadCell>(new Dictionary<string, object?> { ["Main"] = 1d, ["Worker"] = 3d });

        Assert.Contains("monitor-thread-mixed", html);
        Assert.Contains(">Mixed<", html);
        Assert.Contains("width: 25%", html);

        string unknown = await RenderAsync<ThreadCell>(new Dictionary<string, object?> { ["Main"] = 1d, ["Worker"] = 0d, ["Known"] = false });
        Assert.DoesNotContain("monitor-thread-tag", unknown);
    }

    [Fact]
    public async Task A_run_report_has_the_thread_column_and_wait_figures()
    {
        var run = new RunResultResponse
        {
            Rows =
            [
                new RunRow { List = RunLists.Grids, Name = "Miner", MainMsPerFrame = 0d, WorkerMsPerFrame = 2d },
                new RunRow { List = RunLists.Clusters, Name = "Cluster 0", MainMsPerFrame = 3d },
            ],
            MainWaitMsPerFrame = 0.4d,
            ParallelWorkerMsPerFrame = 2.5d,
        };

        string html = await RenderAsync<RunReport>(new Dictionary<string, object?> { ["Result"] = run });

        Assert.Contains("<th>Thread</th>", html);
        Assert.Contains(">Worker<", html);
        Assert.Contains("Main waiting", html);
        Assert.Contains("0.40 ms/f", html);
        Assert.Contains("2.50 ms/f", html);
        Assert.Contains("can read slightly high", html);

        // Each list is a panel that opens and closes, in one of two stacks.
        Assert.Equal(2, html.Split("class=\"monitor-panel-stack\"").Length - 1);
        Assert.Contains("<details class=\"monitor-panel\" data-panel=\"runs.Grids\" open", html);
        Assert.Contains("<summary><h2>Grids</h2></summary>", html);
    }

    // Renders a component to HTML with no browser, so a broken binding or markup fails a test.
    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddSingleton<IJSRuntime, NoJs>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new MonitorAssets("_content/SenX.Monitor/"));

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);

        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/monitor");
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    [Fact]
    public void A_jump_isnt_a_cost()
    {
        _store.Add(Server, TotalWithJump(), Name);

        StoredGroup grid = Assert.Single(_store.LatestGroups(Server, HistoryDimension.Grids));
        Assert.Equal("Worker", grid.Thread);
        Assert.Empty(_store.LatestGroups(Server, HistoryDimension.ThreadJumps));
    }

    [Fact]
    public void A_jump_never_opens_an_incident()
    {
        var incidents = new MonitorIncidents(
            new ScriptedServers(),
            _directory,
            _settings,
            new MonitorBaselines(_store, _settings, NullLogger<MonitorBaselines>.Instance),
            NullLogger<MonitorIncidents>.Instance);

        incidents.Apply(Server, TotalWithJump());

        Assert.Empty(incidents.List(Server));
    }

    [Fact]
    public void A_jump_reaches_the_api_as_its_own_kind()
    {
        var marker = new StoredMarker(DateTime.UtcNow, StoredMarkerKind.ThreadJump, "Grid Miner moved");

        Assert.Equal(MonitorEventKind.ThreadJump, MonitorApi.ToEvent(Server, marker).Kind);
    }

    [Fact]
    public void A_jump_has_a_colour_no_other_marker_uses()
    {
        _store.ServerStarted(Server, _started.Ticks, [], []);
        _store.Add(Server, TotalWithJump(), Name);
        var charts = new ChartBuilder(_store, new ScriptedServers());

        ChartsResponse response = charts.Build("overview", Server, 120, HistoryDimension.Grids);

        Assert.Contains(response.Markers, marker => marker.Color == ChartColors.ThreadJump && marker.Text.StartsWith("Grid Miner", StringComparison.Ordinal));
        Assert.DoesNotContain(new[] { ChartColors.Bad, ChartColors.Session, ChartColors.Network, ChartColors.Other, ChartColors.Join, "monitor-c-5" }, color => color == ChartColors.ThreadJump);
    }

    [Fact]
    public void A_cluster_has_no_thread_and_other_rows_do()
    {
        var run = new RunResultResponse
        {
            State = RunState.Finished,
            Rows =
            [
                new RunRow { List = RunLists.Clusters, Name = "Cluster 0", MainMsPerFrame = 3d },
                new RunRow { List = RunLists.Grids, Name = "Miner", Key = Grid, MainMsPerFrame = 1d, WorkerMsPerFrame = 1d },
            ],
            MainWaitMsPerFrame = 0.4d,
            ParallelWorkerMsPerFrame = 2.5d,
        };

        MonitorReply reply = ReplyBuilder.FromRun(new MonitorRequest { Server = Server }, run, "1", DateTime.UtcNow);

        Assert.Equal(MonitorThread.Unknown, reply.Lists.Single(list => list.List == MonitorList.Clusters).Rows.Single().Thread);
        Assert.Equal(MonitorThread.Mixed, reply.Lists.Single(list => list.List == MonitorList.Grids).Rows.Single().Thread);
        Assert.Equal(0.4d, reply.Run!.MainWaitMs);
        Assert.Equal(2.5d, reply.Run.ParallelWorkerMs);
    }

    [Theory]
    [InlineData(1d, 0d, "M")]
    [InlineData(0d, 1d, "W")]
    [InlineData(1d, 1d, "M+W")]
    [InlineData(0d, 0d, "-")]
    public void Chat_tags_say_which_threads(double main, double worker, string tag)
    {
        Assert.Equal(tag, ThreadTags.Tag(main, worker));
    }

    [Fact]
    public void The_text_report_tags_each_row_and_says_how_long_the_game_thread_waited()
    {
        var run = new RunResultResponse
        {
            Rows = [new RunRow { List = RunLists.Grids, Name = "Miner", MainMsPerFrame = 0d, WorkerMsPerFrame = 2d }],
            MainWaitMsPerFrame = 0.4d,
        };

        string text = ReportText.ForRun(run);

        Assert.Contains("  W Miner: main 0.00, workers 2.00", text);
        Assert.Contains("Game thread waiting for workers 0.40 ms/f", text);
    }
}
