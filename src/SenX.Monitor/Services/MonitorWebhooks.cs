using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>What happened to an incident that's worth a post.</summary>
public enum WebhookEvent
{
    Opened,
    EvidenceReady,
    Closed,
}

/// <summary>A post waiting to go. Only ids, so the words come from what's true when it's sent.</summary>
internal sealed record WebhookJob(WebhookEvent Event, string ServerId, string IncidentId);

/// <summary>
/// Posts incidents to a Discord webhook: when lag is noticed, when the evidence is in, and when it
/// ends. A webhook only posts, so there's no bot to run and nothing to keep connected. Posts go out
/// on their own queue, so Discord being slow or down can't touch collection, and a failure is only
/// logged. Nothing here runs unless a webhook is set.
/// </summary>
public sealed class MonitorWebhooks : BackgroundService
{
    private const int QueuedPosts = 64;
    private const int RowsPerField = 3;
    private const int MaxTitle = 256;
    private const int MaxDescription = 4096;
    private const int MaxFieldValue = 1024;
    private const int AttemptsWhenLimited = 2;

    private const int ColorLag = 0xE5484D;
    private const int ColorEvidence = 0xF76B15;
    private const int ColorRecovered = 0x30A46C;

    private static readonly TimeSpan s_longestWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_defaultWait = TimeSpan.FromSeconds(5);

    private readonly MonitorIncidents _incidents;
    private readonly MonitorSettings _settings;
    private readonly HistoryStore _store;
    private readonly HttpClient _http;
    private readonly ILogger<MonitorWebhooks> _logger;
    private readonly Channel<WebhookJob> _queue = Channel.CreateBounded<WebhookJob>(new BoundedChannelOptions(QueuedPosts)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    public MonitorWebhooks(MonitorIncidents incidents, MonitorSettings settings, HistoryStore store, HttpClient http, ILogger<MonitorWebhooks> logger)
    {
        _incidents = incidents;
        _settings = settings;
        _store = store;
        _http = http;
        _logger = logger;

        incidents.Changed += (server, incident) => Queue(new WebhookJob(incident.Closed == null ? WebhookEvent.Opened : WebhookEvent.Closed, server, incident.Id));
        incidents.EvidenceArrived += (server, id) => Queue(new WebhookJob(WebhookEvent.EvidenceReady, server, id));
    }

    // These are raised under a lock, so this mustn't wait, and a server with no webhook costs nothing.
    private void Queue(WebhookJob job)
    {
        if (_settings.WebhookFor(job.ServerId) != null)
            _queue.Writer.TryWrite(job);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (WebhookJob job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SendAsync(job, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // A background service that throws stops all of Quasar, and one lost alert doesn't matter that much.
                    // The URL is a secret, so only the kind of failure goes in the log.
                    _logger.LogWarning("Monitor could not send a Discord alert: {Failure}.", exception.GetType().Name);
                }
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Quasar is shutting down, and whatever that interrupted doesn't matter now.
        }
    }

    internal async Task SendAsync(WebhookJob job, CancellationToken cancellationToken)
    {
        string? url = _settings.WebhookFor(job.ServerId);
        StoredIncident? incident = _incidents.Load(job.ServerId, job.IncidentId);

        if (url == null || incident == null)
            return;

        await PostAsync(url, Serialize(Compose(job.Event, ServerName(job.ServerId), incident)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a harmless message, so an admin can see a webhook works. Returns whether Discord took it.</summary>
    public async Task<bool> SendTestAsync(string serverId, CancellationToken cancellationToken)
    {
        string? url = _settings.WebhookFor(serverId);
        if (url == null)
            return false;

        string title = "Monitor can post here";
        string description = "Lag on " + ServerName(serverId) + " will be reported in this channel.";

        return await PostAsync(url, Serialize(new WebhookEmbed(title, description, ColorRecovered, [])), cancellationToken).ConfigureAwait(false);
    }

    private string ServerName(string serverId) => _store.KnownServers().Find(server => server.Id == serverId)?.Name ?? serverId;

    private async Task<bool> PostAsync(string url, string json, CancellationToken cancellationToken)
    {
        try
        {
            for (int attempt = 0; attempt < AttemptsWhenLimited; attempt++)
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    if (!response.IsSuccessStatusCode)
                        _logger.LogWarning("Discord refused a Monitor alert, with status {Status}.", (int)response.StatusCode);

                    return response.IsSuccessStatusCode;
                }

                TimeSpan wait = response.Headers.RetryAfter?.Delta ?? s_defaultWait;
                await Task.Delay(wait < s_longestWait ? wait : s_longestWait, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning("Discord kept limiting a Monitor alert, so it was dropped.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The URL is a secret, so only the kind of failure goes in the log.
            _logger.LogWarning("Monitor could not reach Discord: {Failure}.", exception.GetType().Name);
        }

        return false;
    }

    internal static WebhookEmbed Compose(WebhookEvent kind, string server, StoredIncident incident)
    {
        string reason = MonitorIncidents.ReasonOf(incident);

        switch (kind)
        {
            case WebhookEvent.Opened:
                return new WebhookEmbed("Lag on " + server, Join(reason + ".", Unusual(incident)), ColorLag, []);

            case WebhookEvent.EvidenceReady:
                return new WebhookEmbed("What's behind the lag on " + server, reason + ".", ColorEvidence, Evidence(incident));

            default:
                bool recovered = incident.ClosedBy == "Recovered";
                string how = recovered ? server + " recovered" : server + " restarted during lag";
                string length = Lasted(incident);

                return new WebhookEmbed(
                    how,
                    Invariant($"{length} Lowest sim speed {incident.LowestSim:0.00}, worst frame {incident.WorstFrameMs:0} ms."),
                    ColorRecovered,
                    []);
        }
    }

    private static string? Unusual(StoredIncident incident) =>
        incident.Unusual.Count == 0 ? null : "Unusual for this server: " + string.Join("; ", incident.Unusual.Select(ChatReport.SafeForDiscord)) + ".";

    private static string Join(string first, string? second) => second == null ? first : first + "\n" + second;

    private static string Lasted(StoredIncident incident)
    {
        if (incident.Closed is not { } closed)
            return string.Empty;

        TimeSpan length = closed - incident.Opened;

        return length.TotalMinutes >= 1d
            ? Invariant($"Lasted {length.TotalMinutes:0} min.")
            : Invariant($"Lasted {length.TotalSeconds:0} s.");
    }

    private static List<WebhookField> Evidence(StoredIncident incident)
    {
        IncidentResultResponse? report = incident.Evidence.LastOrDefault(evidence => evidence.Report != null)?.Report;
        var fields = new List<WebhookField>();

        if (report == null)
            return fields;

        if (report.Run is { } run)
        {
            AddList(fields, "Grids", run, RunLists.Grids);
            AddList(fields, "Mods and plugins", run, RunLists.Mods);
            AddList(fields, "Scripts", run, RunLists.Scripts);
            AddList(fields, "Session components", run, RunLists.Session);
        }

        if (report.SlowCalls.Count > 0)
        {
            fields.Add(new WebhookField("Slowest calls", string.Join("\n", report.SlowCalls.Take(RowsPerField)
                .Select(call => Invariant($"{ChatReport.SafeForDiscord(call.Name)}: {call.Ms:0.0} ms")))));
        }

        return fields;
    }

    private static void AddList(List<WebhookField> fields, string title, RunResultResponse run, string list)
    {
        List<RunRow> rows = run.Rows.Where(row => row.List == list).Take(RowsPerField).ToList();

        if (rows.Count > 0)
            fields.Add(new WebhookField(title, string.Join("\n", rows.Select(row => Invariant($"{ChatReport.SafeForDiscord(row.Name)}: {row.MainMsPerFrame + row.WorkerMsPerFrame:0.00} ms/f")))));
    }

    /// <summary>The JSON Discord takes. Mentions are off, because grid names are chosen by players, and one could be "@everyone".</summary>
    internal static string Serialize(WebhookEmbed embed)
    {
        var fields = embed.Fields.Select(field => new { name = field.Name, value = Cut(field.Value, MaxFieldValue), inline = false }).ToList();

        return JsonSerializer.Serialize(new
        {
            username = "Monitor",
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[]
            {
                new
                {
                    title = Cut(embed.Title, MaxTitle),
                    description = Cut(embed.Description, MaxDescription),
                    color = embed.Color,
                    timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    fields,
                },
            },
        });
    }

    private static string Cut(string text, int most) => text.Length <= most ? text : text[..(most - 1)] + "…";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One post's content, before it becomes Discord's JSON.</summary>
internal sealed record WebhookEmbed(string Title, string Description, int Color, IReadOnlyList<WebhookField> Fields);

internal sealed record WebhookField(string Name, string Value);
