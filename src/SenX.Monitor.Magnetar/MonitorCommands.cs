using System.Collections.Generic;
using PluginSdk.Commands;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Patching;
using SenX.Monitor.Magnetar.Reporting;
using VRage.Game.ModAPI;

namespace SenX.Monitor.Magnetar;

[CommandRoot("monitor", "Monitor", "Server performance monitor")]
public sealed class MonitorCommands : CommandModule
{
    private const string Options = "Options: --secs=N --top=N --grid=ID --this --player=NAME --faction=TAG --noblocks --block=TYPE";
    private const string GridOptions = Options + " --details --gps";
    private const string ListOptions = "Options: --secs=N --top=N";
    private const string PhysicsOptions = "Options: --secs=N --top=N. After a result: --inspect=N --takeme=N --takeme=done";

    [Command("howto", "How to use Monitor, and what its numbers mean")]
    [Permission(MyPromoteLevel.None)]
    public string HowTo() => AdminActions.HowToText;

    [Command("mine", "What your own grids cost right now, from the latest background total")]
    [Permission(MyPromoteLevel.None)]
    public List<string> Mine() => Reply(PlayerCommands.Mine(Context));

    [Command("test", "Times your grid for a few seconds. Give a name, or sit at its controls")]
    [Permission(MyPromoteLevel.None)]
    public List<string> Test(params string[] grid) => Reply(PlayerCommands.Test(Context, grid));

    [Command("status", "Sim speed, frame time, GC and the top of the tree")]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Status() => Reply(SpikeReport.Status());

    [Command("sites", "Call volume and sampling for each detail site")]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Sites() => Reply(SpikeReport.Sites());

    [Command("health", "What Monitor patched, and anything that drifted or was skipped")]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Health() => Reply(SpikeReport.Patches());

    [Command("patches", "Which other plugins patch the game, and how much")]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Patches() => Reply(PatchList.Summary());

    [Command("sim", "Best, worst and average sim speed over the last --secs=N seconds")]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Sim(params string[] options) => Reply(SpikeReport.Sim(options));

    [Command("frames", "Where frame time went over the last --secs=N seconds")]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Frames(params string[] options) => Reply(SpikeReport.Frames(options));

    [Command("net", "What's crossing the network and who for, over the last --secs=N seconds")]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Net(params string[] options) => Reply(SpikeReport.Net(options));

    [Command("diagnose", "Times everything, then reports the frame and the top of every list", "Options: --secs=N --top=N --details")]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Diagnose(params string[] options) => Reply(LagRuns.Start(Context, LagView.Diagnose, "everything", options));

    [Command("physics", "Times everything, then lists physics stages and the costliest clusters", PhysicsOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Physics(params string[] options) => Reply(LagRuns.Start(Context, LagView.Physics, "physics clusters", options));

    [Command("grids", "Times everything, then lists the costliest grids", GridOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Grids(params string[] options) => Reply(LagRuns.Start(Context, LagView.Grids, "grids", options));

    [Command("blocktypes", "Times everything, then lists the costliest block types", Options)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> BlockTypes(params string[] options) => Reply(LagRuns.Start(Context, LagView.BlockTypes, "block types", options));

    [Command("blocks", "Times everything, then lists the costliest block definitions", Options)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Blocks(params string[] options) => Reply(LagRuns.Start(Context, LagView.Blocks, "block definitions", options));

    [Command("players", "Times everything, then lists the costliest players", Options)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Players(params string[] options) => Reply(LagRuns.Start(Context, LagView.Players, "players", options));

    [Command("factions", "Times everything, then lists the costliest factions", Options)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Factions(params string[] options) => Reply(LagRuns.Start(Context, LagView.Factions, "factions", options));

    [Command("scripts", "Times everything, then lists the costliest programmable blocks", Options)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Scripts(params string[] options) => Reply(LagRuns.Start(Context, LagView.Scripts, "scripts", options));

    [Command("session", "Times everything, then lists the costliest session components", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Session(params string[] options) => Reply(LagRuns.Start(Context, LagView.Session, "session components", options));

    [Command("entities", "Times everything, then lists the costliest entity types", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Entities(params string[] options) => Reply(LagRuns.Start(Context, LagView.Entities, "entity types", options));

    [Command("logic", "Times everything, then lists the costliest game logic components", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Logic(params string[] options) => Reply(LagRuns.Start(Context, LagView.Logic, "game logic", options));

    [Command("callbacks", "Times everything, then lists the costliest grid and deferred callbacks", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Callbacks(params string[] options) => Reply(LagRuns.Start(Context, LagView.Callbacks, "callbacks", options));

    [Command("rpcs", "Times everything, then lists the costliest RPCs from clients and the biggest sent", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Rpcs(params string[] options) => Reply(LagRuns.Start(Context, LagView.Rpcs, "RPCs", options));

    [Command("mods", "Times everything, then lists the costliest mods and plugins", ListOptions)]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Mods(params string[] options) => Reply(LagRuns.Start(Context, LagView.Mods, "mods and plugins", options));

    [Command("top", "Latest background totals: grids, blocktypes, blocks, players, factions, entities, session, logic, callbacks, rpcs, rpcssent, scripts or mods")]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Top(string list) => Reply(AdminActions.Top(list));

    [Command("cleangps", "Removes the GPS markers Monitor sent you")]
    [Permission(MyPromoteLevel.Admin)]
    public string CleanGps() => AdminActions.CleanGps(Context.Caller.IdentityId);

    [Command("everything", "Hold every call timed full time: on or off")]
    [Permission(MyPromoteLevel.Admin)]
    public string Everything(bool on) => AdminActions.Everything(on);

    [Command("drill", "Looks inside a method by timing what it calls. Give part of a name, or a row number from the last result", "Options: --secs=N, up to 120")]
    [Permission(MyPromoteLevel.Admin)]
    public List<string> Drill(params string[] words) => Reply(DrillCommand.Run(Context, words));

    [Command("trigger", "Run automatically when lag is noticed: on or off")]
    [Permission(MyPromoteLevel.Admin)]
    public string Trigger(bool on) => AdminActions.Trigger(on);

    [Command("detail", "Set background detail: grouped, everything or off")]
    [Permission(MyPromoteLevel.Admin)]
    public string Detail(DetailMode mode) => AdminActions.Detail(mode);

    // Each page goes out as its own reply, laid out for whoever asked.
    private List<string> Reply(ChatReport report) => ChatReply.For(Context, report);
}
