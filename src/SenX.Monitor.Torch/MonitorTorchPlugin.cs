using System.Reflection;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using NLog;
using Sandbox;
using SenX.Monitor.Magnetar;
using SenX.Monitor.Magnetar.Patching;
using SenX.Monitor.Magnetar.Reporting;
using SenX.Monitor.Services;
using SenX.Monitor.Torch.Ui;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.Managers.PatchManager;
using Torch.Server.Views;

namespace SenX.Monitor.Torch;

/// <summary>
/// Monitor as one Torch plugin. On Quasar the worker and the pages are separate processes. Here they
/// share the game's, so everything behind the pages stays off the game thread and allocates as little as
/// it can. If anything goes wrong, Monitor shuts itself down and the game carries on without it.
/// </summary>
public sealed class MonitorTorchPlugin : TorchPluginBase, IWpfPlugin
{
    private const string DataFolder = "Monitor";
    private const string WorkerFailed = "Monitor couldn't start measuring, so these pages won't change. Torch's log says why.";
    private const string ProfilerInstalled = "The Profiler plugin is also installed. Both time the same methods, so both read a little high, and Monitor won't time physics clusters alongside it.";
    private const string HarmonyAssembly = "0Harmony";

    private static readonly NLog.Logger s_log = LogManager.GetLogger("Monitor");

    private readonly Worker _worker = new();

    private ITorchBase? _torch;
    private MonitorServices? _services;
    private UserControl? _control;
    private string? _problem;
    private bool _started;
    private int _harmonyReported;

    public override void Init(ITorchBase torch)
    {
        base.Init(torch);

        _torch = torch;
        MonitorLogSink.Write = (text, exception) => s_log.Warn(exception, text);

        // Other plugins' assemblies are all loaded by now, and this catches any that load later.
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

        if (AppDomain.CurrentDomain.GetAssemblies().Any(IsHarmony))
            ReportHarmony();

        try
        {
            _worker.Prepare();
            PatchEngine.Manager = torch.Managers.GetManager<PatchManager>();

            string name = string.IsNullOrEmpty(torch.Config.InstanceName) ? "This server" : torch.Config.InstanceName;

            _services = new MonitorServices(new LocalServers(name), Path.Combine(StoragePath, DataFolder));

            // Torch has the whole history right here, so other plugins get all of it, and the worker's rings for what's newest.
            MonitorTools tools = _services.Api.ToolsFor(LocalServers.LocalId);
            tools.Live = ServerApi.Instance;
            _worker.Api = tools;
            _services.Start();

            // Patching waits for the game's static constructors, and Monitor does it itself rather than
            // leaving it to Torch's commit, where a failure would stop the server starting.
            if (torch.GameState >= TorchGameState.Created && torch.GameState < TorchGameState.Unloading)
                StartWorker();
            else
                torch.GameStateChanged += OnGameStateChanged;
        }
        catch (Exception exception)
        {
            // Nothing here may stop Torch starting.
            s_log.Error(exception, "Monitor failed to start and has shut itself down.");
            _problem = WorkerFailed;
            _services?.Dispose();
            _services = null;
        }
    }

    private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        if (IsHarmony(args.LoadedAssembly))
            ReportHarmony();
    }

    private static bool IsHarmony(Assembly assembly) => assembly.GetName().Name == HarmonyAssembly;

    // Torch's plugins patch through Torch alone. Only the assembly's name is looked at, and Harmony itself is never touched.
    private void ReportHarmony()
    {
        if (Interlocked.Exchange(ref _harmonyReported, 1) != 0)
            return;

        try
        {
            s_log.Error("A plugin has loaded Harmony" + WhoBroughtHarmony() + ". It will break Torch's patching, including Monitor's. Remove that plugin, because Torch plugins must patch through Torch.");
        }
        catch (Exception exception)
        {
            s_log.Error(exception, "A plugin has loaded Harmony, and Monitor could not work out which.");
        }
    }

    private string WhoBroughtHarmony()
    {
        List<string> names = (_torch?.Managers.GetManager<IPluginManager>().Plugins.Values ?? Enumerable.Empty<ITorchPlugin>())
            .Where(plugin => plugin.GetType().Assembly.GetReferencedAssemblies().Any(reference => reference.Name == HarmonyAssembly))
            .Select(plugin => plugin.Name)
            .ToList();

        return names.Count == 0 ? string.Empty : " (" + string.Join(", ", names) + ")";
    }

    private void OnGameStateChanged(MySandboxGame game, TorchGameState state)
    {
        if (state == TorchGameState.Created)
            StartWorker();
    }

    private void StartWorker()
    {
        if (_started)
            return;

        _started = true;

        // A rare start-up hang was seen once, on 2026-11-08, in a container test on Torch under Proton: Monitor's patch summary
        // was logged, then nothing, and Torch never logged "Starting new torch session". That was about 1 in 700 starts. On
        // 2026-11-13 it was searched for with 52 full starts, then 1,000 starts each stopped at that line, with a 180 s detector
        // and thread dumps ready. It didn't happen again: every start reached its session 41-65 s in, at most 34 s after the
        // summary. Its cause is unknown, and nothing here is known to block once Start returns.
        try
        {
            _worker.Start();

            if (_torch!.Managers.GetManager<IPluginManager>().Plugins.Values.Any(plugin => plugin.Name.IndexOf("Profiler", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                s_log.Warn(ProfilerInstalled);
                _problem = ProfilerInstalled;
            }
        }
        catch (Exception exception)
        {
            s_log.Error(exception, "Monitor could not start measuring and has shut itself down.");
            _problem = WorkerFailed;
            _worker.GiveUp();
        }
    }

    public override void Update() => _worker.Update();

    public override void Dispose()
    {
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;

        if (_torch != null)
            _torch.GameStateChanged -= OnGameStateChanged;

        (_control as MonitorView)?.Stop();
        _services?.Dispose();
        _services = null;

        try
        {
            _worker.Shutdown();
        }
        catch (Exception exception)
        {
            s_log.Error(exception, "Monitor could not shut down cleanly.");
        }

        MonitorLogSink.Write = null;
        base.Dispose();
    }

    /// <summary>Torch asks on its UI thread, which is the thread that has to own the control.</summary>
    public UserControl GetControl()
    {
        if (_control != null)
            return _control;

        if (_services == null)
        {
            _control = new UserControl { Content = new TextBlock { Text = WorkerFailed, Margin = new System.Windows.Thickness(10) } };
            return _control;
        }

        _control = new MonitorView(_services, () => Worker.Closed ? WorkerFailed : _problem);

        // Tabs scroll for themselves, so the header stays put and charts get a real height to work with.
        _control.SetScrollContainer(false);
        return _control;
    }
}
