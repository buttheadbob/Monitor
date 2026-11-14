using System;
using System.Reflection;
using System.Threading;
using Sandbox;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.ModAPI;
using SenX.Monitor.Api;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Hooks;
using SenX.Monitor.Magnetar.Patching;
using SenX.Monitor.Magnetar.Reporting;
using SenX.Monitor.Magnetar.World;

namespace SenX.Monitor.Magnetar;

/// <summary>
/// The worker, whichever host loaded it. It patches once, then only measures. Magnetar and Torch
/// each start it and call <see cref="Update"/> every frame on the game thread. If anything here goes
/// wrong, Monitor shuts itself down and the game carries on as if it were never installed.
/// </summary>
internal sealed class Worker
{
    // Long enough for tiered compilation to settle, so calibration measures optimized code.
    private const int WarmUpSeconds = 30;
    private const int LogEverySeconds = 60;
    private const string ApiAssembly = "SenX.Monitor.Api";

    private static readonly object s_historyGate = new();
    private static readonly byte[] s_history = new byte[HistoryWriter.MaxBytes];

    private static int s_seconds;
    private static int s_detailMemoryReady;

    private bool _running;
    private bool _gameReady;
    private bool _detailBound;
    private bool _detailStarted;
    private bool _modsRead;
    private bool _pluginsHooked;
    private bool _lostSitesLogged;
    private IMyEntities? _entities;

    /// <summary>What other plugins on this server get. A host with the whole history gives them that instead.</summary>
    public IMonitorServerApi Api { get; set; } = ServerApi.Instance;

    /// <summary>Everything that doesn't touch the game. Other code can read the rings from here on.</summary>
    public void Prepare()
    {
        Closed = false;
        Capture.Closed = false;
        AppDomain.CurrentDomain.AssemblyResolve += ResolveApi;
        MonitorCore.Initialize();
    }

    /// <summary>Patches the game and starts measuring. Call once it's safe to patch.</summary>
    public void Start()
    {
        PatchEngine.Apply();
        MonitorLog.Info(SpikeReport.Patches().ToString());
        _running = true;
    }

    // Other plugins compile against the API and don't ship it, so their reference lands on Monitor's copy.
    private static Assembly? ResolveApi(object? sender, ResolveEventArgs args)
    {
        // By its fixed name, because Magnetar compiles the API into this plugin, under a name of its own.
        // Compared as text, because parsing a name can throw, and this runs inside someone else's load.
        return args.Name == ApiAssembly || args.Name.StartsWith(ApiAssembly + ",", StringComparison.OrdinalIgnoreCase)
            ? typeof(IMonitorServerApi).Assembly
            : null;
    }

    public void Update()
    {
        if (!_running)
            return;

        try
        {
            if (FrameEngine.TakeFault() is { } frameFault)
            {
                MonitorLog.Error("Monitor's frame hook failed, so it has shut itself down. The game isn't affected.", frameFault);
                GiveUp();
                return;
            }

            if (!_gameReady)
            {
                if (MySandboxGame.IsGameReady)
                    OnGameReady();

                return;
            }

            if (!_detailBound && Volatile.Read(ref s_detailMemoryReady) == 1)
            {
                ThreadBlocks.BindMainThread();
                _detailBound = true;
            }

            if (!_modsRead && MySession.Static != null)
                _modsRead = ServerInventory.ReadMods(MySession.Static);

            if (!_pluginsHooked && ServerInventory.PluginTypes is { } plugins)
            {
                _pluginsHooked = true;
                PatchEngine.HookPlugins(plugins);
            }

            PhysicsHooks.RestoreOptimizer();
            Capture.Deliver();
            Markers.Flush();
            IncidentService.Step();
            PatchAudit.Step();
            DrillDown.Step();
            ServerApi.Instance.Step();
            CensusWalker.Step();

            if (Sweep.TakeFault() is { } fault)
                MonitorLog.Error("Monitor's background total failed, so that one was dropped. The next one carries on.", fault);

            if (SlotMeta.TakeFault() is { } slotFault)
                MonitorLog.Error("Monitor could not read what an entity is, so detail has been turned off.", slotFault);
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor failed and has shut itself down.", exception);
            GiveUp();
        }
    }

    private void OnGameReady()
    {
        _gameReady = true;

        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.Ticks;
        FrameEngine.SecondElapsed = OnSecond;
        FrameEngine.Start();
        MySession.OnSaved += OnWorldSaved;
        _entities = MyAPIGateway.Entities;
        _entities.OnEntityRemove += LagReport.OnEntityRemoved;
        _entities.OnEntityAdd += OnEntityAdded;
        IncidentService.Start();
        Sweep.OwnerOf = CodeOwners.OwnerOf;
        MonitorServerApi.Register(Api);

        // Clearing memory touches every page, so it happens on a low priority thread, never the game's.
        var thread = new Thread(SetUpOffGameThread)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Monitor setup",
        };

        thread.Start();
    }

    private static void OnWorldSaved(bool succeeded, string path) => Markers.NoteSave(succeeded);

    private static void SetUpOffGameThread()
    {
        ServerInventory.ReadPlugins();
        PrepareDetailMemory();
    }

    private static void PrepareDetailMemory()
    {
        try
        {
            ThreadBlocks.Initialize(DetailLayout.Default(Math.Min(Environment.ProcessorCount, 32)));
            Volatile.Write(ref s_detailMemoryReady, 1);
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not allocate detail memory. The tree keeps running.", exception);
        }
    }

    private void OnSecond()
    {
        try
        {
            s_seconds++;

            if (!_detailStarted && s_seconds >= WarmUpSeconds && _detailBound)
            {
                _detailStarted = true;
                Calibration.Run();
                DetailController.Configure(PatchEngine.PatchedSites());
                DetailController.SetMode(DetailMode.Grouped);
            }

            // On its own, because a report of what can't be timed mustn't depend on detail starting. By now every
            // plugin has patched what it's going to, whichever order they loaded in. Off the frame hook, like the status.
            if (!_lostSitesLogged && s_seconds >= WarmUpSeconds)
            {
                _lostSitesLogged = true;
                ThreadPool.QueueUserWorkItem(_ => LogLostSites());
            }

            // Not here, because this is inside the game's frame, and the report reads an hour of rings to build.
            if (s_seconds % LogEverySeconds == 0)
                ThreadPool.QueueUserWorkItem(_ => LogStatus());
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor's detail control failed. Detail is off, the tree keeps running.", exception);
            DetailController.SetMode(DetailMode.Off);
        }
    }

    private static void LogLostSites()
    {
        try
        {
            if (PatchEngine.TakenOverNote() is { } lost)
                MonitorLog.Warning(lost + ". !monitor health lists them.");
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not check what it can't time.", exception);
        }
    }

    private static void LogStatus()
    {
        try
        {
            MonitorLog.Info(SpikeReport.Status().ToString());
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not write its status to the log.", exception);
        }
    }

    /// <summary>True once Monitor has shut itself down after a failure. Commands and the API can still reach it, and nothing they start would ever finish.</summary>
    public static bool Closed { get; private set; }

    /// <summary>Shuts down after a failure, and refuses anything new from then on.</summary>
    public void GiveUp()
    {
        Closed = true;
        Capture.Closed = true;
        Attempt(Capture.Abandon);
        Shutdown();
    }

    public void Shutdown()
    {
        _running = false;
        FrameEngine.SecondElapsed = null;
        MonitorServerApi.Register(null);
        Sweep.OwnerOf = null;

        // The resolver stays. Other plugins can still reach code that names the API, and they should find Current is null, not fail to load.

        // Each on its own, so a failed step can't leave what comes after it in the game.
        Attempt(ServerApi.Instance.Stop);
        Attempt(() => MySession.OnSaved -= OnWorldSaved);
        Attempt(StopWatchingEntities);
        Attempt(IncidentService.Stop);
        Attempt(PatchAudit.RemoveAll);
        Attempt(DrillDown.RemoveAll);
        Attempt(PhysicsHooks.RestoreOptimizer);
        Attempt(PatchEngine.Remove);
    }

    // A player's character spawning starts the game's asteroid generation around it, which can hold the frame for
    // seconds. The character's owner is set from its builder before it's added, so it's known here.
    private static void OnEntityAdded(IMyEntity entity)
    {
        if (entity is not MyCharacter character || MySession.Static?.Players is not { } players)
            return;

        long identity = character.GetPlayerIdentityId();
        if (identity == 0L)
            return;

        bool known = players.TryGetPlayerId(identity, out MyPlayer.PlayerId player);
        Markers.NoteCharacterSpawn(players.IdentityIsNpc(identity), known ? player.SteamId : 0UL, known ? player.SerialId : 0);
    }

    // A world that never loaded has nothing to stop watching, and the game's Entities is null by now.
    private void StopWatchingEntities()
    {
        if (_entities != null)
        {
            _entities.OnEntityRemove -= LagReport.OnEntityRemoved;
            _entities.OnEntityAdd -= OnEntityAdded;
        }

        _entities = null;
    }

    private static void Attempt(Action step)
    {
        try
        {
            step();
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not undo part of what it added to the game.", exception);
        }
    }

    public static RuntimeInfoResponse ReadRuntimeInfo() => new()
    {
        MonitorVersion = typeof(Worker).Assembly.GetName().Version?.ToString() ?? "unknown",
        Runtime = Environment.Version.ToString(),
        ServerGc = GcProbe.IsServerGc,
        GcLatencyMode = GcProbe.LatencyMode.ToString(),
        Plugins = ServerInventory.Plugins,
        Mods = ServerInventory.Mods,
    };

    /// <summary>Packs one batch of history and hands the bytes over while they're still good. Safe from any thread.</summary>
    public static unsafe T PackHistory<T>(HistoryPullRequest pull, Func<byte[], int, T> take)
    {
        lock (s_historyGate)
        {
            fixed (byte* output = s_history)
            {
                int length = HistoryWriter.Write(pull.FromRollup, pull.FromCensus, pull.FromDetail, output, s_history.Length, pull.FromGroups, pull.FromMarkers);
                return take(s_history, length);
            }
        }
    }
}
