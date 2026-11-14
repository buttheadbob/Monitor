using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class IncidentTests : IDisposable
{
    private const string Server = "server-1";

    private static readonly MonitorServer s_server = new(Server, "One", Server);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _started = DateTime.UtcNow.AddHours(-1);
    private readonly ScriptedServers _servers = new();
    private readonly MonitorSettings _settings;
    private readonly HistoryStore _store;
    private MonitorIncidents _incidents;

    public IncidentTests()
    {
        _settings = new MonitorSettings(_directory);
        _store = new HistoryStore(_directory, _settings, NullLogger<HistoryStore>.Instance);
        _incidents = NewIncidents();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private MonitorIncidents NewIncidents() => new(
        _servers,
        _directory,
        _settings,
        new MonitorBaselines(_store, _settings, NullLogger<MonitorBaselines>.Instance),
        NullLogger<MonitorIncidents>.Instance);

    private HistoryBatch Trip(int second, uint id = 1U)
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        batch.Markers.Add(new HistoryMarker { UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks, Kind = HistoryMarkerKind.LagTripped, Value = id });
        return batch;
    }

    private HistoryBatch Seconds(int first, int count, uint simulatedFrames)
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };

        for (int second = first; second < first + count; second++)
        {
            batch.Rollups.Add(new HistoryRollup
            {
                UnitsSinceStart = TimeSpan.FromSeconds(second).Ticks,
                Frames = simulatedFrames,
                SimulatedFrames = simulatedFrames,
                WindowUnits = 10_000_000U,
                FrameMax = 300000U,
            });
        }

        return batch;
    }

    private void Open(int second = 100)
    {
        _incidents.Apply(Server, Trip(second));
    }

    private IncidentInfo Only() => Assert.Single(_incidents.List(Server));

    [Fact]
    public void A_trip_opens_an_incident()
    {
        Open();

        IncidentInfo incident = Only();
        Assert.Equal(_started.AddSeconds(100), incident.Opened);
        Assert.Null(incident.Closed);
        Assert.Equal(incident.Opened, _incidents.OpenSince(Server));
    }

    [Fact]
    public void Other_markers_dont()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        batch.Markers.Add(new HistoryMarker { Kind = HistoryMarkerKind.Saved });

        _incidents.Apply(Server, batch);

        Assert.Empty(_incidents.List(Server));
    }

    [Fact]
    public void An_incident_tracks_how_bad_it_got()
    {
        Open();

        _incidents.Apply(Server, Seconds(100, 5, 30U));

        Assert.Equal(0.5d, Only().LowestSim, 6);
        Assert.Equal(30d, Only().WorstFrameMs, 6);
    }

    [Fact]
    public void Seconds_from_before_it_opened_dont_count()
    {
        Open();

        _incidents.Apply(Server, Seconds(50, 5, 6U));

        Assert.Equal(1d, Only().LowestSim, 6);
    }

    [Fact]
    public void It_closes_after_a_healthy_minute_at_the_first_healthy_second()
    {
        Open();

        _incidents.Apply(Server, Seconds(100, 10, 30U));
        _incidents.Apply(Server, Seconds(110, 61, 60U));

        IncidentInfo incident = Only();
        Assert.Equal(_started.AddSeconds(110), incident.Closed);
        Assert.Equal("Recovered", incident.ClosedBy);
        Assert.Null(_incidents.OpenSince(Server));
    }

    [Fact]
    public void A_healthy_stretch_short_of_a_minute_doesnt_close_it()
    {
        Open();

        _incidents.Apply(Server, Seconds(110, 59, 60U));

        Assert.Null(Only().Closed);
    }

    [Fact]
    public void A_dip_in_the_healthy_stretch_starts_it_again()
    {
        Open();

        _incidents.Apply(Server, Seconds(110, 50, 60U));
        _incidents.Apply(Server, Seconds(160, 1, 30U));
        _incidents.Apply(Server, Seconds(161, 50, 60U));

        Assert.Null(Only().Closed);
    }

    [Fact]
    public void Lag_that_outlasts_the_cooldown_stays_one_incident()
    {
        Open();

        _incidents.Apply(Server, Trip(1000, 2U));

        Assert.Equal(2, _incidents.Load(Server, Only().Id)!.Evidence.Count);
    }

    [Fact]
    public void A_restart_ends_it()
    {
        Open();

        _incidents.ServerRestarted(Server, _started.AddMinutes(30).Ticks);

        Assert.Equal("Server restarted", Only().ClosedBy);
    }

    [Fact]
    public void Open_since_ends_when_the_incident_closes()
    {
        Open();
        Assert.NotNull(_incidents.OpenSince(Server));

        _incidents.ServerRestarted(Server, _started.AddMinutes(30).Ticks);

        Assert.Null(_incidents.OpenSince(Server));
    }

    [Fact]
    public async Task Evidence_hands_back_the_run_it_held()
    {
        Open();
        _servers.Incidents[1L] = new IncidentResultResponse { State = RunState.Finished, Reason = "SimSpeed", Value = 0.61d, Run = new RunResultResponse { State = RunState.Finished } };

        var runs = new List<RunResultResponse>();

        await _incidents.CollectEvidenceAsync(s_server, _started.Ticks, CancellationToken.None, runs.Add);

        Assert.Single(runs);
    }

    [Fact]
    public void An_open_incident_is_still_open_after_a_Quasar_restart()
    {
        Open();

        _incidents = NewIncidents();

        Assert.NotNull(_incidents.OpenSince(Server));
    }

    [Fact]
    public async Task Evidence_arrives_when_the_worker_has_it()
    {
        Open();
        _servers.Incidents[1L] = new IncidentResultResponse { State = RunState.Finished, Reason = "SimSpeed", Value = 0.61d };

        await _incidents.CollectEvidenceAsync(s_server, _started.Ticks, CancellationToken.None);

        Assert.Equal("Sim speed fell to 0.61", Only().Reason);
    }

    [Fact]
    public async Task Evidence_still_being_gathered_is_asked_for_again()
    {
        Open();

        await _incidents.CollectEvidenceAsync(s_server, _started.Ticks, CancellationToken.None);
        Assert.Equal("Lag noticed", Only().Reason);

        _servers.Incidents[1L] = new IncidentResultResponse { State = RunState.Finished, Reason = "Spike", Value = 2500d };
        await _incidents.CollectEvidenceAsync(s_server, _started.Ticks, CancellationToken.None);

        Assert.Equal("One frame took 2500 ms", Only().Reason);
    }

    [Fact]
    public async Task Evidence_arrives_even_after_it_recovered()
    {
        Open();
        _incidents.Apply(Server, Seconds(110, 61, 60U));

        _servers.Incidents[1L] = new IncidentResultResponse { State = RunState.Finished, Reason = "SimSpeed", Value = 0.5d };
        await _incidents.CollectEvidenceAsync(s_server, _started.Ticks, CancellationToken.None);

        Assert.NotNull(_incidents.Load(Server, Only().Id)!.Evidence[0].Report);
    }

    [Fact]
    public async Task Evidence_from_before_a_restart_is_written_off()
    {
        Open();

        await _incidents.CollectEvidenceAsync(s_server, _started.AddMinutes(30).Ticks, CancellationToken.None);

        Assert.NotNull(_incidents.Load(Server, Only().Id)!.Evidence[0].Lost);
    }

    [Fact]
    public void A_loaded_incident_is_a_copy()
    {
        Open();

        _incidents.Load(Server, Only().Id)!.LowestSim = 0d;

        Assert.Equal(1d, Only().LowestSim, 6);
    }

    [Fact]
    public void Servers_have_their_own_incidents()
    {
        Open();

        Assert.Empty(_incidents.List("another"));
    }

    [Fact]
    public void Old_finished_incidents_go_with_the_retention_setting()
    {
        var old = new HistoryBatch { StartedUtcTicks = DateTime.UtcNow.AddDays(-30).Ticks };
        old.Markers.Add(new HistoryMarker { Kind = HistoryMarkerKind.LagTripped, Value = 1U });

        _incidents.Apply(Server, old);
        _incidents.ServerRestarted(Server, DateTime.UtcNow.AddDays(-29).Ticks);

        Assert.Empty(_incidents.List(Server));
    }

    [Fact]
    public void Quiet_seconds_change_nothing()
    {
        _incidents.Apply(Server, Seconds(0, 100, 60U));

        Assert.Empty(_incidents.List(Server));
    }

    [Fact]
    public void Limits_stay_within_what_makes_sense()
    {
        _settings.Triggers = new TriggerSettings { SimBelow = 5d, Seconds = 0, CooldownSeconds = 1, RunSeconds = 500, JoinGraceSeconds = 900 };

        TriggerSettings limits = _settings.Triggers;

        Assert.Equal(1d, limits.SimBelow, 6);
        Assert.Equal(1, limits.Seconds);
        Assert.Equal(60, limits.CooldownSeconds);
        Assert.Equal(60, limits.RunSeconds);
        Assert.Equal(120, limits.JoinGraceSeconds);
    }

    [Fact]
    public void The_join_grace_can_be_turned_off_but_not_below_zero()
    {
        _settings.Triggers = new TriggerSettings { JoinGraceSeconds = -5 };

        Assert.Equal(0, _settings.Triggers.JoinGraceSeconds);
        Assert.Equal(15, new TriggerSettings().JoinGraceSeconds);
    }

    [Fact]
    public void Limits_are_still_there_after_a_Quasar_restart()
    {
        _settings.Triggers = new TriggerSettings { Seconds = 12 };

        Assert.Equal(12, new MonitorSettings(_directory).Triggers.Seconds);
    }

    [Fact]
    public void Changing_limits_tells_the_collector_to_send_them()
    {
        long version = _settings.TriggersVersion;

        _settings.Triggers = new TriggerSettings();

        Assert.NotEqual(version, _settings.TriggersVersion);
    }

    [Fact]
    public void Player_limits_stay_within_what_makes_sense()
    {
        _settings.Triggers = new TriggerSettings { PlayerTestSeconds = 500, PlayerTestCooldownSeconds = 1, PlayerTestLowestSim = 5d };

        TriggerSettings limits = _settings.Triggers;

        Assert.Equal(60, limits.PlayerTestSeconds);
        Assert.Equal(60, limits.PlayerTestCooldownSeconds);
        Assert.Equal(1d, limits.PlayerTestLowestSim, 6);
    }

    [Fact]
    public void Player_commands_can_be_turned_off()
    {
        _settings.Triggers = new TriggerSettings { PlayerMine = false, PlayerTest = false };

        Assert.False(new MonitorSettings(_directory).Triggers.PlayerMine);
        Assert.False(new MonitorSettings(_directory).Triggers.PlayerTest);
    }

    [Fact]
    public void The_retention_setting_survives_changing_limits()
    {
        _settings.RetentionDays = 5;
        _settings.Triggers = new TriggerSettings();

        Assert.Equal(5, new MonitorSettings(_directory).RetentionDays);
    }
}
