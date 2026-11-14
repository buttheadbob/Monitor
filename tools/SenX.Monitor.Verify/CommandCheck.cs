using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using PluginSdk.Commands;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar;
using VRage.Game.ModAPI;

namespace SenX.Monitor.Verify;

/// <summary>
/// Sends Monitor's chat commands through the real SDK dispatcher, with the game's real DLLs loaded,
/// and runs one measurement from start to finish. There is no game session, so it covers routing,
/// options, mode changes and report formatting, but not live game data.
/// </summary>
internal static unsafe class CommandCheck
{
    private const long GridId = 4242L;
    private const long Owner = 77L;

    private static readonly List<string> s_failures = new();
    private static List<string> s_replies = new();
    private static CommandDispatcher s_dispatcher = null!;

    private sealed class Responder : ICommandResponder
    {
        public void Send(in CommandReply reply, in CommandCaller caller)
        {
            s_replies.Add(reply.Text);
            Console.WriteLine("  > " + reply.Text);
        }
    }

    public static int Run()
    {
        var registry = new CommandRegistry();
        registry.RegisterModule(typeof(MonitorCommands), "senx.monitor");
        s_dispatcher = new CommandDispatcher(registry, (message, exception) => s_failures.Add(message + ": " + exception));

        CheckBeforeDetailStarts();
        StartDetail();
        CheckToggles();
        CheckMeasuringRun();
        CheckHistoryPull();

        Console.WriteLine();
        foreach (string failure in s_failures)
            Console.WriteLine("FAILED: " + failure);

        Console.WriteLine(s_failures.Count == 0 ? "All command checks passed." : s_failures.Count + " command checks failed.");
        return s_failures.Count == 0 ? 0 : 1;
    }

    private static void CheckBeforeDetailStarts()
    {
        Expect("!monitor", "grids");
        Expect("!monitor grids", "Detail starts 30 seconds");
        Expect("!monitor everything on", "Detail starts 30 seconds");
        Expect("!monitor sim", "waiting for the game");
        Expect("!monitor net", "waiting for the game");
        Expect("!monitor grids --nonsense", "Unknown option");
        Expect("!monitor rpcs --grid=1", "can't be limited");
        Expect("!monitor grids --secs=0", "--secs must be");
        Expect("!monitor grids --inspect=0", "only work with !monitor physics");
        Expect("!monitor physics --inspect=0", "Run !monitor physics first");
        Expect("!monitor physics --takeme=0", "Run !monitor physics first");
        Expect("!monitor physics --takeme=done", "Finished");
        Expect("!monitor frames --top=abc", "only takes --secs");
    }

    private static void StartDetail()
    {
        ThreadBlocks.Initialize(DetailLayout.Default(4));
        ThreadBlocks.BindMainThread();

        var sites = new List<int>();
        for (int site = Site.First; site <= Site.Last; site++)
            sites.Add(site);

        DetailController.Configure(sites);
        DetailController.SetMode(DetailMode.Grouped);
        Capture.Inline = true;
    }

    private static void CheckToggles()
    {
        Expect("!monitor everything on", "Everything is on");
        ExpectMode(DetailMode.Everything);
        Expect("!monitor everything off", "Everything is off");
        ExpectMode(DetailMode.Grouped);
        Expect("!monitor detail everything", "Detail is everything");
        ExpectMode(DetailMode.Everything);
        Expect("!monitor detail off", "Detail is off");
        ExpectMode(DetailMode.Off);
        Expect("!monitor detail grouped", "Detail is grouped");
        ExpectMode(DetailMode.Grouped);
        Expect("!monitor everything maybe", "not a valid");
        Expect("!monitor detail sideways", "not a valid");
        ExpectMode(DetailMode.Grouped);
    }

    private static void CheckMeasuringRun()
    {
        Expect("!monitor grids --secs=5 --top=3", "Measuring grids for 5 s");
        Expect("!monitor blocks", "already running");

        FrameRecord* record = Native.Allocate<FrameRecord>(1);
        record->BusyUnits = 100_000U;

        Step(record, CaptureState.Measuring);
        Check("every site is timed during a run", Gates.Read(Site.SessionBefore) == 0 && Gates.Read(Site.EntityBefore10) == 0);

        // The id is unknown to the game here, which is also how a grid removed during a run looks.
        EntitySlot* slot = Recorder.AddEntity(Site.EntityBefore, Phase.Before, GridId, Clock.Frequency / 100L, 0);
        slot->GridId = GridId;
        slot->OwnerId = Owner;

        for (int i = 0; i < 60; i++)
            Capture.OnFrame(record, true, false);

        Capture.ExpireForTests();
        Step(record, CaptureState.Idle);

        s_replies = new List<string>();
        Console.WriteLine("(run finished)");
        Capture.Deliver();

        Check("the report names the row", s_replies.Any(reply => reply.Contains("(removed grid 4242)")));
        Check("the report gives the frame", s_replies.Any(reply => reply.Contains("Measured")));
        Check("the report states its cost", s_replies.Any(reply => reply.Contains("Timing cost")));
        ExpectMode(DetailMode.Grouped);
        Check("grouped gates are back", Gates.Read(Site.EntityBefore10) == Gates.Closed || Gates.Read(Site.SessionBefore) == Gates.Closed);

        // Nothing is patched here, so this also proves a run still works when cluster timing has to stay off.
        Expect("!monitor physics --secs=5", "Measuring physics clusters for 5 s");
        Check("cluster timing stays closed when it isn't safe", !ClusterTurns.Open);

        Step(record, CaptureState.Measuring);
        Check("cluster timing stays closed during that run", !ClusterTurns.Open);
        Capture.ExpireForTests();
        Step(record, CaptureState.Idle);

        s_replies = new List<string>();
        Console.WriteLine("(run finished)");
        Capture.Deliver();

        Check("the report says why clusters weren't timed", s_replies.Any(reply => reply.Contains("Clusters weren't timed, because")));
    }

    private static void CheckHistoryPull()
    {
        Console.WriteLine("history.pull through the real plugin");

        FrameEngine.Start();
        HistoryWriter.StartedUtcTicks = DateTime.UtcNow.Ticks;
        long from = FrameEngine.Rollups.Head;

        for (int second = 0; second < 3; second++)
        {
            TreeCounters.Calls[Scope.Components] = 1L;
            FrameEngine.OnFrameEnd(Clock.Now());
            FrameEngine.CommitSecondNow();
        }

        string payload = QuasarBridge.AnswerAsync(MonitorProtocol.HistoryPullOperation, JsonConvert.SerializeObject(new HistoryPullRequest { FromRollup = from })).GetAwaiter().GetResult();

        // Quasar's channel reads the payload as JSON straight into a byte array.
        byte[] bytes = JsonConvert.DeserializeObject<byte[]>(payload)!;
        HistoryBatch? batch = HistoryReader.Read(bytes);

        Console.WriteLine($"  {bytes.Length} bytes for {batch?.Rollups.Count} rows");
        Check("the batch reads back", batch != null);
        Check("it holds the 3 seconds", batch?.Rollups.Count == 3 && batch.RollupFirst == from);

        Check("an unknown operation is refused", RefusedWith("no.such.operation", null)?.Contains("no.such.operation") == true);
        Check("a broken request is refused, and says nothing of why", RefusedWith(MonitorProtocol.HistoryPullOperation, "{ not json") != null);
    }

    private static string? RefusedWith(string operation, string? payload)
    {
        try
        {
            QuasarBridge.AnswerAsync(operation, payload).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            return exception.Message;
        }
    }

    private static void Step(FrameRecord* record, CaptureState until)
    {
        for (int i = 0; i < 32 && Capture.State != until; i++)
            Capture.OnFrame(record, true, false);

        Check("the run reached " + until, Capture.State == until);
    }

    private static void Expect(string line, string reply)
    {
        Console.WriteLine(line);
        s_replies = new List<string>();

        var caller = new CommandCaller(1UL, 1L, "Verify", MyPromoteLevel.Owner);
        bool handled = s_dispatcher.Handle(line, caller, new Responder());

        Check("handled: " + line, handled);
        Check(line + " replies '" + reply + "'", s_replies.Any(text => text.IndexOf(reply, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private static void ExpectMode(DetailMode mode) =>
        Check("mode is " + mode, DetailController.Mode == mode);

    private static void Check(string name, bool passed)
    {
        if (!passed)
            s_failures.Add(name);
    }
}
