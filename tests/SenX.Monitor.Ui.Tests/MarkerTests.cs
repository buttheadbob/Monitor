using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class MarkerTests : IDisposable
{
    private const string Server = "server-1";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly long _firstStart = DateTime.UtcNow.AddHours(-2).Ticks;
    private readonly long _secondStart = DateTime.UtcNow.AddHours(-1).Ticks;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private HistoryStore NewStore() => new(_directory, new MonitorSettings(_directory), NullLogger<HistoryStore>.Instance);

    private static RuntimeItem Item(string id, string name) => new() { Id = id, Name = name };

    private static string Name(HistoryDimension kind, long key) => key.ToString();

    private static List<StoredMarker> All(HistoryStore store) =>
        store.Markers(Server, DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddMinutes(1));

    [Fact]
    public void Saves_from_the_worker_land_at_their_real_time()
    {
        HistoryStore store = NewStore();
        var started = new DateTime(_firstStart, DateTimeKind.Utc);

        var batch = new HistoryBatch { StartedUtcTicks = _firstStart, MarkerFirst = 4L, MarkerHead = 6L };
        batch.Markers.Add(new HistoryMarker { Sequence = 4L, UnitsSinceStart = TimeSpan.FromMinutes(5).Ticks, Kind = HistoryMarkerKind.Saved });
        batch.Markers.Add(new HistoryMarker { Sequence = 5L, UnitsSinceStart = TimeSpan.FromMinutes(10).Ticks, Kind = HistoryMarkerKind.SaveFailed });

        store.Add(Server, batch, Name);

        List<StoredMarker> markers = All(store);

        Assert.Equal(new StoredMarker(started.AddMinutes(10), StoredMarkerKind.Save, "World save failed"), markers[0]);
        Assert.Equal(new StoredMarker(started.AddMinutes(5), StoredMarkerKind.Save, "World saved"), markers[1]);
        Assert.Equal(6L, store.Position(Server).NextMarkers);
    }

    [Fact]
    public void A_save_says_how_long_it_took()
    {
        HistoryStore store = NewStore();

        var batch = new HistoryBatch { StartedUtcTicks = _firstStart, MarkerHead = 1L };
        batch.Markers.Add(new HistoryMarker { Kind = HistoryMarkerKind.Saved, Value = 1240U });
        store.Add(Server, batch, Name);

        Assert.Equal("World saved (1240 ms)", Assert.Single(All(store)).Text);
    }

    [Fact]
    public void Runs_and_detail_changes_are_marked_so_a_bump_has_a_reason()
    {
        HistoryStore store = NewStore();

        var batch = new HistoryBatch { StartedUtcTicks = _firstStart, MarkerHead = 2L };
        batch.Markers.Add(new HistoryMarker { Kind = HistoryMarkerKind.RunStarted, Value = 10U });
        batch.Markers.Add(new HistoryMarker { Kind = HistoryMarkerKind.DetailChanged, Value = 2U });
        store.Add(Server, batch, Name);

        Assert.Contains(All(store), marker => marker.Text == "Run for 10 s" && marker.Kind == StoredMarkerKind.Measuring);
        Assert.Contains(All(store), marker => marker.Text == "Detail set to everything");
    }

    [Fact]
    public void A_finished_chat_run_isnt_a_line_on_the_charts()
    {
        HistoryStore store = NewStore();

        var batch = new HistoryBatch { StartedUtcTicks = _firstStart, MarkerHead = 1L };
        batch.Markers.Add(new HistoryMarker { Kind = HistoryMarkerKind.RunFinished, Value = 3U });
        store.Add(Server, batch, Name);

        Assert.Empty(All(store));
    }

    [Fact]
    public void A_first_start_is_marked_and_nothing_counts_as_changed()
    {
        HistoryStore store = NewStore();

        store.ServerStarted(Server, _firstStart, [Item("a", "Plugin A")], [Item("1", "Mod One")]);

        StoredMarker marker = Assert.Single(All(store));
        Assert.Equal(StoredMarkerKind.Restart, marker.Kind);
        Assert.Equal(new DateTime(_firstStart, DateTimeKind.Utc), marker.Time);
    }

    [Fact]
    public void A_restart_says_what_changed()
    {
        HistoryStore store = NewStore();

        store.ServerStarted(Server, _firstStart, [Item("a", "Plugin A")], [Item("1", "Mod One")]);
        store.ServerStarted(Server, _secondStart, [Item("b", "Plugin B")], [Item("1", "Mod One"), Item("2", "Mod Two")]);

        List<string> changes = All(store).Where(marker => marker.Kind == StoredMarkerKind.Change).Select(marker => marker.Text).ToList();

        Assert.Equal(3, changes.Count);
        Assert.Contains("Plugin added: Plugin B", changes);
        Assert.Contains("Plugin removed: Plugin A", changes);
        Assert.Contains("Mod added: Mod Two", changes);
    }

    [Fact]
    public void A_restart_with_nothing_different_is_only_a_restart()
    {
        HistoryStore store = NewStore();

        store.ServerStarted(Server, _firstStart, [Item("a", "Plugin A")], [Item("1", "Mod One")]);
        store.ServerStarted(Server, _secondStart, [Item("a", "Plugin A")], [Item("1", "Mod One")]);

        Assert.Equal(2, All(store).Count);
        Assert.All(All(store), marker => Assert.Equal(StoredMarkerKind.Restart, marker.Kind));
    }

    [Fact]
    public void Quasar_restarting_marks_nothing()
    {
        NewStore().ServerStarted(Server, _firstStart, [], []);

        HistoryStore afterRestart = NewStore();
        afterRestart.ServerStarted(Server, _firstStart, [], []);

        Assert.Single(All(afterRestart));
    }

    [Fact]
    public void Markers_are_still_there_after_a_Quasar_restart()
    {
        HistoryStore store = NewStore();
        store.ServerStarted(Server, _firstStart, [], []);

        StoredMarker marker = Assert.Single(All(NewStore()));

        Assert.Equal("Server started", marker.Text);
        Assert.Equal(StoredMarkerKind.Restart, marker.Kind);
    }

    [Fact]
    public void A_start_is_compared_with_the_one_before_a_Quasar_restart()
    {
        NewStore().ServerStarted(Server, _firstStart, [], [Item("1", "Mod One")]);

        HistoryStore afterRestart = NewStore();
        afterRestart.ServerStarted(Server, _secondStart, [], []);

        Assert.Contains(All(afterRestart), marker => marker.Text == "Mod removed: Mod One");
    }

    [Fact]
    public void Markers_past_retention_are_left_behind()
    {
        NewStore().ServerStarted(Server, DateTime.UtcNow.AddDays(-30).Ticks, [], []);

        Assert.Empty(All(NewStore()));
    }

    [Fact]
    public void Servers_keep_their_own_markers()
    {
        HistoryStore store = NewStore();

        store.ServerStarted("another", _firstStart, [], []);

        Assert.Empty(All(store));
    }

    [Fact]
    public void A_new_name_on_the_same_id_is_one_change()
    {
        List<string> changes = RuntimeChanges.Between("Plugin", [Item("a", "A 1.0")], [Item("a", "A 1.1")]);

        Assert.Equal("Plugin changed: A 1.0 to A 1.1", Assert.Single(changes));
    }

    [Fact]
    public void The_same_lists_have_no_changes()
    {
        Assert.Empty(RuntimeChanges.Between("Mod", [Item("1", "One"), Item("2", "Two")], [Item("2", "Two"), Item("1", "One")]));
    }

    [Fact]
    public void A_repeated_id_is_not_a_fault()
    {
        Assert.Empty(RuntimeChanges.Between("Mod", [Item("1", "One"), Item("1", "One")], [Item("1", "One")]));
    }
}
