using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Components.Charts;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class BoardBuilderTests : IDisposable
{
    private const string Server = "server-1";
    private const string Other = "server-2";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly HistoryStore _store;
    private readonly BoardBuilder _boards;
    private readonly ScriptedServers _servers = new();
    private readonly DateTime _started = DateTime.UtcNow.AddMinutes(-10);

    public BoardBuilderTests()
    {
        var settings = new MonitorSettings(_directory);

        _store = new HistoryStore(_directory, settings, NullLogger<HistoryStore>.Instance);
        _boards = new BoardBuilder(_store, _servers, settings);
        _servers.Servers.Add(new MonitorServer(Server, "Alpha", Server));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static string Name(HistoryDimension kind, long key) => kind switch
    {
        HistoryDimension.Mods => key == 1L ? "Big Mod" : "Small Mod",
        HistoryDimension.ModParts => ModNames.Part("Big Mod", "Part " + key),
        _ => "Grid " + key,
    };

    [Fact]
    public void Thread_jumps_dont_push_events_off_the_board()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        batch.Markers.Add(new HistoryMarker { UnitsSinceStart = TimeSpan.FromMinutes(1).Ticks, Kind = HistoryMarkerKind.LagTripped, Value = 1U });

        for (int i = 0; i < 40; i++)
        {
            batch.Groups.Add(new HistoryGroup
            {
                UnitsSinceStart = TimeSpan.FromMinutes(2).Ticks,
                Dimension = HistoryDimension.ThreadJumps,
                Key = 100L + i,
                Blocks = (uint)HistoryDimension.Grids,
                Grids = (1U << 8) | 2U,
            });
        }

        _store.Add(Server, batch, Name);

        BoardPanel events = Panel("events");
        Assert.Single(events.Table!.Rows);
        Assert.Equal("Lag noticed", events.Table.Rows[0][1].Text);
    }

    private BoardResponse Board(string server = Server) => _boards.Build(server, DateTime.UtcNow.AddMinutes(-20), DateTime.UtcNow);

    private BoardPanel Panel(string id, string server = Server) =>
        Board(server).Sections.SelectMany(section => section.Panels).Single(panel => panel.Id == id);

    // Whole totals every two minutes: a grid is in all of them and another only in the first.
    private void AddGroups(string server, int totals = 3)
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };

        for (int total = 0; total < totals; total++)
        {
            long units = TimeSpan.FromMinutes(total * 2).Ticks;

            batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Grids, Key = 1L, MainUnits = 20000U, UnitsSinceStart = units });
            batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Mods, Key = 1L, MainUnits = 30000U, UnitsSinceStart = units });
            batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.ModParts, Key = 1L, MainUnits = 10000U, UnitsSinceStart = units });

            if (total == 0)
                batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Grids, Key = 2L, MainUnits = 90000U, UnitsSinceStart = units });
        }

        _store.Add(server, batch, Name);
    }

    private void AddRollups(string server, int seconds, float sim = 1f)
    {
        var batch = new HistoryBatch { StartedUtcTicks = DateTime.UtcNow.AddSeconds(-seconds).Ticks, RollupFirst = 0L, RollupHead = seconds };

        for (int second = 0; second < seconds; second++)
        {
            var scopes = new uint[43];
            scopes[HistoryScopes.Frame] = 100000U * 60U;

            batch.Rollups.Add(new HistoryRollup
            {
                Frames = 60U,
                UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks,
                WindowUnits = 10000000U,
                SimulatedFrames = (uint)(sim * 60f),
                FrameP50 = 100000U,
                FrameP95 = 100000U,
                FrameP99 = 100000U,
                ScopeUnits = scopes,
            });
        }

        _store.Add(server, batch, Name);
    }

    [Fact]
    public void An_empty_server_still_gets_every_panel()
    {
        BoardResponse board = Board();

        Assert.Contains(board.Sections, section => section.Title == "Where it goes");
        Assert.True(board.Sections.Sum(section => section.Panels.Count) > 40);
        Assert.Null(board.Sections.SelectMany(section => section.Panels).First(panel => panel.Id == "gaugePing").Value);
    }

    [Fact]
    public void Panels_have_unique_ids_and_fit_the_grid()
    {
        AddRollups(Server, 30);
        AddGroups(Server);

        List<BoardPanel> panels = Board().Sections.SelectMany(section => section.Panels).ToList();

        Assert.Equal(panels.Count, panels.Select(panel => panel.Id).Distinct().Count());
        Assert.All(panels, panel => Assert.InRange(panel.Span, 1, 12));
    }

    [Fact]
    public void A_board_is_valid_json_for_the_page()
    {
        AddRollups(Server, 30);
        AddGroups(Server);

        string json = JsonSerializer.Serialize(Board(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.Equal("Alpha", parsed.RootElement.GetProperty("title").GetString());
        Assert.True(parsed.RootElement.GetProperty("sections").GetArrayLength() > 0);
    }

    [Fact]
    public void Tiles_read_the_end_of_the_range()
    {
        AddRollups(Server, 60, 0.5f);

        BoardPanel sim = Panel("statSim");

        Assert.Equal(0.5d, sim.Value!.Value, 2);
        Assert.NotNull(sim.Chart);
        Assert.True(sim.Chart!.Times.Length <= 120);
    }

    [Fact]
    public void A_gauge_turns_red_in_its_red_band()
    {
        AddRollups(Server, 60, 0.5f);

        BoardPanel gauge = Panel("gaugeSim");
        BoardBand band = gauge.Bands!.First(item => gauge.Value <= item.Limit);

        Assert.Equal(ChartColors.Bad, band.Color);
    }

    [Fact]
    public void A_heatmap_has_one_row_per_name_and_a_cell_for_each_step()
    {
        AddGroups(Server);

        BoardHeat heat = Panel("heatGrids").Heat!;

        Assert.Equal(["Grid 1", "Grid 2"], heat.Names.Order().ToList());
        Assert.Equal(heat.Names.Count * heat.Columns, heat.Values.Length);
        Assert.Equal(120d, heat.Step, 0);
        Assert.Equal(3, heat.Columns);
    }

    [Fact]
    public void A_heatmap_leaves_a_gap_where_nothing_was_recorded()
    {
        AddGroups(Server);

        BoardHeat heat = Panel("heatGrids").Heat!;
        int row = heat.Names.ToList().IndexOf("Grid 2");

        Assert.Equal(9d, heat.Values[row * heat.Columns]!.Value, 3);
        Assert.Null(heat.Values[(row * heat.Columns) + 1]);
        Assert.Null(heat.Values[(row * heat.Columns) + 2]);
        Assert.Equal(9d, heat.Maximum, 3);
    }

    [Fact]
    public void A_treemap_scales_a_row_by_how_much_of_the_range_it_was_seen()
    {
        AddGroups(Server);

        IReadOnlyList<BoardNode> tree = Panel("treeGrids").Tree!;

        // 9 ms in one total of three is 3 ms on average, so it doesn't outrank a steady 2 ms by more than it should.
        Assert.Equal(3d, tree.Single(node => node.Name == "Grid 2").Value, 3);
        Assert.Equal(2d, tree.Single(node => node.Name == "Grid 1").Value, 3);
    }

    [Fact]
    public void A_mod_box_holds_its_parts_and_the_rest_of_its_time()
    {
        AddGroups(Server);

        BoardNode mod = Panel("treeMods").Tree!.Single(node => node.Name == "Big Mod");

        Assert.Equal(3d, mod.Value, 3);
        Assert.Equal(mod.Value, mod.Children.Sum(child => child.Value), 3);
        Assert.Contains(mod.Children, child => child.Name == "Part 1" && Math.Abs(child.Value - 1d) < 0.001d);
        Assert.Contains(mod.Children, child => child.Name == "Everything else");
    }

    [Fact]
    public void Every_server_has_its_own_board_and_a_shared_one()
    {
        _servers.Servers.Add(new MonitorServer(Other, "Beta", Other));
        AddRollups(Server, 30, 0.5f);
        AddRollups(Other, 30);
        AddGroups(Server);

        BoardResponse all = Board(string.Empty);

        Assert.Equal("All servers", all.Title);
        Assert.Equal(2, all.Servers.Count);

        BoardTable table = Panel("allTable", string.Empty).Table!;
        Assert.Equal(["Alpha", "Beta"], table.Rows.Select(row => row[0].Text).ToList());
        Assert.Equal(Server, table.Rows[0][0].Server);

        BoardHeat lag = Panel("allLag", string.Empty).Heat!;
        Assert.Equal(["Alpha", "Beta"], lag.Names);
        Assert.InRange(lag.Maximum, 49d, 51d);

        Assert.Equal(["Alpha"], Panel("allGrids", string.Empty).Tree!.Select(node => node.Name).ToList());
    }

    [Fact]
    public void An_unknown_server_is_an_empty_board_not_a_failure()
    {
        BoardResponse board = Board("nobody");

        Assert.Equal("nobody", board.Title);
        Assert.Null(board.Sections.SelectMany(section => section.Panels).First(panel => panel.Id == "statSim").Value);
    }

    [Fact]
    public void The_page_never_holds_a_script_of_its_own()
    {
        string page = BoardPage.Html("/_quasar/plugins/senx.monitor/");

        Assert.Contains("src=\"/_quasar/plugins/senx.monitor/board.js\"", page);
        Assert.Contains("data-api=\"../" + BoardPage.DataRoute + "\"", page);
        Assert.DoesNotContain("<script>", page);
    }

    [Fact]
    public void A_relative_asset_path_is_reached_from_the_page()
    {
        Assert.Contains("href=\"../files/board.css\"", BoardPage.Html("files/"));
    }

    [Fact]
    public void An_asset_path_is_encoded()
    {
        Assert.Contains(WebUtility.HtmlEncode("\"x/") + "board.js", BoardPage.Html("\"x/"));
    }
}
