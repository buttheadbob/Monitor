using System.Collections.Concurrent;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Services;

namespace PreviewHost;

/// <summary>
/// A pretend server for working on the pages. The numbers are invented, but they go through the real
/// worker code on a thread of their own: the real frame engine, sweep, census, runs and history pull.
/// What the pages show has taken the same road as on a live server, apart from game hooks and names.
/// </summary>
internal sealed unsafe class SimulatedServers : IMonitorServers, IDisposable
{
    private const string ServerId = "preview";
    private const int MostServers = 40;
    private const int Grids = 6;
    private const int BlocksPerGrid = 40;
    private const long FramesBetweenSaves = 18000L;

    private static readonly int[] s_sites = SiteGroups.All.SelectMany(group => group.Sites).Distinct().ToArray();

    private readonly ConcurrentQueue<Action> _onWorkerThread = new();
    private readonly byte[] _history = new byte[HistoryWriter.MaxBytes];
    private readonly object _historyGate = new();
    private readonly ConcurrentDictionary<long, IncidentResultResponse> _incidents = new();
    private readonly Thread _thread;
    private readonly Random _random = new(1234);
    private volatile bool _stopping;
    private volatile RunResultResponse? _result;
    private readonly IReadOnlyList<MonitorServer> _servers;
    private long _runId;
    private uint _lastIncident;

    private struct World : ICensusSource
    {
        public int Snapshot() => Grids + 10;

        public void Visit(int index)
        {
            if (index < Grids)
                Census.AddGrid(1L + (index % 3), 1000 + (index * 250), 500 + (index * 100), concealed: index % 4 == 0);
            else
                Census.AddFloatingObject();
        }

        public void Finish()
        {
            for (int player = 1; player <= 5; player++)
                Census.AddPlayer(100L + player, 1L + (player % 3), 20 + (player * 7), PeerOf(player), player == 5 ? 90000 : 0);

            Census.SetFactionCount(3);
        }
    }

    private static ulong PeerOf(int player) => 76561190000000000UL + (ulong)player;

    // --previewServers=N shows N servers, for pages that compare them. They all read the one simulated worker.
    // Read from the command line itself, because the Torch preview shares this file and has no configuration system.
    public SimulatedServers()
    {
        const string Option = "--previewServers=";
        string? asked = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith(Option, StringComparison.OrdinalIgnoreCase));
        int count = asked != null && int.TryParse(asked.Substring(Option.Length), out int wanted) ? Math.Clamp(wanted, 1, MostServers) : 1;
        _servers = count == 1
            ? [new MonitorServer(ServerId, "Preview server", ServerId)]
            : Enumerable.Range(1, count).Select(i => new MonitorServer(ServerId + "-" + i, "Preview server " + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), ServerId + "-" + i)).ToList();

        _thread = new Thread(Run) { IsBackground = true, Name = "Simulated server" };
        _thread.Start();
    }

    public IReadOnlyList<MonitorServer> Connected() => _servers;

    public Task<RuntimeInfoResponse> RuntimeInfoAsync(string serverId, CancellationToken cancellationToken) => Task.FromResult(new RuntimeInfoResponse
    {
        MonitorVersion = "preview",
        Runtime = Environment.Version.ToString(),
        Plugins = [new RuntimeItem { Id = "SenX.Monitor.Magnetar", Name = "SenX.Monitor.Magnetar preview" }],
        Mods = [new RuntimeItem { Id = "1", Name = "Preview mod" }],
    });

    public Task<byte[]> PullHistoryAsync(string serverId, HistoryPullRequest request, CancellationToken cancellationToken)
    {
        lock (_historyGate)
        {
            fixed (byte* output = _history)
            {
                int length = HistoryWriter.Write(request.FromRollup, request.FromCensus, request.FromDetail, output, _history.Length, request.FromGroups, request.FromMarkers);
                return Task.FromResult(_history.AsSpan(0, length).ToArray());
            }
        }
    }

    public Task<IdentityResponse> LookUpAsync(string serverId, IdentityRequest request, CancellationToken cancellationToken)
    {
        var response = new IdentityResponse();

        foreach (IdentityQuery query in request.Queries)
            response.Answers.Add(new IdentityAnswer { Kind = query.Kind, Key = query.Key, Name = query.Kind == HistoryDimension.ModParts ? ModNames.Part(NameOf(HistoryDimension.Mods, query.Representative), "Component" + query.Key) : NameOf(query.Kind, query.Key) });

        return Task.FromResult(response);
    }

    public Task<RunStartResponse> StartRunAsync(string serverId, RunStartRequest request, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<RunStartResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        _onWorkerThread.Enqueue(() =>
        {
            long id = _runId + 1L;
            CaptureStart started = Capture.TryStart(request.Seconds, null, result => _result = Summarize(result));

            if (started == CaptureStart.Started)
            {
                _runId = id;
                _result = null;
            }

            answer.SetResult(new RunStartResponse
            {
                Started = started == CaptureStart.Started,
                RunId = id,
                Message = started == CaptureStart.Started ? null : "The run didn't start: " + started + ".",
            });
        });

        return answer.Task;
    }

    public Task<RunResultResponse> RunResultAsync(string serverId, RunResultRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(_result ?? new RunResultResponse { State = RunState.Running });

    public Task<IncidentResultResponse> IncidentResultAsync(string serverId, IncidentResultRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(_incidents.TryGetValue(request.Id, out IncidentResultResponse? report) ? report : new IncidentResultResponse { State = RunState.Running });

    public Task<TriggerSetResponse> SetTriggersAsync(string serverId, TriggerSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult(new TriggerSetResponse { Applied = true });

    public Task<DrillRootsResponse> DrillRootsAsync(string serverId, DrillRootsRequest request, CancellationToken cancellationToken) => Task.FromResult(new DrillRootsResponse
    {
        Roots = [new DrillRoot { Id = "preview", Name = "PreviewPlugin.Update", Owner = "Plugin: PreviewPlugin" }],
    });

    public Task<DrillStartResponse> StartDrillAsync(string serverId, DrillStartRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new DrillStartResponse { Started = true });

    public Task<DrillResultResponse> DrillResultAsync(string serverId, DrillResultRequest request, CancellationToken cancellationToken) => Task.FromResult(new DrillResultResponse
    {
        State = RunState.Finished,
        Method = "PreviewPlugin.Update",
        Owner = "Plugin: PreviewPlugin",
        Seconds = 10,
        CallsPerFrame = 1d,
        MainMs = 1.2d,
        SelfMs = 0.2d,
        Rows =
        [
            new DrillRow { Id = "preview", Name = "PreviewPlugin.Scan", Owner = "Plugin: PreviewPlugin", CallsPerFrame = 40d, MainMs = 0.8d, PeakMs = 0.1d },
            new DrillRow { Name = "Action.Invoke", Owner = "Game", CallsPerFrame = 1d, MainMs = 0.2d, PeakMs = 0.3d },
        ],
    });

    public Task<PatchListResponse> ListPatchesAsync(string serverId, PatchListRequest request, CancellationToken cancellationToken) => Task.FromResult(new PatchListResponse
    {
        Total = 1,
        Methods =
        [
            new PatchedMethodRow
            {
                Method = "Sandbox.Game.Entities.MyCubeGrid.UpdateBeforeSimulation()",
                Patches = [new PatchEntryRow { Kind = PatchKinds.Prefix, Owner = "preview", Plugin = "PreviewPlugin", Patch = "Patches.Prefix" }],
            },
        ],
    });

    public Task<AuditStartResponse> StartAuditAsync(string serverId, AuditStartRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new AuditStartResponse { Message = "The preview server doesn't audit." });

    public Task<AuditResultResponse> AuditResultAsync(string serverId, AuditResultRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new AuditResultResponse());

    public void Dispose() => _stopping = true;

    private void OnLag(LagReason reason, double value)
    {
        uint id = ++_lastIncident;
        Markers.AddLag(id);

        CaptureStart started = Capture.TryStart(5, null, result => _incidents[id] = new IncidentResultResponse
        {
            State = RunState.Finished,
            Reason = reason.ToString(),
            Value = value,
            Run = Summarize(result),
        });

        if (started != CaptureStart.Started)
            _incidents[id] = new IncidentResultResponse { State = RunState.Finished, Reason = reason.ToString(), Value = value, NoRun = "the run didn't start" };
    }

    private static RunResultResponse Summarize(CaptureResult? result)
    {
        if (result == null)
            return new RunResultResponse { State = RunState.Failed };

        var response = new RunResultResponse
        {
            State = RunState.Finished,
            Seconds = result.ElapsedUnits / (double)Clock.UnitsPerSecond,
            Frames = result.SimulatedFrames,
            SimSpeed = result.SimSpeed,
            AverageFrameMs = result.AverageFrameUnits / 10000d,
            WorstFrameMs = result.WorstFrameUnits / 10000d,
            TimingMsPerFrame = result.TimingNanosPerFrame / 1000000d,
            Coverage = result.Coverage,
        };

        foreach (RollupRow row in result.Grids.Top(10))
        {
            response.Rows.Add(new RunRow
            {
                List = RunLists.Grids,
                Name = NameOf(HistoryDimension.Grids, row.Key),
                Key = row.Key,
                MainMsPerFrame = result.PerFrame(row.MainUnits) / 10000d,
                WorkerMsPerFrame = result.PerFrame(row.WorkerUnits) / 10000d,
                PeakMs = row.MaxUnits / 10000d,
            });
        }

        response.MainWaitMsPerFrame = result.PerFrame(result.MainWaitUnits) / 10000d;
        response.ParallelWorkerMsPerFrame = result.PerFrame(result.ParallelWorkerUnits) / 10000d;

        // The simulated worker only times grids, so these lists are made up, to show every panel and thread label.
        Sample(response, RunLists.BlockTypes, ("MyRefinery", 0.42, 0d), ("MyAssembler", 0.31, 0d), ("MyThrust", 0.08, 0.21), ("MyLargeTurret", 0.05, 0d));
        Sample(response, RunLists.Players, ("Player 1", 0.61, 0.12), ("Player 2", 0.34, 0.05), ("Player 3", 0.12, 0d));
        Sample(response, RunLists.Scripts, ("Miner control (Programmable block)", 0.09, 0d), ("Door timer (Programmable block)", 0.01, 0d));
        Sample(
            response,
            RunLists.Session,
            ("Sandbox.Game.SessionComponents.MySessionComponentEconomy", 0.07, 0d),
            ("Sandbox.Game.World.MySector", 0.06, 0d),
            ("Sandbox.Game.Entities.Planet.MyPlanetEnvironmentSessionComponent", 0.04, 0d),
            ("Sandbox.Game.GameSystems.MyEnvironmentBotSpawningSystem", 0.03, 0d),
            ("Sandbox.Game.Entities.MyGamePruningStructure", 0.02, 0d));
        Sample(response, RunLists.Logic, ("Sandbox.Game.Entities.Blocks.MyFunctionalBlockLogic", 0.05, 0d), ("VRage.Game.Components.MyNullGameLogicComponent", 0.01, 0d));
        Sample(response, RunLists.Mods, ("Game", 1.63, 0.41), ("Plugin: Preview Tools", 0.12, 0d), ("Mod: Preview 1", 0.04, 0.02));
        Sample(response, RunLists.Entities, ("MyCubeGrid", 1.22, 0.38), ("MyVoxelMap", 0.06, 0.11), ("MyPlanet", 0.03, 0d));

        return response;
    }

    private static void Sample(RunResultResponse response, string list, params (string Name, double MainMs, double WorkerMs)[] rows)
    {
        foreach ((string name, double main, double worker) in rows)
            response.Rows.Add(new RunRow { List = list, Name = name, MainMsPerFrame = main, WorkerMsPerFrame = worker, PeakMs = (main + worker) * 4d, CallsPerFrame = 1d });
    }

    private static string NameOf(HistoryDimension kind, long key) => kind switch
    {
        HistoryDimension.Grids => "Ship " + key,
        HistoryDimension.Owners => "Player " + key,
        HistoryDimension.Factions => "F" + key,
        HistoryDimension.Mods => "Mod: Preview " + key,
        HistoryDimension.Rpcs or HistoryDimension.RpcsSent => "MyCubeGrid.OnPreviewRpc" + (key % 4),
        HistoryDimension.BlockTypes => "BlockType" + (key % 5),
        HistoryDimension.Definitions => "Definition" + (key % 7),
        HistoryDimension.Scripts => "Script " + key,
        HistoryDimension.Session => "SessionComponent" + (key % 5),
        HistoryDimension.Logic => "GameLogic" + (key % 5),
        HistoryDimension.Callbacks => "Callback" + (key % 5),
        _ => kind + " " + key,
    };

    private void Run()
    {
        MonitorCore.Initialize();
        ThreadBlocks.Initialize(DetailLayout.Default(2));
        ThreadBlocks.BindMainThread();
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.Ticks;
        FrameEngine.Start();
        Calibration.Run();
        DetailController.Configure(s_sites);
        DetailController.SetMode(DetailMode.Grouped);
        LagWatch.Limits = new LagLimits { CooldownSeconds = 120 };
        LagWatch.Tripped = OnLag;
        Sweep.OwnerOf = key => 1L + (key % 3L);

        var world = default(World);
        long frame = 0L;

        while (!_stopping)
        {
            while (_onWorkerThread.TryDequeue(out Action? action))
                action();

            Frame(frame++);
            Census.Step(ref world, FrameEngine.Paused);
            Capture.Deliver();

            if (frame % FramesBetweenSaves == 0L)
                Markers.NoteSave(succeeded: true);

            Markers.Flush();

            // A lag spell every couple of minutes, so the charts have something to show.
            Thread.Sleep(frame % 7200L < 600L ? 30 : 16);
        }
    }

    private void Frame(long frame)
    {
        double wave = 1d + (0.4d * Math.Sin(frame / 600d));
        double lag = frame % 7200L < 600L ? 3d : 1d;
        long frameStart = Clock.Now();

        TreeCounters.Calls[Scope.Components] = 1L;

        long physics = Ms(2.5d * wave * lag);
        long before = Ms(1.5d * wave);
        long after = Ms(1d * wave);
        long network = Ms(0.6d);
        long send = Ms(0.8d * wave);
        long session = Ms(0.5d);

        TreeCounters.Add(Scope.Physics, physics);
        TreeCounters.Add(Scope.EntitiesBefore, before);
        TreeCounters.Add(Scope.EntitiesAfter, after);
        TreeCounters.Add(Scope.Components, physics + before + after + session);
        TreeCounters.Add(Scope.Network, network);
        TreeCounters.Add(Scope.ReplicationSend, send);

        RecordDetail();

        FrameEngine.OnFrameEnd(frameStart - (physics + before + after + network + send + session));
    }

    private void RecordDetail()
    {
        byte gate = Gates.Read(Site.EntityBefore);
        if (gate != Gates.Closed)
        {
            for (long grid = 1L; grid <= Grids; grid++)
            {
                for (long block = 0L; block < BlocksPerGrid; block += 4L)
                {
                    long id = (grid * 1000L) + block;
                    EntitySlot* created = Recorder.AddEntity(Site.EntityBefore, Phase.Before, id, Ms(0.004d * grid * (1d + _random.NextDouble())), gate);

                    if (created != null)
                    {
                        created->GridId = grid * 1000000L;
                        created->OwnerId = 100L + (grid % 3L);
                        created->DefinitionKey = EntityKeys.Pack((int)(block % 7L) + 1, 0x10000L * (1L + (block % 5L)));
                    }
                }
            }
        }

        gate = Gates.Read(Site.SessionBefore);
        if (gate != Gates.Closed)
        {
            for (long component = 1L; component <= 5L; component++)
                Recorder.AddKey(Site.SessionBefore, SiteKeys.Pack(component, Site.SessionBefore), Ms(0.05d * component), gate);
        }

        for (int player = 1; player <= 5; player++)
        {
            NetTraffic.Sent(PeerOf(player), 400 * player, NetKind.StateSync, accepted: true);
            NetTraffic.Sent(PeerOf(player), 120, NetKind.Events, accepted: true);
            NetTraffic.Sent(PeerOf(player), _random.Next(60) == 0 ? 3000 : 40, NetKind.Replication, accepted: true);
            NetTraffic.Received(PeerOf(player), 90U);
            NetTraffic.NoteRpc(PeerOf(player));
        }

        for (long rpc = 1L; rpc <= 4L; rpc++)
            NetTraffic.SentRpc(rpc, 2000L / rpc);

        gate = Gates.Read(Site.Rpc);
        if (gate != Gates.Closed)
        {
            for (long rpc = 1L; rpc <= 4L; rpc++)
                Recorder.AddKey(Site.Rpc, SiteKeys.Pack(rpc, Site.Rpc), Ms(0.01d * rpc), gate);
        }
    }

    private static long Ms(double milliseconds) => (long)(milliseconds * Clock.Frequency / 1000d);
}
