using SenX.Monitor.Contracts;
using SenX.Monitor.Core;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>What the admin commands do, once each host has worked out who asked.</summary>
internal static class AdminActions
{
    public const string HowToText =
        "Players: !monitor mine shows what your grids cost, and !monitor test times one. Admins: !monitor diagnose finds what's causing lag. " +
        "Times are per frame. Main is the game thread, which is what holds a frame up. Workers is added up across threads, so it can pass the frame. " +
        "Peak is the slowest single call. A frame has 16.67 ms before sim speed falls. " +
        "Commands that time everything cost the server while they run, and each one says what timing added.";

    private const string WarmingUp = "Detail starts 30 seconds after the game is ready. Try again shortly.";

    private static DetailMode s_beforeEverything = DetailMode.Grouped;

    public static ChatReport Top(string list)
    {
        GroupDimension dimension = GroupDimensions.Named(list);

        return dimension == GroupDimension.None
            ? "No list is called '" + list + "'. Use grids, blocktypes, blocks, players, factions, entities, session, logic, callbacks, rpcs, rpcssent, scripts or mods."
            : LagReport.Background(dimension);
    }

    public static string CleanGps(long identityId)
    {
        if (identityId == 0L)
            return "Only a player has GPS markers.";

        int removed = GpsMarkers.Clean(identityId);
        return removed == 1 ? "Removed 1 marker." : "Removed " + removed.ToString() + " markers.";
    }

    public static string Everything(bool on)
    {
        if (!DetailController.Ready)
            return WarmingUp;

        if (on && DetailController.Mode != DetailMode.Everything)
            s_beforeEverything = DetailController.Mode;

        DetailController.SetMode(on ? DetailMode.Everything : s_beforeEverything);

        return on
            ? "Everything is on. Every call is timed until you turn it off."
            : "Everything is off. Detail is back to " + s_beforeEverything.ToString().ToLowerInvariant() + ".";
    }

    public static string Trigger(bool on)
    {
        IncidentService.SetEnabled(on);

        return on
            ? "Monitor will gather evidence by itself when it notices lag."
            : "Monitor won't react to lag by itself. Its saved settings put this back when the server next starts.";
    }

    public static string Detail(DetailMode mode)
    {
        if (!DetailController.Ready)
            return WarmingUp;

        DetailController.SetMode(mode);

        switch (mode)
        {
            case DetailMode.Grouped:
                return "Detail is grouped. One related group is measured at a time, so compare groups by share of the frame.";
            case DetailMode.Everything:
                return "Detail is everything. Every call is timed until you change it.";
            default:
                return "Detail is off. The tree keeps running, and lag-finding commands still work.";
        }
    }
}
