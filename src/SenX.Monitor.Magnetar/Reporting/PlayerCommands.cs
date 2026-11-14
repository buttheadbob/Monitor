using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PluginSdk.Commands;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using VRage.Game.Entity;
using static SenX.Monitor.Contracts.ChatFigure;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>
/// Lets players see what their own grids cost. Everyone can run these, so each one checks for itself
/// that a player in the game is asking, and only ever shows that player's own grids. Other players
/// are never named. A rank is only a number. On the game thread, on demand only.
/// </summary>
internal static unsafe class PlayerCommands
{
    private const string NotInGame = "This command is for players in the game.";
    private const string TurnedOff = "Admins have turned this command off.";
    private const long NameSearchGapSeconds = 5L;
    private const int MaxGridsShown = 10;
    private const int RowsPerList = 5;
    private const int MineEverySeconds = 5;
    private const int ServerGapSeconds = 60;

    private static readonly Dictionary<long, long> s_nextMine = new();
    private static readonly Dictionary<long, long> s_nextTest = new();

    private static TriggerSettings s_settings = new();
    private static long s_nextServerTest;

    private static string Ms(double units) => (units / 10000d).ToString("0.00", CultureInfo.InvariantCulture);

    public static void Configure(TriggerSettings settings) => s_settings = settings;

    /// <summary>
    /// The player asking, if a player is. The permission level lets anyone in, and a console or a bot
    /// isn't a player with grids. This is also what decides how a reply is laid out.
    /// </summary>
    internal static MyPlayer? PlayerIn(CommandContext context)
    {
        CommandCaller caller = context.Caller;

        if (caller.IsConsole || caller.SteamId == 0UL || caller.IdentityId == 0L || MySession.Static == null)
            return null;

        return MySession.Static.Players.TryGetPlayerBySteamId(caller.SteamId, out MyPlayer player)
               && player.Identity?.IdentityId == caller.IdentityId
            ? player
            : null;
    }

    private static bool Waiting(Dictionary<long, long> next, long who, out int seconds)
    {
        long now = Clock.Now();
        seconds = 0;

        if (!next.TryGetValue(who, out long allowed) || now >= allowed)
            return false;

        seconds = (int)((allowed - now) / Clock.Frequency) + 1;
        return true;
    }

    /// <summary>The player's grids from the latest background total, which costs nothing because it's already there.</summary>
    public static ChatReport Mine(CommandContext context)
    {
        if (!(PlayerIn(context) is { } player))
            return NotInGame;

        if (!s_settings.PlayerMine)
            return TurnedOff;

        long me = player.Identity.IdentityId;

        if (Waiting(s_nextMine, me, out int wait))
            return "Try again in " + wait + " s.";

        CaptureResult? totals = Sweep.LatestTotals;
        if (totals == null)
            return "There's no total to read right now. They arrive every 30 seconds, so try again shortly.";

        s_nextMine[me] = Clock.Now() + (MineEverySeconds * Clock.Frequency);

        var mine = new List<(MyCubeGrid Grid, RollupRow Row)>();
        var everyGrid = new List<long>(totals.Grids.Count);

        totals.Grids.Visit(row =>
        {
            everyGrid.Add(row.TotalUnits);

            if (MyEntities.TryGetEntityById(row.Key, out MyEntity entity) && entity is MyCubeGrid grid && grid.BigOwners.Contains(me))
                mine.Add((grid, row));
        });

        if (mine.Count == 0)
            return "None of your grids were in the latest total. A grid that does nothing costs nothing.";

        everyGrid.Sort();

        var report = new ChatReport()
            .Title("Your grids")
            .Note("Estimates from the latest background total. !monitor test measures one properly.");
        double frame = FrameUnits();
        ChatTable table = report.Table("Grid", "Main", "Frame", "Work", "Rank", ThreadTags.Header);

        foreach ((MyCubeGrid grid, RollupRow row) in mine.OrderByDescending(row => row.Row.TotalUnits).Take(MaxGridsShown))
        {
            double gridMain = totals.PerFrame(row.MainUnits);
            double gridWorker = totals.PerFrame(row.WorkerUnits);

            table.Row(grid.DisplayName, Ms(gridMain), ShareOf(gridMain, frame), Ms(gridWorker), RankOf(everyGrid, row.TotalUnits), ThreadTags.Tag(gridMain, gridWorker));
        }

        if (mine.Count > MaxGridsShown)
            report.Text("And " + (mine.Count - MaxGridsShown) + " more.");

        double main = mine.Sum(row => totals.PerFrame(row.Row.MainUnits));
        double workers = mine.Sum(row => totals.PerFrame(row.Row.WorkerUnits));

        return report.Facts(Fig("All of them", Ms(main), "ms/f on the main thread"), Fig("Share of frame", ShareOf(main, frame)), Fig("Workers", Ms(workers), "ms/f"));
    }

    private static string ShareOf(double main, double frame) =>
        (frame > 0d ? (main / frame * 100d).ToString("0.0", CultureInfo.InvariantCulture) : "?") + "%";

    // How many grids cost more, which says where a grid stands without naming any of them.
    private static string RankOf(List<long> sorted, long total)
    {
        int index = sorted.BinarySearch(total);
        if (index < 0)
            index = ~index;

        while (index + 1 < sorted.Count && sorted[index + 1] == total)
            index++;

        return (sorted.Count - index).ToString(CultureInfo.InvariantCulture) + " of " + sorted.Count.ToString(CultureInfo.InvariantCulture);
    }

    private static double FrameUnits()
    {
        if (!FrameEngine.TryReadLatest(out RollupRecord row) || row.Frames == 0U)
            return 0d;

        return row.ScopeUnits[Scope.Frame] / (double)row.Frames;
    }

    /// <summary>Times one of the player's own grids. A run costs the whole server, so this has limits a free read doesn't.</summary>
    public static ChatReport Test(CommandContext context, string[] words)
    {
        if (!(PlayerIn(context) is { } player))
            return NotInGame;

        if (!s_settings.PlayerTest)
            return TurnedOff;

        long me = player.Identity.IdentityId;

        // First, because finding a grid by name walks the whole world on the game thread.
        if (Waiting(s_nextTest, me, out int wait))
            return "You can test again in " + wait + " s.";

        long now = Clock.Now();

        // Whatever comes of it, a name means a walk of the whole world, so it can't be asked for again straight away.
        if (words.Length > 0)
            s_nextTest[me] = now + (NameSearchGapSeconds * Clock.Frequency);

        MyCubeGrid? grid = words.Length == 0 ? LagRuns.ControlledGrid(context.Caller.SteamId) : OwnGridNamed(string.Join(" ", words), me);

        if (grid == null)
            return words.Length == 0 ? "Sit at the controls of your grid, or name it: !monitor test My Ship" : "You don't own a grid with that name.";

        if (!grid.BigOwners.Contains(me))
            return "You can only test your own grids.";

        if (now < s_nextServerTest)
            return "Someone else just ran a test. Try again in " + (((s_nextServerTest - now) / Clock.Frequency) + 1L) + " s.";

        // Every measurement costs the whole server, so a test waits its turn after any other kind.
        TimeSpan untilNextRun = ServerApi.Instance.UntilNextRun();

        if (untilNextRun > TimeSpan.Zero)
            return "A measurement just ran. Try again in " + ((long)untilNextRun.TotalSeconds + 1L) + " s.";

        if (FrameEngine.TryReadLatest(out RollupRecord latest) && latest.SimSpeed < s_settings.PlayerTestLowestSim)
            return "The server is busy, and a test would add to that. Try again when it's settled.";

        int seconds = Math.Min(Math.Max(s_settings.PlayerTestSeconds, 1), CaptureArgs.MaxSeconds);
        string name = grid.DisplayName;

        CaptureStart started = Capture.TryStart(seconds, new CaptureFilter { GridId = grid.EntityId }, result => Report(context, name, result));

        switch (started)
        {
            case CaptureStart.Started:
                s_nextTest[me] = now + (s_settings.PlayerTestCooldownSeconds * Clock.Frequency);
                s_nextServerTest = now + ((seconds + ServerGapSeconds) * Clock.Frequency);
                ServerApi.Instance.NoteTriggeredRun(seconds);
                return "Measuring '" + name + "' for " + seconds + " s.";
            case CaptureStart.Busy:
                return "A measurement is already running. Try again shortly.";
            default:
                return "Measuring isn't available right now. Try again shortly.";
        }
    }

    private static MyCubeGrid? OwnGridNamed(string name, long me)
    {
        foreach (MyEntity entity in MyEntities.GetEntities())
        {
            if (entity is MyCubeGrid grid && grid.BigOwners.Contains(me) && string.Equals(grid.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                return grid;
        }

        return null;
    }

    private static void Report(CommandContext context, string name, CaptureResult? result)
    {
        try
        {
            if (result == null)
                MonitorLog.Error("A player's test failed.", Capture.Fault ?? new InvalidOperationException("The run ended with no result."));

            ChatReply.Send(context, result == null ? "The measurement failed. Try again later." : LagReport.GridTest(result, name, RowsPerList));
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not report a player's test.", exception);
        }
    }
}
