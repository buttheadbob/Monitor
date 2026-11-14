using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Services;
using SenX.Monitor.Torch.Web;

namespace SenX.Monitor.Torch;

/// <summary>
/// Everything behind the pages, wired by hand because Torch has no service container. These are the
/// same classes Quasar's build runs, so history, incidents, runs and alerts behave the same. They run
/// whether or not the window is open, so a problem overnight can still be looked at in the morning.
/// </summary>
public sealed class MonitorServices : IHostApplicationLifetime, IDisposable
{
    private static readonly TimeSpan s_webhookTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _closing = new();
    private readonly object _boardGate = new();

    private BoardWebServer? _board;

    public MonitorServices(IMonitorServers servers, string directory)
    {
        Servers = servers;
        Settings = new MonitorSettings(directory);
        Store = new HistoryStore(directory, Settings, new SinkLogger<HistoryStore>());
        Charts = new ChartBuilder(Store, servers);
        Windows = new HistoryWindows(Store);
        Boards = new BoardBuilder(Store, servers, Settings);
        Baselines = new MonitorBaselines(Store, Settings, new SinkLogger<MonitorBaselines>());
        Incidents = new MonitorIncidents(servers, directory, Settings, Baselines, new SinkLogger<MonitorIncidents>());
        Runs = new MonitorRuns(servers, directory, Settings, this, new SinkLogger<MonitorRuns>());
        Collector = new MonitorCollector(servers, Store, Incidents, Runs, Settings, new SinkLogger<MonitorCollector>());
        Webhooks = new MonitorWebhooks(Incidents, Settings, Store, new HttpClient { Timeout = s_webhookTimeout }, new SinkLogger<MonitorWebhooks>());
        Api = new MonitorApi(servers, Store, Windows, Runs, Collector, Incidents, Settings, this, new SinkLogger<MonitorApi>());
    }

    public IMonitorServers Servers { get; }

    public MonitorSettings Settings { get; }

    public HistoryStore Store { get; }

    public ChartBuilder Charts { get; }

    public HistoryWindows Windows { get; }

    public BoardBuilder Boards { get; }

    public MonitorBaselines Baselines { get; }

    public MonitorIncidents Incidents { get; }

    public MonitorRuns Runs { get; }

    public MonitorCollector Collector { get; }

    public MonitorWebhooks Webhooks { get; }

    public MonitorApi Api { get; }

    public CancellationToken ApplicationStopping => _closing.Token;

    public void Start()
    {
        Collector.Start();
        Webhooks.Start();
    }

    /// <summary>Starts the small web server the first time a board is opened, and gives the board's address.</summary>
    public string BoardAddress()
    {
        lock (_boardGate)
        {
            _board ??= new BoardWebServer(this);
            return _board.Address;
        }
    }

    public void Dispose()
    {
        lock (_boardGate)
        {
            _board?.Dispose();
            _board = null;
        }

        _closing.Cancel();
        Collector.Dispose();
        Webhooks.Dispose();
    }
}
