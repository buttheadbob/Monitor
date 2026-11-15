using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PluginSdk.Commands;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Patching;
using VRage.Game.Entity;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>What a lag-finding command shows. Every run measures everything, so this only picks the rows.</summary>
internal enum LagView
{
    Grids,
    BlockTypes,
    Blocks,
    Players,
    Factions,
    Scripts,
    Session,
    Entities,
    Logic,
    Callbacks,
    Rpcs,
    Mods,
    Physics,
    Diagnose,
}

/// <summary>
/// Starts a lag-finding command and replies when it finishes. Detail switches to Everything for the
/// run and goes back by itself. Runs on the game thread, on demand only.
/// </summary>
internal static class LagRuns
{
    public static ChatReport Start(CommandContext context, LagView view, string label, string[] words)
    {
        CaptureArgs? args = CaptureArgs.Parse(words, out string? error);
        if (args == null)
            return error!;

        if (args.Gps && context.Caller.IdentityId == 0L)
            return "--gps can only be used by a player.";

        if (args.ForPhysicsOnly)
        {
            if (view != LagView.Physics)
                return "--inspect and --takeme only work with !monitor physics.";

            return FollowUp(context, args);
        }

        string? problem = args.ProblemWith(OptionsFor(view));
        if (problem != null)
            return problem;

        CaptureFilter? filter = BuildFilter(context, args, out error);
        if (error != null)
            return error;

        bool wantsClusters = view == LagView.Physics || view == LagView.Diagnose;
        string? clusterProblem = wantsClusters ? PatchEngine.ClusterTurnProblem() : null;

        CaptureStart started = Capture.TryStart(
            args.Seconds,
            filter,
            result => Finish(context, view, args, result, clusterProblem),
            wantsClusters && clusterProblem == null);

        switch (started)
        {
            case CaptureStart.Started:
                return FormattableString.Invariant($"Measuring {label} for {args.Seconds} s, with every call timed. Results follow when it's done.");
            case CaptureStart.Busy:
                return FormattableString.Invariant($"A measurement is already running, about {Capture.SecondsLeft} s left.");
            case CaptureStart.Paused:
                return "The server is paused, so there's nothing to measure.";
            default:
                return "Detail starts 30 seconds after the game is ready. Try again shortly.";
        }
    }

    private static CaptureOptions OptionsFor(LagView view)
    {
        switch (view)
        {
            case LagView.Grids:
                return CaptureOptions.Filters | CaptureOptions.Gps | CaptureOptions.Details;
            case LagView.BlockTypes:
            case LagView.Blocks:
            case LagView.Players:
            case LagView.Factions:
            case LagView.Scripts:
                return CaptureOptions.Filters;
            case LagView.Diagnose:
                return CaptureOptions.Details;
            default:
                return CaptureOptions.None;
        }
    }

    private static ChatReport FollowUp(CommandContext context, CaptureArgs args)
    {
        if (args.Inspect.HasValue)
            return ClusterReport.Inspect(args.Inspect.Value);

        if (context.Caller.IdentityId == 0L)
            return "--takeme can only be used by a player.";

        if (args.TakeMeDone)
        {
            GpsMarkers.Clean(context.Caller.IdentityId);
            return "Finished.";
        }

        return ClusterReport.TakeMe(context.Caller.SteamId, context.Caller.IdentityId, args.TakeMe!.Value);
    }

    private static void Finish(CommandContext context, LagView view, CaptureArgs args, CaptureResult? result, string? clusterProblem)
    {
        try
        {
            if (result == null)
            {
                MonitorLog.Error("Monitor's measurement failed.", Capture.Fault ?? new InvalidOperationException("No result."));
                context.Respond("The measurement failed. The details are in the server log.");
                return;
            }

            ChatReport report = LagReport.Build(view, args, result, clusterProblem);
            MonitorLog.Info(report.ToString());
            ChatReply.Send(context, report);

            RunService.Publish(result, clusterProblem, context.Caller.Name);

            if (args.Gps && view == LagView.Grids)
                GpsMarkers.SendTopGrids(context.Caller.IdentityId, result, args.Top);
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not report a measurement.", exception);
        }
    }

    private static CaptureFilter? BuildFilter(CommandContext context, CaptureArgs args, out string? error)
    {
        error = null;
        long gridId = args.GridId;

        if (args.This)
        {
            MyCubeGrid? grid = ControlledGrid(context.Caller.SteamId);
            if (grid == null)
            {
                error = "--this needs you to be controlling a grid.";
                return null;
            }

            gridId = grid.EntityId;
        }

        return BuildFilter(gridId, args.Player, args.Faction, args.NoBlocks, args.BlockType, out error);
    }

    /// <summary>Turns what to limit a run to into a filter, however it was asked for. Game thread only.</summary>
    internal static CaptureFilter? BuildFilter(long gridId, string? playerName, string? factionName, bool noBlocks, string? blockType, out string? error)
    {
        error = null;
        var filter = new CaptureFilter { GridId = gridId, GridsOnly = noBlocks };

        if (filter.GridId != 0L && !(MyEntities.TryGetEntityById(filter.GridId, out MyEntity entity) && entity is MyCubeGrid))
        {
            error = "No grid has the id " + filter.GridId + ".";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(playerName))
        {
            MyIdentity? identity = FindPlayer(playerName!);

            if (identity == null)
            {
                error = "No player matches '" + playerName + "'. Use a name, an identity id, or sid/STEAMID.";
                return null;
            }

            filter.Owners = new[] { identity.IdentityId };
        }

        if (!string.IsNullOrWhiteSpace(factionName))
        {
            MyFaction? faction = FindFaction(factionName!);
            if (faction == null)
            {
                error = "No faction matches '" + factionName + "'. Use a tag, a name or a faction id.";
                return null;
            }

            long[] members = faction.Members.Keys.ToArray();
            Array.Sort(members);

            // Both must hold, so a player in a faction is that player, and not the whole faction.
            filter.Owners = filter.Owners == null ? members : filter.Owners.Where(owner => Array.BinarySearch(members, owner) >= 0).ToArray();

            if (filter.Owners.Length == 0)
            {
                error = "That player isn't in that faction.";
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(blockType))
        {
            filter.BlockTypes = BlockClasses.Matching(blockType!);

            // Nothing matching is most likely a typo, and a run that finds nothing would hide that.
            if (filter.BlockTypes.Length == 0)
            {
                error = "No block class has '" + blockType + "' in its name.";
                return null;
            }
        }

        bool narrowed = filter.GridId != 0L || filter.Owners != null || filter.GridsOnly || filter.BlockTypes != null;
        return narrowed ? filter : null;
    }

    private static MyIdentity? FindPlayer(string text)
    {
        MyPlayerCollection players = MySession.Static.Players;

        if (text.StartsWith("sid/", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(text.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong steamId))
            return players.TryGetIdentity(players.TryGetIdentityId(steamId));

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long identityId))
            return players.TryGetIdentity(identityId);

        return players.GetAllIdentities().FirstOrDefault(identity => string.Equals(identity.DisplayName, text, StringComparison.OrdinalIgnoreCase));
    }

    private static MyFaction? FindFaction(string text)
    {
        foreach (KeyValuePair<long, MyFaction> faction in MySession.Static.Factions)
        {
            if (string.Equals(faction.Value.Tag, text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(faction.Value.Name, text, StringComparison.OrdinalIgnoreCase)
                || faction.Key.ToString(CultureInfo.InvariantCulture) == text)
            {
                return faction.Value;
            }
        }

        return null;
    }

    /// <summary>The grid a player is controlling, if any.</summary>
    internal static MyCubeGrid? ControlledGrid(ulong steamId)
    {
        if (!MySession.Static.Players.TryGetPlayerBySteamId(steamId, out var player))
            return null;

        MyEntity? entity = player.Controller?.ControlledEntity?.Entity;

        while (entity != null)
        {
            if (entity is MyCubeGrid grid)
                return grid;

            entity = entity.Parent;
        }

        return null;
    }
}
