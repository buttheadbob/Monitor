namespace SenX.Monitor.Core;

/// <summary>Detail sites that are measured together.</summary>
public sealed class SiteGroup
{
    public SiteGroup(string name, params int[] sites)
    {
        Name = name;
        Sites = sites;
    }

    public string Name { get; }

    public int[] Sites { get; }
}

/// <summary>
/// How detail sites are grouped. Related work is measured together, so numbers inside a group came
/// from the same frames. The costliest measurements are spread across groups, so no single turn
/// carries them all. A site may sit in several groups.
/// </summary>
public static class SiteGroups
{
    public static readonly SiteGroup[] All =
    {
        new("Grids and blocks", Site.ParallelBefore, Site.ParallelAfter, Site.GridCallback, Site.EntityBefore, Site.EntityAfter),
        new("Periodic blocks", Site.EntityBefore10, Site.EntityBefore100, Site.EntityAfter10, Site.EntityAfter100),
        new("Logic and scripts", Site.GameLogic, Site.EntityLogic, Site.CompositeLogic, Site.Script),
        new("Session", Site.SessionBefore, Site.SessionSimulate, Site.SessionAfter, Site.InvokeLater, Site.EntitySimulate, Site.EntityOnce, Site.PluginUpdate, Site.Rpc),
    };

    /// <summary>
    /// True for sites that run inside another site's timed call, such as a grid callback inside a
    /// grid update. Their time is already in the outer site, so totals must not add it twice.
    /// </summary>
    public static bool IsNested(int site) =>
        site == Site.GridCallback || site == Site.EntityLogic || site == Site.CompositeLogic || site == Site.Script;

    public static string Name(int site)
    {
        switch (site)
        {
            case Site.EntityBefore: return "Entity before";
            case Site.EntityBefore10: return "Entity before 10";
            case Site.EntityBefore100: return "Entity before 100";
            case Site.EntityAfter: return "Entity after";
            case Site.EntityAfter10: return "Entity after 10";
            case Site.EntityAfter100: return "Entity after 100";
            case Site.EntitySimulate: return "Entity simulate";
            case Site.EntityOnce: return "Entity once";
            case Site.ParallelBefore: return "Parallel before";
            case Site.ParallelAfter: return "Parallel after";
            case Site.GameLogic: return "Game logic";
            case Site.EntityLogic: return "Entity logic";
            case Site.CompositeLogic: return "Composite logic";
            case Site.PluginUpdate: return "Plugin update";
            case Site.Rpc: return "RPCs";
            case Site.SessionBefore: return "Session before";
            case Site.SessionSimulate: return "Session simulate";
            case Site.SessionAfter: return "Session after";
            case Site.GridCallback: return "Grid callbacks";
            case Site.InvokeLater: return "Invoke later";
            case Site.Script: return "Scripts";
            default: return "Site " + site;
        }
    }
}
