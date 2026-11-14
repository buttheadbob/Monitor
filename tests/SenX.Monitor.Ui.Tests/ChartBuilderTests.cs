using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Components.Charts;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class ChartBuilderTests : IDisposable
{
    private const string Server = "server-1";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly HistoryStore _store;
    private readonly ChartBuilder _charts;
    private readonly DateTime _started = DateTime.UtcNow.AddSeconds(-30);
    private readonly FakeServers _servers = new();

    public ChartBuilderTests()
    {
        _store = new HistoryStore(_directory, new MonitorSettings(_directory), NullLogger<HistoryStore>.Instance);
        _charts = new ChartBuilder(_store, _servers);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeServers : IMonitorServers
    {
        public List<MonitorServer> Servers { get; } = [];

        public IReadOnlyList<MonitorServer> Connected() => Servers;

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
            throw new NotSupportedException();

        public Task<RunResultResponse> RunResultAsync(string serverId, RunResultRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private HistoryBatch Batch(int seconds)
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, RollupFirst = 0L, RollupHead = seconds };

        for (int second = 0; second < seconds; second++)
        {
            var scopes = new uint[43];
            scopes[HistoryScopes.Frame] = 6000000U;
            scopes[HistoryScopes.Physics] = 3000000U;

            batch.Rollups.Add(new HistoryRollup
            {
                UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks,
                Frames = 60U,
                SimulatedFrames = 60U,
                WindowUnits = 10_000_000U,
                ScopeUnits = scopes,
            });
        }

        return batch;
    }

    private static string Name(HistoryDimension kind, long key) => kind == HistoryDimension.Factions ? "F" + key : "Grid " + key;

    private HistoryBatch Players(int second, params (long Identity, long Faction)[] players)
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, HasDetail = true };
        batch.Census.Add(new HistoryCensus { UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks });

        foreach ((long identity, long faction) in players)
            batch.Players.Add(new HistoryPlayer { IdentityId = identity, FactionId = faction, PingMs = (uint)(identity * 10L) });

        return batch;
    }

    private ChartData Chart(string view, string id, HistoryDimension list = HistoryDimension.Grids) =>
        _charts.Build(view, Server, 5, list).Charts.Single(chart => chart.Id == id);

    [Fact]
    public void Every_line_has_a_value_for_every_time()
    {
        // uPlot draws from one shared time column, so a short line would shift all that follow.
        _store.Add(Server, Batch(10), Name);

        ChartData cause = Chart("overview", "cause");

        Assert.Equal(10, cause.Times.Length);
        Assert.All(cause.Lines, line => Assert.Equal(cause.Times.Length, line.Values.Length));
    }

    [Fact]
    public void Times_are_in_order()
    {
        _store.Add(Server, Batch(10), Name);

        double[] times = Chart("overview", "sim").Times;

        Assert.Equal(times.Order(), times);
    }

    [Fact]
    public void A_pause_breaks_the_line()
    {
        HistoryBatch batch = Batch(5);
        batch.Rollups[2].AfterGap = true;
        _store.Add(Server, batch, Name);

        ChartData sim = Chart("overview", "sim");

        Assert.Equal(6, sim.Times.Length);
        Assert.Null(sim.Lines[0].Values[2]);
        Assert.Equal(sim.Times.Order(), sim.Times);
    }

    [Fact]
    public void Frame_parts_are_counted_once_each()
    {
        _store.Add(Server, Batch(1), Name);

        double[] parts = FrameParts.Split(_store.Latest(Server)!);

        // Physics is 5 ms of a 10 ms frame, so 5 ms is other work and the rest of 16.67 ms is idle.
        Assert.Equal(5d, parts[0], 3);
        Assert.Equal(5d, parts[Array.FindIndex(FrameParts.All, part => part.Name == "Other")], 3);
        Assert.Equal(6.667d, parts[Array.FindIndex(FrameParts.All, part => part.Name == "Idle")], 2);
        Assert.Equal(1000d / 60d, parts.Sum(), 2);
    }

    [Fact]
    public void Lines_are_biggest_first()
    {
        _store.Add(Server, Batch(5), Name);

        List<double> latest = Chart("overview", "cause").Lines.Where(line => !line.Overlay).Select(line => line.Values[^1] ?? 0d).ToList();

        Assert.Equal(latest.OrderByDescending(value => value), latest);
    }

    [Fact]
    public void Overlays_come_after_the_bands()
    {
        // The browser numbers series on this, so a band after an overlay would be stacked wrongly.
        _store.Add(Server, Batch(5), Name);

        List<ChartLine> lines = Chart("overview", "cause").Lines.ToList();
        int firstOverlay = lines.FindIndex(line => line.Overlay);

        Assert.True(firstOverlay > 0);
        Assert.All(lines.Skip(firstOverlay), line => Assert.True(line.Overlay));
    }

    [Fact]
    public void The_cause_chart_shows_the_budget_and_sim_speed()
    {
        _store.Add(Server, Batch(5), Name);

        List<ChartLine> lines = Chart("overview", "cause").Lines.ToList();

        Assert.Equal(1000d / 60d, lines.Single(line => line.Name == "Frame budget").Values[0]!.Value, 6);
        Assert.Equal(1d, lines.Single(line => line.Name == "Sim speed").Values[0]!.Value, 3);
        Assert.True(lines.Single(line => line.Name == "Sim speed").SecondScale);
    }

    [Fact]
    public void Idle_isnt_stacked_because_it_isnt_using_the_frame()
    {
        _store.Add(Server, Batch(5), Name);

        Assert.DoesNotContain(Chart("overview", "cause").Lines, line => line.Name == FrameParts.IdleName);
    }

    [Fact]
    public void Available_time_is_what_the_frame_leaves_of_its_budget()
    {
        _store.Add(Server, Batch(1), Name);
        HistoryPoint point = _store.Latest(Server)!;

        // A 10 ms frame leaves 6.67 ms of the 16.67 ms budget.
        Assert.Equal(6.667d, FrameParts.Available(point), 2);
    }

    [Fact]
    public void Available_time_uses_the_worst_frames_for_the_second_line()
    {
        HistoryBatch batch = Batch(1);
        batch.Rollups[0].FrameP99 = 140000U;
        _store.Add(Server, batch, Name);

        Assert.Equal(2.667d, FrameParts.AvailableAtWorst(_store.Latest(Server)!), 2);
    }

    [Fact]
    public void An_overloaded_server_has_no_time_available_and_never_less()
    {
        HistoryBatch batch = Batch(1);
        batch.Rollups[0].FrameP99 = 900000U;
        batch.Rollups[0].ScopeUnits[HistoryScopes.Frame] = 300000U * 60U;
        _store.Add(Server, batch, Name);

        HistoryPoint point = _store.Latest(Server)!;

        Assert.Equal(0d, FrameParts.Available(point));
        Assert.Equal(0d, FrameParts.AvailableAtWorst(point));
    }

    [Fact]
    public void A_list_lines_up_rows_that_start_at_different_times()
    {
        HistoryBatch batch = Batch(1);
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Grids, Key = 1L, MainUnits = 10000U, UnitsSinceStart = 0L });
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Grids, Key = 2L, MainUnits = 20000U, UnitsSinceStart = 0L });
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Grids, Key = 2L, MainUnits = 30000U, UnitsSinceStart = TimeSpan.FromSeconds(10).Ticks });
        _store.Add(Server, batch, Name);

        ChartData costs = Chart("costs", "costs");

        Assert.Equal(2, costs.Times.Length);
        ChartLine first = costs.Lines.Single(line => line.Name == "Grid 2");
        Assert.Equal(3d, first.Values[1]!.Value, 3);
    }

    private static string ModName(HistoryDimension kind, long key) => kind switch
    {
        HistoryDimension.Mods => key == 1L ? "Mod: One" : "Mod: One More",
        HistoryDimension.ModParts => ModNames.Part(key < 100L ? "Mod: One" : "Mod: One More", "Part" + key),
        _ => "Grid " + key,
    };

    private void Mods()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };

        void Add(HistoryDimension list, long key, int second, uint main) => batch.Groups.Add(new HistoryGroup
        {
            Dimension = list,
            Key = key,
            MainUnits = main,
            UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks,
        });

        foreach (int second in new[] { 0, 30 })
        {
            Add(HistoryDimension.Mods, 1L, second, 30000U);
            Add(HistoryDimension.Mods, 2L, second, 10000U);
            Add(HistoryDimension.ModParts, 10L, second, 20000U);
            Add(HistoryDimension.ModParts, 11L, second, 10000U);
            Add(HistoryDimension.ModParts, 100L, second, 10000U);
        }

        _store.Add(Server, batch, ModName);
    }

    private ChartData Parts(string mod) =>
        _charts.Build("modparts", Server, 5, HistoryDimension.Grids, mod).Charts.Single(chart => chart.Id == "modParts");

    [Fact]
    public void Each_mod_has_a_line_over_time()
    {
        Mods();

        ChartData mods = Chart("mods", "mods");

        Assert.Equal(2, mods.Times.Length);
        Assert.Equal(["Mod: One", "Mod: One More"], mods.Lines.Select(line => line.Name));
        Assert.Equal(3d, mods.Lines[0].Values[1]!.Value, 3);
    }

    [Fact]
    public void A_mods_chart_shows_only_its_parts_named_without_the_mod()
    {
        Mods();

        ChartData parts = Parts("Mod: One");

        Assert.Equal(["Part10", "Part11"], parts.Lines.Select(line => line.Name));
    }

    [Fact]
    public void A_mod_with_nothing_recorded_has_an_empty_chart()
    {
        Mods();

        Assert.Empty(Parts("Mod: Nobody").Lines);
    }

    [Fact]
    public void The_parts_chart_without_a_mod_is_empty_and_not_a_fault()
    {
        Mods();

        Assert.Empty(_charts.Build("modparts", Server, 5, HistoryDimension.Grids).Charts.Single().Lines);
    }

    [Theory]
    [InlineData("Mod: One", "Part", "Mod: One > Part")]
    [InlineData("Plugin: Some > Thing", "Part", "Plugin: Some > Thing > Part")]
    public void A_parts_name_starts_with_its_mod(string mod, string part, string expected)
    {
        Assert.Equal(expected, ModNames.Part(mod, part));
        Assert.StartsWith(ModNames.PartsOf(mod), expected);
    }

    [Theory]
    [InlineData("Mod: One > Part", "Part")]
    [InlineData("Part", "Part")]
    [InlineData("Mod: Some > Thing > Part", "Part")]
    public void A_parts_own_name_leaves_its_mod_off(string name, string expected)
    {
        Assert.Equal(expected, ModNames.PartName(name));
    }

    [Fact]
    public void The_timeline_has_a_row_per_player_most_online_first()
    {
        // Each store call adds a minute's pass, because players are kept once a minute.
        _store.Add(Server, Players(-180, (1L, 5L), (2L, 5L)), Name);
        _store.Add(Server, Players(-120, (2L, 5L)), Name);

        ChartData online = Chart("world", "online");

        Assert.Equal(["Grid 2", "Grid 1"], online.Rows);
        Assert.Equal(0d, online.Lines[0].Values[1]);
        Assert.Equal(1d, online.Lines[1].Values[0]);
        Assert.Null(online.Lines[1].Values[1]);
    }

    [Fact]
    public void A_player_who_leaves_leaves_a_gap_on_the_timeline()
    {
        _store.Add(Server, Players(-240, (1L, 5L)), Name);
        _store.Add(Server, Players(-180), Name);
        _store.Add(Server, Players(-120, (1L, 5L)), Name);

        ChartLine line = Chart("world", "online").Lines.Single();

        Assert.Equal(3, line.Values.Length);
        Assert.NotNull(line.Values[0]);
        Assert.Null(line.Values[1]);
        Assert.NotNull(line.Values[2]);
    }

    [Fact]
    public void Online_members_add_up_by_faction()
    {
        _store.Add(Server, Players(-180, (1L, 5L), (2L, 5L), (3L, 6L)), Name);

        ChartData factions = Chart("world", "factionsOnline");

        Assert.True(factions.Stacked);
        Assert.Equal(2d, factions.Lines.Single(line => line.Name == "F5").Values[0]);
        Assert.Equal(1d, factions.Lines.Single(line => line.Name == "F6").Values[0]);
    }

    [Fact]
    public void A_faction_with_nobody_on_reads_zero_and_not_a_gap()
    {
        // A band with a hole would drop every band above it.
        _store.Add(Server, Players(-240, (1L, 5L)), Name);
        _store.Add(Server, Players(-180, (2L, 6L)), Name);

        ChartLine line = Chart("world", "factionsOnline").Lines.Single(entry => entry.Name == "F5");

        Assert.Equal(0d, line.Values[1]);
    }

    [Fact]
    public void Player_ping_keeps_only_the_highest()
    {
        (long, long)[] players = Enumerable.Range(1, 12).Select(id => ((long)id, 5L)).ToArray();
        _store.Add(Server, Players(-180, players), Name);

        ChartData ping = Chart("world", "playerPing");

        Assert.Equal(8, ping.Lines.Count);
        Assert.Equal("Grid 12", ping.Lines[0].Name);
    }

    [Fact]
    public void The_game_loop_chart_shows_update_and_idle()
    {
        HistoryBatch batch = Batch(1);
        batch.Rollups[0].ScopeUnits[HistoryScopes.Update] = 5400000U;
        _store.Add(Server, batch, Name);

        ChartData loop = Chart("overview", "gameLoop");

        Assert.Equal(9d, loop.Lines.Single(line => line.Name == "Update").Values[0]!.Value, 3);
        Assert.Equal(6.667d, loop.Lines.Single(line => line.Name == "Idle").Values[0]!.Value, 2);
    }

    [Fact]
    public void Garbage_collections_and_allocation_are_charted()
    {
        HistoryBatch batch = Batch(1);
        batch.Rollups[0].Gen2 = 3U;
        batch.Rollups[0].AllocatedKb = 2048U;
        _store.Add(Server, batch, Name);

        Assert.Equal(3d, Chart("overview", "gc").Lines.Single(line => line.Name == "Gen 2").Values[0]!.Value, 3);
        Assert.Equal(2d, Chart("overview", "allocated").Lines[0].Values[0]!.Value, 3);
    }

    [Fact]
    public void Memory_is_charted_from_the_census()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        batch.Census.Add(new HistoryCensus { UnitsSinceStart = TimeSpan.FromSeconds(1).Ticks, WorkingSetMb = 9000U, ManagedMb = 4000U });
        _store.Add(Server, batch, Name);

        ChartData memory = Chart("overview", "memory");

        Assert.Equal(9000d, memory.Lines.Single(line => line.Name == "Working set").Values[0]!.Value, 3);
        Assert.Equal(4000d, memory.Lines.Single(line => line.Name == "Managed").Values[0]!.Value, 3);
    }

    private ChartData Net(string id) => _charts.Build("network", Server, 5, HistoryDimension.Grids).Charts.Single(chart => chart.Id == id);

    private HistoryBatch Traffic(int seconds)
    {
        HistoryBatch batch = Batch(seconds);

        foreach (HistoryRollup row in batch.Rollups)
        {
            row.SentKb = 100U;
            row.ReceivedKb = 20U;
            row.StateSyncKb = 50U;
            row.EventsKb = 25U;
            row.ReplicationKb = 15U;
            row.RpcsIn = 7U;
        }

        return batch;
    }

    [Fact]
    public void Traffic_is_charted_a_second_at_a_time()
    {
        _store.Add(Server, Traffic(5), Name);

        ChartData traffic = Net("traffic");

        Assert.Equal(100d, traffic.Lines.Single(line => line.Name == "Sent").Values[0]!.Value, 3);
        Assert.Equal(20d, traffic.Lines.Single(line => line.Name == "Received").Values[0]!.Value, 3);
        Assert.Equal(7d, Net("rpcRate").Lines[0].Values[0]!.Value, 3);
    }

    [Fact]
    public void What_was_sent_adds_up_to_all_of_it()
    {
        _store.Add(Server, Traffic(5), Name);

        ChartData kinds = Net("trafficKinds");

        Assert.True(kinds.Stacked);
        Assert.Equal(10d, kinds.Lines.Single(line => line.Name == "Other").Values[0]!.Value, 3);
        Assert.Equal(100d, kinds.Lines.Sum(line => line.Values[0]!.Value), 3);
    }

    [Fact]
    public void Other_never_reads_below_nothing()
    {
        // These come from running totals rounded down, so the kinds can add up past the total by a KB.
        HistoryBatch batch = Traffic(1);
        batch.Rollups[0].SentKb = 80U;
        _store.Add(Server, batch, Name);

        Assert.Equal(0d, Net("trafficKinds").Lines.Single(line => line.Name == "Other").Values[0]!.Value, 3);
    }

    private HistoryBatch PlayersWithTraffic(int second, params (long Identity, uint SentBytes, uint Queued)[] players)
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, HasDetail = true };
        batch.Census.Add(new HistoryCensus { UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks });

        foreach ((long identity, uint sent, uint queued) in players)
            batch.Players.Add(new HistoryPlayer { IdentityId = identity, FactionId = 5L, SentBytesPerSecond = sent, QueuedBytes = queued });

        return batch;
    }

    [Fact]
    public void Each_player_has_a_line_in_kilobytes()
    {
        _store.Add(Server, PlayersWithTraffic(-180, (1L, 2048U, 512U)), Name);

        Assert.Equal(2d, Net("playerTraffic").Lines.Single().Values[0]!.Value, 3);
        Assert.Equal(0.5d, Net("playerQueue").Lines.Single().Values[0]!.Value, 3);
    }

    [Fact]
    public void Only_the_highest_players_get_a_line()
    {
        (long, uint, uint)[] players = Enumerable.Range(1, 12).Select(id => ((long)id, (uint)id * 1024U, 0U)).ToArray();
        _store.Add(Server, PlayersWithTraffic(-180, players), Name);

        ChartData traffic = Net("playerTraffic");

        Assert.Equal(8, traffic.Lines.Count);
        Assert.Equal("Grid 12", traffic.Lines[0].Name);
    }

    [Fact]
    public void Rpcs_have_a_line_each_over_time()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Rpcs, Key = 1L, MainUnits = 30000U, UnitsSinceStart = 0L });
        _store.Add(Server, batch, (_, _) => "MyCubeGrid.OnSomething");

        Assert.Equal("MyCubeGrid.OnSomething", Assert.Single(Net("rpcs").Lines).Name);
    }

    [Fact]
    public void Rpcs_sent_are_charted_in_kilobytes()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.RpcsSent, Key = 1L, BytesPerSecond = 2048U, UnitsSinceStart = 0L });
        _store.Add(Server, batch, (_, _) => "MyCubeGrid.OnSomething");

        ChartData sent = Net("rpcsSent");

        Assert.Equal(" KB/s", sent.Unit);
        Assert.Equal(2d, Assert.Single(sent.Lines).Values[0]!.Value, 3);
    }

    [Fact]
    public void Rpcs_received_are_still_charted_in_time()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        batch.Groups.Add(new HistoryGroup { Dimension = HistoryDimension.Rpcs, Key = 1L, MainUnits = 30000U, UnitsSinceStart = 0L });
        _store.Add(Server, batch, (_, _) => "MyCubeGrid.OnSomething");

        ChartData rpcs = Net("rpcs");

        Assert.Equal(" ms/f", rpcs.Unit);
        Assert.Equal(3d, Assert.Single(rpcs.Lines).Values[0]!.Value, 3);
    }

    [Fact]
    public void Concealed_blocks_are_taken_out_of_the_active_line()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        batch.Census.Add(new HistoryCensus { UnitsSinceStart = TimeSpan.FromSeconds(1).Ticks, Blocks = 1000U, ConcealedBlocks = 300U });
        _store.Add(Server, batch, Name);

        ChartData blocks = Chart("world", "blocksConceal");

        Assert.Equal(700d, blocks.Lines.Single(line => line.Name == "Active").Values[0]!.Value, 3);
        Assert.Equal(300d, blocks.Lines.Single(line => line.Name == "Concealed").Values[0]!.Value, 3);
    }

    [Fact]
    public void Blocks_by_faction_follow_the_biggest_over_time()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, HasDetail = true };
        batch.Census.Add(new HistoryCensus { UnitsSinceStart = TimeSpan.FromSeconds(1).Ticks });
        batch.Factions.Add(new HistoryFaction { FactionId = 1L, Blocks = 500U });
        batch.Factions.Add(new HistoryFaction { FactionId = 2L, Blocks = 900U });
        _store.Add(Server, batch, Name);

        ChartData blocks = Chart("world", "blocksByFaction");

        Assert.Equal("F2", blocks.Lines[0].Name);
        Assert.Equal(900d, blocks.Lines[0].Values[0]!.Value, 3);
    }

    [Fact]
    public void Run_lines_leave_long_ranges_like_saves_do()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, MarkerHead = 1L };
        batch.Markers.Add(new HistoryMarker { UnitsSinceStart = TimeSpan.FromSeconds(5).Ticks, Kind = HistoryMarkerKind.RunStarted, Value = 10U });
        _store.Add(Server, batch, Name);

        Assert.Single(_charts.Build("overview", Server, 60, HistoryDimension.Grids).Markers);
        Assert.Empty(_charts.Build("overview", Server, 24 * 60, HistoryDimension.Grids).Markers);
    }

    [Fact]
    public void The_tree_shape_matches_the_workers()
    {
        Assert.Equal(
            SenX.Monitor.Core.ScopeTable.All.Select(scope => (scope.Id, scope.Parent, scope.Name)),
            TreeShape.All.Select(node => (node.Id, node.Parent, node.Name)));
    }

    [Fact]
    public void An_unknown_view_is_empty_and_not_a_fault()
    {
        Assert.Empty(_charts.Build("nonsense", Server, 5, HistoryDimension.Grids).Charts);
    }

    private void Saved()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks, MarkerHead = 1L };
        batch.Markers.Add(new HistoryMarker { UnitsSinceStart = TimeSpan.FromSeconds(5).Ticks, Kind = HistoryMarkerKind.Saved });
        _store.Add(Server, batch, Name);
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("costs")]
    [InlineData("mods")]
    [InlineData("modparts")]
    [InlineData("network")]
    [InlineData("world")]
    public void A_servers_charts_get_its_markers(string view)
    {
        _store.ServerStarted(Server, _started.Ticks, [], []);
        Saved();

        ChartsResponse response = _charts.Build(view, Server, 60, HistoryDimension.Grids);

        Assert.Equal(2, response.Markers.Count);
        Assert.Contains(response.Markers, marker => marker.Text == "Server started" && marker.Color == ChartColors.Session);
        Assert.Contains(response.Markers, marker => marker.Text == "World saved" && marker.Color == ChartColors.Other);
    }

    [Fact]
    public void A_marker_time_is_on_the_charts_time_scale()
    {
        _store.ServerStarted(Server, _started.Ticks, [], []);

        double expected = new DateTimeOffset(_started).ToUnixTimeMilliseconds() / 1000d;

        Assert.Equal(expected, Assert.Single(_charts.Build("overview", Server, 60, HistoryDimension.Grids).Markers).Time, 3);
    }

    [Fact]
    public void Saves_leave_long_ranges_and_restarts_stay()
    {
        _store.ServerStarted(Server, _started.Ticks, [], []);
        Saved();

        ChartsResponse response = _charts.Build("overview", Server, 24 * 60, HistoryDimension.Grids);

        Assert.Equal("Server started", Assert.Single(response.Markers).Text);
    }

    [Fact]
    public void Comparing_servers_has_no_markers()
    {
        _servers.Servers.Add(new MonitorServer(Server, "One", Server));
        _store.ServerStarted(Server, _started.Ticks, [], []);

        Assert.Empty(_charts.Build("servers", string.Empty, 60, HistoryDimension.Grids).Markers);
    }

    [Fact]
    public void Comparing_servers_gives_each_one_a_line_on_shared_times()
    {
        _servers.Servers.Add(new MonitorServer("b", "Beta", "history-b"));
        _servers.Servers.Add(new MonitorServer("a", "Alpha", "history-a"));
        _store.Add("history-a", Batch(30), Name);
        _store.Add("history-b", Batch(30), Name);

        ChartData sim = _charts.Build("servers", string.Empty, 5, HistoryDimension.Grids).Charts.Single(chart => chart.Id == "serversSim");

        Assert.Equal(["Alpha", "Beta"], sim.Lines.Select(line => line.Name));
        Assert.NotEqual(sim.Lines[0].Color, sim.Lines[1].Color);
        Assert.All(sim.Lines, line => Assert.Equal(sim.Times.Length, line.Values.Length));
        Assert.All(sim.Lines, line => Assert.All(line.Values, value => Assert.NotNull(value)));
    }

    [Fact]
    public void A_server_with_no_data_leaves_gaps_and_not_zeroes()
    {
        _servers.Servers.Add(new MonitorServer("a", "Alpha", "history-a"));
        _servers.Servers.Add(new MonitorServer("b", "Beta", "history-b"));
        _store.Add("history-a", Batch(30), Name);

        ChartData sim = _charts.Build("servers", string.Empty, 5, HistoryDimension.Grids).Charts.Single(chart => chart.Id == "serversSim");

        Assert.All(sim.Lines.Single(line => line.Name == "Beta").Values, value => Assert.Null(value));
    }

    [Fact]
    public void A_step_keeps_the_worst_sim_speed_in_it()
    {
        _servers.Servers.Add(new MonitorServer("a", "Alpha", "history-a"));

        HistoryBatch batch = Batch(30);
        for (int second = 0; second < batch.Rollups.Count; second++)
            batch.Rollups[second].SimulatedFrames = second % 2 == 0 ? 60U : 30U;

        _store.Add("history-a", batch, Name);

        ChartData sim = _charts.Build("servers", string.Empty, 5, HistoryDimension.Grids).Charts.Single(chart => chart.Id == "serversSim");

        // Only the first and last steps can hold a single row, and that row might be a good one.
        List<double> steps = sim.Lines[0].Values.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        Assert.True(steps.Count >= 5);
        Assert.All(steps.Skip(1).SkipLast(1), value => Assert.Equal(0.5d, value, 2));
    }

    [Fact]
    public void All_servers_gives_every_chart_with_a_line_per_server()
    {
        _servers.Servers.Add(new MonitorServer("b", "Beta", "history-b"));
        _servers.Servers.Add(new MonitorServer("a", "Alpha", "history-a"));
        _store.Add("history-a", Batch(30), Name);
        _store.Add("history-b", Batch(30), Name);

        ChartsResponse response = _charts.Build("all", string.Empty, 5, HistoryDimension.Grids);

        Assert.Equal(ChartBuilder.AllServersCharts, response.Charts.Select(chart => chart.Id));
        Assert.Empty(response.Markers);
        Assert.All(response.Charts, chart => Assert.Equal(["Alpha", "Beta"], chart.Lines.Select(line => line.Name)));

        ChartData sim = response.Charts.Single(chart => chart.Id == "allSim");
        Assert.All(sim.Lines, line => Assert.All(line.Values, value => Assert.NotNull(value)));
    }

    [Fact]
    public void All_servers_leaves_a_silent_server_empty_and_not_zero()
    {
        _servers.Servers.Add(new MonitorServer("a", "Alpha", "history-a"));
        _servers.Servers.Add(new MonitorServer("b", "Beta", "history-b"));
        _store.Add("history-a", Batch(30), Name);

        ChartData sim = _charts.Build("all", string.Empty, 5, HistoryDimension.Grids).Charts.Single(chart => chart.Id == "allSim");

        Assert.All(sim.Lines.Single(line => line.Name == "Beta").Values, value => Assert.Null(value));
    }

    [Fact]
    public void Twenty_servers_never_share_a_colour_and_line_style()
    {
        for (int i = 0; i < 20; i++)
            _servers.Servers.Add(new MonitorServer("s" + i, "Server " + i.ToString("00"), "history-" + i));

        ChartData sim = _charts.Build("all", string.Empty, 5, HistoryDimension.Grids).Charts.Single(chart => chart.Id == "allSim");

        Assert.Equal(20, sim.Lines.Count);
        Assert.Equal(20, sim.Lines.Select(line => (line.Color, line.Dashed, line.Dotted)).Distinct().Count());
        Assert.False(sim.Lines[0].Dashed || sim.Lines[0].Dotted);
        Assert.True(sim.Lines[8].Dashed);
        Assert.True(sim.Lines[16].Dotted);
    }
}
