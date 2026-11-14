using System.Runtime.CompilerServices;
using PluginSdk.Commands;
using Sandbox;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar;
using SenX.Monitor.Magnetar.Patching;
using SenX.Monitor.Magnetar.Reporting;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using ReplyContext = PluginSdk.Commands.CommandContext;
using TorchContext = Torch.Commands.CommandContext;

namespace SenX.Monitor.Torch;

/// <summary>
/// Monitor's chat commands, the same ones as on Magnetar. Options are read from the raw arguments,
/// because Torch can't fill a parameter array. A player's command arrives on the game thread, but a
/// console's or a bot's arrives on whatever thread sent it, so every command makes sure of the game thread.
///
/// A command that takes a while says it's started, then sends one finished report when it's done. A caller
/// that took the replies Torch returned has stopped listening by then, so a command from another thread
/// holds that thread until the report has gone. On the game thread it can't wait, and the report arrives by
/// itself for anyone subscribed to replies, as Torch's console, Orion and SEDiscordBridge all are.
/// </summary>
[Category("monitor")]
public sealed class MonitorCommands : CommandModule
{
    private const string Options = "Options: --secs=N --top=N --grid=ID --this --player=NAME --faction=TAG --noblocks --block=TYPE";
    private const string GridOptions = Options + " --details --gps";
    private const string ListOptions = "Options: --secs=N --top=N";
    private const string PhysicsOptions = "Options: --secs=N --top=N. After a result: --inspect=N --takeme=N --takeme=done";
    private const string OnOrOff = "Use on or off.";
    private const string Root = "monitor ";

    // Long enough to wait out real lag, and it only holds up whoever sent the command.
    private const int GameThreadTimeoutMs = 10000;

    // As long as the longest run, because that's the wait this caller asked for.
    private const int MostHeldMs = (CaptureArgs.MaxSeconds + 30) * 1000;

    [Command("howto", "How to use Monitor, and what its numbers mean")]
    [Permission(MyPromoteLevel.None)]
    public void HowTo() => Reply(_ => AdminActions.HowToText);

    [Command("mine", "What your own grids cost right now, from the latest background total")]
    [Permission(MyPromoteLevel.None)]
    public void Mine() => Reply(PlayerCommands.Mine);

    [Command("test", "Times your grid for a few seconds. Give a name, or sit at its controls")]
    [Permission(MyPromoteLevel.None)]
    public void Test() => Reply(context => PlayerCommands.Test(context, Words()));

    [Command("status", "Sim speed, frame time, GC and the top of the tree")]
    [Permission(MyPromoteLevel.Admin)]
    public void Status() => Reply(_ => SpikeReport.Status());

    [Command("sites", "Call volume and sampling for each detail site")]
    [Permission(MyPromoteLevel.Admin)]
    public void Sites() => Reply(_ => SpikeReport.Sites());

    [Command("health", "What Monitor patched, and anything that drifted or was skipped")]
    [Permission(MyPromoteLevel.Admin)]
    public void Health() => Reply(_ => SpikeReport.Patches());

    [Command("patches", "Which other plugins patch the game, and how much")]
    [Permission(MyPromoteLevel.Admin)]
    public void Patches() => Reply(_ => PatchList.Summary());

    [Command("sim", "Best, worst and average sim speed over the last --secs=N seconds")]
    [Permission(MyPromoteLevel.Admin)]
    public void Sim() => Reply(_ => SpikeReport.Sim(Words()));

    [Command("frames", "Where frame time went over the last --secs=N seconds")]
    [Permission(MyPromoteLevel.Admin)]
    public void Frames() => Reply(_ => SpikeReport.Frames(Words()));

    [Command("net", "What's crossing the network and who for, over the last --secs=N seconds")]
    [Permission(MyPromoteLevel.Admin)]
    public void Net() => Reply(_ => SpikeReport.Net(Words()));

    [Command("diagnose", "Times everything, then reports the frame and the top of every list", "Options: --secs=N --top=N --details")]
    [Permission(MyPromoteLevel.Admin)]
    public void Diagnose() => Run(LagView.Diagnose, "everything");

    [Command("physics", "Times everything, then lists physics stages and the costliest clusters", PhysicsOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public void Physics() => Run(LagView.Physics, "physics clusters");

    [Command("grids", "Times everything, then lists the costliest grids", GridOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public void Grids() => Run(LagView.Grids, "grids");

    [Command("blocktypes", "Times everything, then lists the costliest block types", Options)]
    [Permission(MyPromoteLevel.Admin)]
    public void BlockTypes() => Run(LagView.BlockTypes, "block types");

    [Command("blocks", "Times everything, then lists the costliest block definitions", Options)]
    [Permission(MyPromoteLevel.Admin)]
    public void Blocks() => Run(LagView.Blocks, "block definitions");

    [Command("players", "Times everything, then lists the costliest players", Options)]
    [Permission(MyPromoteLevel.Admin)]
    public void Players() => Run(LagView.Players, "players");

    [Command("factions", "Times everything, then lists the costliest factions", Options)]
    [Permission(MyPromoteLevel.Admin)]
    public void Factions() => Run(LagView.Factions, "factions");

    [Command("scripts", "Times everything, then lists the costliest programmable blocks", Options)]
    [Permission(MyPromoteLevel.Admin)]
    public void Scripts() => Run(LagView.Scripts, "scripts");

    [Command("session", "Times everything, then lists the costliest session components", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public void Session() => Run(LagView.Session, "session components");

    [Command("entities", "Times everything, then lists the costliest entity types", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public void Entities() => Run(LagView.Entities, "entity types");

    [Command("logic", "Times everything, then lists the costliest game logic components", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public void Logic() => Run(LagView.Logic, "game logic");

    [Command("callbacks", "Times everything, then lists the costliest grid and deferred callbacks", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public void Callbacks() => Run(LagView.Callbacks, "callbacks");

    [Command("rpcs", "Times everything, then lists the costliest RPCs from clients and the biggest sent", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public void Rpcs() => Run(LagView.Rpcs, "RPCs");

    [Command("mods", "Times everything, then lists the costliest mods and plugins", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public void Mods() => Run(LagView.Mods, "mods and plugins");

    [Command("top", "Latest background totals: grids, blocktypes, blocks, players, factions, entities, session, logic, callbacks, rpcs, rpcssent, scripts or mods")]
    [Permission(MyPromoteLevel.Admin)]
    public void Top(string list) => Reply(_ => AdminActions.Top(list));

    [Command("cleangps", "Removes the GPS markers Monitor sent you")]
    [Permission(MyPromoteLevel.Admin)]
    public void CleanGps() => Reply(context => AdminActions.CleanGps(context.Caller.IdentityId));

    [Command("everything", "Hold every call timed full time: on or off")]
    [Permission(MyPromoteLevel.Admin)]
    public void Everything() => OnOff(AdminActions.Everything);

    [Command("drill", "Looks inside a method by timing what it calls. Give part of a name, or a row number from the last result", "Options: --secs=N, up to 120")]
    [Permission(MyPromoteLevel.Admin)]
    public void Drill() => ReplyThenHold(context => DrillCommand.Run(context, Words()), () => DrillDown.Running);

    [Command("trigger", "Run automatically when lag is noticed: on or off")]
    [Permission(MyPromoteLevel.Admin)]
    public void Trigger() => OnOff(AdminActions.Trigger);

    [Command("detail", "Set background detail: grouped, everything or off")]
    [Permission(MyPromoteLevel.Admin)]
    public void Detail(string mode) => Reply(_ => Enum.TryParse(mode, ignoreCase: true, out DetailMode parsed)
        ? AdminActions.Detail(parsed)
        : "Use grouped, everything or off.");

    // Each command's own name, because that's what a Torch fault can leave at the front of its words.
    private string[] Words([CallerMemberName] string command = "") => CaptureArgs.WordsAfter(Root + command.ToLowerInvariant(), Context.Args);

    private void Run(LagView view, string label, [CallerMemberName] string command = "") =>
        ReplyThenHold(context => LagRuns.Start(context, view, label, Words(command)), () => Capture.Active);

    private void OnOff(Func<bool, string> action, [CallerMemberName] string command = "") => Reply(_ => (Words(command) is { Length: 1 } words ? words[0].ToLowerInvariant() : string.Empty) switch
    {
        "on" or "true" or "yes" => action(true),
        "off" or "false" or "no" => action(false),
        _ => OnOrOff,
    });

    // Each page goes out as its own reply, laid out for whoever asked.
    private void Reply(Func<ReplyContext, ChatReport> report)
    {
        ReplyContext context = Who();

        void Answer() => ChatReply.Send(context, report(context));

        if (Thread.CurrentThread == MySandboxGame.Static.UpdateThread)
            Answer();
        else
            Context.Torch.InvokeBlocking(Answer, GameThreadTimeoutMs);
    }

    private void ReplyThenHold(Func<ReplyContext, ChatReport> report, Func<bool> going)
    {
        if (Thread.CurrentThread == MySandboxGame.Static.UpdateThread)
        {
            Reply(report);
            return;
        }

        var answered = new ManualResetEventSlim();
        ReplyContext context = Who(answered);
        bool started = false;

        void Answer()
        {
            bool before = going();
            ChatReply.Send(context, report(context));
            started = !before && going();

            // That was the first reply, and what's being waited for is the next one.
            answered.Reset();
        }

        Context.Torch.InvokeBlocking(Answer, GameThreadTimeoutMs);

        // A report's pages all go out in one go on the game thread, so they've gone once this has run.
        if (started && answered.Wait(MostHeldMs))
            Context.Torch.InvokeBlocking(() => { }, GameThreadTimeoutMs);
    }

    // Kept for later, because a run answers when it finishes, long after this command returned.
    private ReplyContext Who(ManualResetEventSlim? answered = null)
    {
        TorchContext torch = Context;
        IMyPlayer? player = torch.Player;

        var caller = new CommandCaller(player == null, player?.SteamUserId ?? 0UL, player?.IdentityId ?? 0L, player?.DisplayName ?? "Console");

        return new ReplyContext(caller, message =>
        {
            torch.Respond(message);
            answered?.Set();
        });
    }
}
