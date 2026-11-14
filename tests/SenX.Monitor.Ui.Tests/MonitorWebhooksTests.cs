using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class MonitorWebhooksTests : IDisposable
{
    private const string Server = "server-1";
    private const string OwnUrl = "https://discord.com/api/webhooks/1/own";
    private const string SharedUrl = "https://discord.com/api/webhooks/2/shared";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _started = DateTime.UtcNow.AddHours(-1);
    private readonly ScriptedServers _servers = new();
    private readonly FakeDiscord _discord = new();
    private readonly MonitorSettings _settings;
    private readonly HistoryStore _store;
    private readonly MonitorIncidents _incidents;
    private readonly MonitorWebhooks _webhooks;

    public MonitorWebhooksTests()
    {
        _settings = new MonitorSettings(_directory);
        _store = new HistoryStore(_directory, _settings, NullLogger<HistoryStore>.Instance);
        _incidents = new MonitorIncidents(
            _servers,
            _directory,
            _settings,
            new MonitorBaselines(_store, _settings, NullLogger<MonitorBaselines>.Instance),
            NullLogger<MonitorIncidents>.Instance);
        _webhooks = new MonitorWebhooks(_incidents, _settings, _store, new HttpClient(_discord), NullLogger<MonitorWebhooks>.Instance);
    }

    public void Dispose()
    {
        _webhooks.Dispose();

        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeDiscord : HttpMessageHandler
    {
        public List<(string Url, string Body)> Posts { get; } = [];

        public Queue<HttpStatusCode> Replies { get; } = new();

        public bool Down { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
                throw new HttpRequestException("Discord is down.");

            string body = await request.Content!.ReadAsStringAsync(cancellationToken);

            lock (Posts)
                Posts.Add((request.RequestUri!.ToString(), body));

            var response = new HttpResponseMessage(Replies.Count > 0 ? Replies.Dequeue() : HttpStatusCode.NoContent);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));

            return response;
        }
    }

    private string Open()
    {
        var batch = new HistoryBatch { StartedUtcTicks = _started.Ticks };
        batch.Markers.Add(new HistoryMarker { UnitsSinceStart = TimeSpan.FromSeconds(100).Ticks, Kind = HistoryMarkerKind.LagTripped, Value = 1U });
        _incidents.Apply(Server, batch);

        return _incidents.List(Server)[0].Id;
    }

    private Task SendAsync(WebhookEvent kind, string id) => _webhooks.SendAsync(new WebhookJob(kind, Server, id), CancellationToken.None);

    private static StoredIncident Incident(string closedBy = "Recovered")
    {
        var opened = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        return new StoredIncident { Opened = opened, Closed = opened.AddMinutes(2), ClosedBy = closedBy, LowestSim = 0.62d, WorstFrameMs = 240d };
    }

    [Fact]
    public async Task Lag_is_posted_to_the_servers_webhook()
    {
        _settings.SetWebhook(Server, OwnUrl);

        await SendAsync(WebhookEvent.Opened, Open());

        (string url, string body) = Assert.Single(_discord.Posts);
        Assert.Equal(OwnUrl, url);
        Assert.Contains("Lag on " + Server, body);
    }

    [Fact]
    public async Task No_webhook_means_nothing_is_posted()
    {
        await SendAsync(WebhookEvent.Opened, Open());

        Assert.Empty(_discord.Posts);
    }

    [Fact]
    public async Task A_server_without_its_own_uses_the_one_for_all()
    {
        _settings.SetWebhook(null, SharedUrl);

        await SendAsync(WebhookEvent.Opened, Open());

        Assert.Equal(SharedUrl, Assert.Single(_discord.Posts).Url);
    }

    [Fact]
    public async Task A_servers_own_webhook_comes_first()
    {
        _settings.SetWebhook(null, SharedUrl);
        _settings.SetWebhook(Server, OwnUrl);

        await SendAsync(WebhookEvent.Opened, Open());

        Assert.Equal(OwnUrl, Assert.Single(_discord.Posts).Url);
    }

    [Fact]
    public async Task A_server_name_is_used_when_its_known()
    {
        _store.NoteServer(new MonitorServer("node", "Main Server", Server));
        _settings.SetWebhook(Server, OwnUrl);

        await SendAsync(WebhookEvent.Opened, Open());

        Assert.Contains("Lag on Main Server", _discord.Posts[0].Body);
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/abc", true)]
    [InlineData("https://ptb.discord.com/api/webhooks/1/abc", true)]
    [InlineData("https://canary.discord.com/api/webhooks/1/abc", true)]
    [InlineData("https://discordapp.com/api/webhooks/1/abc", true)]
    [InlineData("http://discord.com/api/webhooks/1/abc", false)]
    [InlineData("https://discord.com/other/1/abc", false)]
    [InlineData("https://discord.com.example.net/api/webhooks/1/abc", false)]
    [InlineData("https://example.com/api/webhooks/1/abc", false)]
    [InlineData("https://localhost/api/webhooks/1/abc", false)]
    [InlineData("not a url", false)]
    public void Only_Discord_webhooks_are_taken(string url, bool taken)
    {
        Assert.Equal(taken, _settings.SetWebhook(Server, url));
        Assert.Equal(taken, _settings.WebhookFor(Server) != null);
    }

    [Fact]
    public void Removing_a_webhook_stops_it()
    {
        _settings.SetWebhook(Server, OwnUrl);

        Assert.True(_settings.SetWebhook(Server, null));

        Assert.Null(_settings.WebhookFor(Server));
    }

    [Fact]
    public void A_webhook_is_still_there_after_a_Quasar_restart()
    {
        _settings.SetWebhook(Server, OwnUrl);
        _settings.SetWebhook(null, SharedUrl);

        var reopened = new MonitorSettings(_directory);

        Assert.Equal(OwnUrl, reopened.WebhookFor(Server));
        Assert.Equal(SharedUrl, reopened.WebhookFor("another"));
    }

    [Fact]
    public void Changing_limits_leaves_webhooks_alone()
    {
        _settings.SetWebhook(Server, OwnUrl);
        _settings.Triggers = new TriggerSettings();

        Assert.Equal(OwnUrl, new MonitorSettings(_directory).WebhookFor(Server));
    }

    [Fact]
    public void Mentions_are_off_because_players_choose_grid_names()
    {
        string json = MonitorWebhooks.Serialize(new WebhookEmbed("Title", "@everyone", 1, []));

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(0, document.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
    }

    [Fact]
    public void Long_text_is_cut_to_what_Discord_takes()
    {
        string json = MonitorWebhooks.Serialize(new WebhookEmbed(new string('a', 500), new string('b', 5000), 1, [new WebhookField("Name", new string('c', 2000))]));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement embed = document.RootElement.GetProperty("embeds")[0];

        Assert.Equal(256, embed.GetProperty("title").GetString()!.Length);
        Assert.Equal(4096, embed.GetProperty("description").GetString()!.Length);
        Assert.Equal(1024, embed.GetProperty("fields")[0].GetProperty("value").GetString()!.Length);
    }

    [Fact]
    public void The_evidence_post_names_the_cause()
    {
        StoredIncident incident = Incident();
        incident.Evidence.Add(new IncidentEvidence
        {
            Report = new IncidentResultResponse
            {
                Reason = "SimSpeed",
                Value = 0.62d,
                Run = new RunResultResponse { Rows = [new RunRow { List = RunLists.Grids, Name = "Big Ship", MainMsPerFrame = 3d, WorkerMsPerFrame = 1d }] },
                SlowCalls = [new SlowCallRow { Name = "Drill", Ms = 40d }],
            },
        });

        WebhookEmbed embed = MonitorWebhooks.Compose(WebhookEvent.EvidenceReady, "Main", incident);

        Assert.Contains("0.62", embed.Description);
        Assert.Equal("Big Ship: 4.00 ms/f", embed.Fields.Single(field => field.Name == "Grids").Value);
        Assert.Equal("Drill: 40.0 ms", embed.Fields.Single(field => field.Name == "Slowest calls").Value);
    }

    [Fact]
    public void Lists_with_nothing_in_them_arent_posted()
    {
        WebhookEmbed embed = MonitorWebhooks.Compose(WebhookEvent.EvidenceReady, "Main", Incident());

        Assert.Empty(embed.Fields);
    }

    [Fact]
    public void The_recovery_post_says_how_long_and_how_bad()
    {
        WebhookEmbed embed = MonitorWebhooks.Compose(WebhookEvent.Closed, "Main", Incident());

        Assert.Equal("Main recovered", embed.Title);
        Assert.Contains("Lasted 2 min", embed.Description);
        Assert.Contains("0.62", embed.Description);
        Assert.Contains("240 ms", embed.Description);
    }

    [Fact]
    public void A_restart_during_lag_isnt_called_a_recovery()
    {
        Assert.Equal("Main restarted during lag", MonitorWebhooks.Compose(WebhookEvent.Closed, "Main", Incident("Server restarted")).Title);
    }

    [Fact]
    public async Task Being_rate_limited_waits_and_tries_again()
    {
        _settings.SetWebhook(Server, OwnUrl);
        _discord.Replies.Enqueue(HttpStatusCode.TooManyRequests);

        await SendAsync(WebhookEvent.Opened, Open());

        Assert.Equal(2, _discord.Posts.Count);
    }

    [Fact]
    public async Task Staying_rate_limited_gives_up()
    {
        _settings.SetWebhook(Server, OwnUrl);
        _discord.Replies.Enqueue(HttpStatusCode.TooManyRequests);
        _discord.Replies.Enqueue(HttpStatusCode.TooManyRequests);
        _discord.Replies.Enqueue(HttpStatusCode.TooManyRequests);

        await SendAsync(WebhookEvent.Opened, Open());

        Assert.Equal(2, _discord.Posts.Count);
    }

    [Fact]
    public async Task Discord_being_down_isnt_a_fault()
    {
        _settings.SetWebhook(Server, OwnUrl);
        _discord.Down = true;

        await SendAsync(WebhookEvent.Opened, Open());
    }

    [Fact]
    public async Task A_test_says_if_Discord_took_it()
    {
        _settings.SetWebhook(Server, OwnUrl);

        Assert.True(await _webhooks.SendTestAsync(Server, CancellationToken.None));

        _discord.Replies.Enqueue(HttpStatusCode.NotFound);
        Assert.False(await _webhooks.SendTestAsync(Server, CancellationToken.None));
    }

    [Fact]
    public async Task A_test_with_no_webhook_says_so()
    {
        Assert.False(await _webhooks.SendTestAsync(Server, CancellationToken.None));
    }

    [Fact]
    public async Task An_incident_reaches_the_queue_by_itself()
    {
        _settings.SetWebhook(Server, OwnUrl);
        await _webhooks.StartAsync(CancellationToken.None);

        Open();

        for (int i = 0; i < 2000 && _discord.Posts.Count == 0; i++)
            await Task.Delay(5);

        await _webhooks.StopAsync(CancellationToken.None);

        Assert.Contains("Lag on", Assert.Single(_discord.Posts).Body);
    }

    [Fact]
    public async Task Evidence_arriving_says_so()
    {
        string? heard = null;
        _incidents.EvidenceArrived += (_, id) => heard = id;

        string id = Open();
        _servers.Incidents[1L] = new IncidentResultResponse { State = RunState.Finished, Reason = "SimSpeed", Value = 0.5d };

        await _incidents.CollectEvidenceAsync(new MonitorServer(Server, "One", Server), _started.Ticks, CancellationToken.None);

        Assert.Equal(id, heard);
    }
}
