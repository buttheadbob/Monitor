using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Hooks;
using VRage.ModAPI;
using VRageMath;
using VRageMath.Spatial;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>
/// Lists the costliest physics clusters from a measuring run, and remembers them so an admin can
/// look inside one or go to it afterwards. Game thread only.
/// </summary>
internal static class ClusterReport
{
    private static List<ClusterView> s_last = new();

    private sealed class GridView
    {
        public string Name = string.Empty;
        public string Owner = string.Empty;
        public string Faction = string.Empty;
        public Vector3D Position;
        public double PerFrame;
        public long Id;
        public long Blocks;
        public RollupRow Row;
    }

    private sealed class ClusterView
    {
        public List<GridView> Grids = new();
        public double SizeKm;
        public Vector3D Center;
    }

    private const int MembersShown = 30;
    private const int TopGridsShown = 10;

    private static string Ms(double units) => (units / 10000d).ToString("0.00", CultureInfo.InvariantCulture);

    public static void Add(ChatReport report, CaptureResult result, int top)
    {
        RollupRow[] rows = result.Clusters.Top(top, AverageStep);
        double total = 0d;
        result.Clusters.Visit(row => total += AverageStep(row));

        double added = result.PerFrame(result.SiteTotal(Site.PhysicsCluster).MainUnits);
        report.Text(FormattableString.Invariant($"Timed {result.Clusters.Count} of {PhysicsHooks.CountActive()} active clusters, each alone in turn."));
        report.Note(FormattableString.Invariant($"This added at most {Ms(added)} ms/f. Alone means every physics thread to itself, so these add up to more than normal physics."));

        var views = new List<ClusterView>();
        ChatTable table = report.Table("Cluster", "Grids", "Km", "Alone", "Share", "Peak");

        foreach (RollupRow row in rows)
        {
            ClusterView? view = Describe(ClusterTurns.ClusterIdOf(row), result);
            int index = views.Count;
            views.Add(view ?? new ClusterView());

            double step = AverageStep(row);
            double share = total > 0d ? step / total * 100d : 0d;
            string names = view == null
                ? "(cluster gone)"
                : string.Join(", ", view.Grids.OrderByDescending(g => g.PerFrame).Take(2).Select(g => g.Name));

            table.Row(
                FormattableString.Invariant($"[{index}] {names}"),
                (view?.Grids.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                (view?.SizeKm ?? 0d).ToString("0.0", CultureInfo.InvariantCulture),
                Ms(step),
                share.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                Ms(row.MaxUnits));
        }

        if (rows.Length == 0)
            report.Text("No clusters were timed.");
        else
            report.Note("--inspect=N lists a cluster's grids, --takeme=N takes you there, --takeme=done finishes.");

        s_last = views;
    }

    /// <summary>The same clusters as rows for Quasar. A cluster's time is per step alone, not per frame.</summary>
    public static void AddRunRows(RunResultResponse response, CaptureResult result, int top)
    {
        foreach (RollupRow row in result.Clusters.Top(top, AverageStep))
        {
            ClusterView? view = Describe(ClusterTurns.ClusterIdOf(row), result);
            string name = view == null
                ? "(cluster gone)"
                : FormattableString.Invariant($"{view.Grids.Count} grids, {view.SizeKm:0.0} km: ")
                  + string.Join(", ", view.Grids.OrderByDescending(g => g.PerFrame).Take(3).Select(g => "\"" + g.Name + "\""));

            response.Rows.Add(new RunRow
            {
                List = RunLists.Clusters,
                Name = name,
                Members = view?.Grids.OrderByDescending(g => g.PerFrame).Take(MembersShown).Select(g => g.Name).ToList(),
                Key = ClusterTurns.ClusterIdOf(row),
                MainMsPerFrame = AverageStep(row) / 10000d,
                PeakMs = row.MaxUnits / 10000d,
                Grids = view?.Grids.Count ?? 0,
                SizeKm = view?.SizeKm ?? 0d,
                TopGrids = view?.Grids.OrderByDescending(g => g.PerFrame).Take(TopGridsShown).Select(g => TopGrid(g, result)).ToList(),
            });
        }
    }

    private static RunRow TopGrid(GridView grid, CaptureResult result) => new()
    {
        List = RunLists.Grids,
        Name = grid.Name,
        Key = grid.Id,
        Blocks = grid.Blocks,
        MainMsPerFrame = result.PerFrame(grid.Row.MainUnits) / 10000d,
        WorkerMsPerFrame = result.PerFrame(grid.Row.WorkerUnits) / 10000d,
        PeakMs = grid.Row.MaxUnits / 10000d,
        CallsPerFrame = result.PerFrame(grid.Row.Calls),
    };

    private static double AverageStep(RollupRow row) => row.Calls == 0L ? 0d : row.TotalUnits / (double)row.Calls;

    private static ClusterView? Describe(int clusterId, CaptureResult result)
    {
        foreach (MyClusterTree.MyCluster cluster in MyPhysics.Clusters.GetClusters())
        {
            if (cluster.ClusterId == clusterId && cluster.UserData is HkWorld world)
                return Describe(world, result);
        }

        return null;
    }

    private static ClusterView Describe(HkWorld world, CaptureResult result)
    {
        var view = new ClusterView();
        var seen = new HashSet<long>();
        var bounds = BoundingBoxD.CreateInvalid();

        foreach (HkRigidBody body in world.RigidBodies)
        {
            if (!(body.GetBody()?.Entity is IMyEntity entity) || !(entity.GetTopMostParent() is MyCubeGrid grid) || !seen.Add(grid.EntityId))
                continue;

            Vector3D position = grid.PositionComp.WorldAABB.Center;
            bounds.Include(ref position);

            long owner = grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0L;
            bool timed = result.Grids.TryFind(grid.EntityId, out RollupRow row);

            view.Grids.Add(new GridView
            {
                Name = grid.DisplayName,
                Owner = owner == 0L ? "Nobody" : MySession.Static.Players.TryGetIdentity(owner)?.DisplayName ?? "Unknown",
                Faction = MySession.Static.Factions.TryGetPlayerFaction(owner)?.Tag ?? "<single>",
                Position = position,
                PerFrame = timed ? result.PerFrame(row.TotalUnits) : 0d,
                Id = grid.EntityId,
                Blocks = grid.BlocksCount,
                Row = row,
            });
        }

        if (view.Grids.Count > 0)
        {
            view.Center = bounds.Center;
            view.SizeKm = bounds.Size.Max() / 1000d;
        }

        return view;
    }

    private static bool TryGet(int index, out ClusterView view, out string? problem)
    {
        view = null!;
        problem = null;

        if (s_last.Count == 0)
        {
            problem = "Run !monitor physics first.";
            return false;
        }

        if (index < 0 || index >= s_last.Count)
        {
            problem = "That cluster isn't in the last result. Pick 0 to " + (s_last.Count - 1).ToString(CultureInfo.InvariantCulture) + ".";
            return false;
        }

        view = s_last[index];
        return true;
    }

    /// <summary>The grids in a cluster from the last run, by faction.</summary>
    public static ChatReport Inspect(int index)
    {
        if (!TryGet(index, out ClusterView view, out string? problem))
            return problem!;

        var report = new ChatReport().Title(FormattableString.Invariant($"Grids in cluster {index}, as of the last run"));

        foreach (GridView grid in view.Grids.OrderBy(g => g.Faction).ThenBy(g => g.Owner))
            report.Item("[" + grid.Faction + "] " + grid.Owner + ": " + grid.Name);

        return report;
    }

    /// <summary>Moves the caller's character to a cluster, and marks its grids with GPS.</summary>
    public static string TakeMe(ulong steamId, long identity, int index)
    {
        if (!TryGet(index, out ClusterView view, out string? problem))
            return problem!;

        if (view.Grids.Count == 0)
            return "That cluster has gone.";

        if (!MySession.Static.Players.TryGetPlayerBySteamId(steamId, out var player) || player.Character == null)
            return "You need a character to do that.";

        MyCharacter character = player.Character;
        if (character.Parent != null)
            return "Leave your seat first.";

        ((IMyEntity)character).SetPosition(view.Center);

        GpsMarkers.Send(identity, view.Grids.Select(g => (g.Name, g.Position)));
        return "Move to another cluster with --takeme=N, or finish with --takeme=done.";
    }
}
