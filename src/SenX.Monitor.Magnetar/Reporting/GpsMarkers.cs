using System.Collections.Generic;
using System.Globalization;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SenX.Monitor.Core;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>GPS markers that take an admin to the grids a run found. Game thread only.</summary>
internal static class GpsMarkers
{
    private static readonly Dictionary<long, List<IMyGps>> s_sent = new();

    public static void SendTopGrids(long identity, CaptureResult result, int top)
    {
        var places = new List<(string Name, Vector3D Position)>();

        foreach (RollupRow row in result.Grids.Top(top))
        {
            if (!MyEntities.TryGetEntityById(row.Key, out MyEntity entity) || !(entity is MyCubeGrid grid))
                continue;

            string perFrame = (result.PerFrame(row.TotalUnits) / 10000d).ToString("0.00", CultureInfo.InvariantCulture);
            places.Add((grid.DisplayName + " (" + perFrame + " ms/f)", grid.PositionComp.WorldAABB.Center));
        }

        Send(identity, places);
    }

    /// <summary>Marks a list of places for one identity, replacing any markers Monitor sent them before.</summary>
    public static void Send(long identity, IEnumerable<(string Name, Vector3D Position)> places)
    {
        Clean(identity);

        IMyGpsCollection? collection = MyAPIGateway.Session?.GPS;
        if (collection == null)
            return;

        var sent = new List<IMyGps>();

        foreach ((string name, Vector3D position) in places)
        {
            IMyGps marker = collection.Create(name, string.Empty, position, true);
            collection.AddGps(identity, marker);
            sent.Add(marker);
        }

        s_sent[identity] = sent;
    }

    /// <summary>Removes the markers Monitor sent this identity. Leaves their own GPS entries alone.</summary>
    public static int Clean(long identity)
    {
        if (!s_sent.TryGetValue(identity, out List<IMyGps>? sent))
            return 0;

        s_sent.Remove(identity);

        IMyGpsCollection? collection = MyAPIGateway.Session?.GPS;
        if (collection == null)
            return 0;

        foreach (IMyGps marker in sent)
            collection.RemoveGps(identity, marker);

        return sent.Count;
    }
}
