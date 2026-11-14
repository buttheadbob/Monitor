namespace SenX.Monitor.Components.Charts;

/// <summary>One slice of a donut.</summary>
public sealed record ChartSlice(string Name, string Color, double Value);

/// <summary>Colours that mean the same thing on every chart, as on the old dashboards.</summary>
public static class ChartColors
{
    public const string Physics = "monitor-c-physics";
    public const string Network = "monitor-c-network";
    public const string Session = "monitor-c-session";
    public const string Other = "monitor-c-other";
    public const string Idle = "monitor-c-idle";
    public const string Good = "monitor-c-idle";
    public const string Bad = "monitor-c-physics";

    /// <summary>A join hitch, which is the game loading voxel files, so it shares no colour with a cause such as network.</summary>
    public const string Join = "monitor-c-0";

    /// <summary>A thread jump, in a colour no other marker uses.</summary>
    public const string ThreadJump = "monitor-c-6";

    private const int Cycle = 8;

    /// <summary>How many colours <see cref="At"/> has before it starts again.</summary>
    public const int Count = Cycle;

    /// <summary>A colour by position, for a few lines that mustn't share one.</summary>
    public static string At(int index) => "monitor-c-" + (index % Cycle);

    /// <summary>A colour for something with no meaning of its own, stable for a given name.</summary>
    public static string For(string name)
    {
        // Not string.GetHashCode, which changes between runs, so a series keeps its colour after a restart.
        int hash = 17;
        foreach (char c in name)
            hash = unchecked((hash * 31) + c);

        return "monitor-c-" + ((hash & int.MaxValue) % Cycle);
    }
}
