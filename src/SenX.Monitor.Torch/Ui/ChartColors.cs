using System.Windows.Media;

namespace SenX.Monitor.Torch.Ui;

/// <summary>
/// What the colour names the chart data uses look like here. They're the fallback colours from the
/// page's stylesheet, so a series reads the same on Quasar and Torch, and they sit midway between
/// light and dark, so they work on any theme.
/// </summary>
internal static class ChartPaints
{
    private static readonly Color s_physics = Rgb(0xE5, 0x53, 0x4B);
    private static readonly Color s_network = Rgb(0xD9, 0xA4, 0x41);
    private static readonly Color s_session = Rgb(0x4C, 0x8B, 0xF5);
    private static readonly Color s_other = Rgb(0xA3, 0x71, 0xF7);
    private static readonly Color s_idle = Rgb(0x3F, 0xB9, 0x50);
    private static readonly Color s_info = Rgb(0x56, 0xC8, 0xDC);
    private static readonly Color s_grey = Rgb(0x9A, 0xA4, 0xB2);

    private static readonly Dictionary<string, Brush> s_brushes = new()
    {
        ["monitor-c-physics"] = Solid(s_physics),
        ["monitor-c-network"] = Solid(s_network),
        ["monitor-c-session"] = Solid(s_session),
        ["monitor-c-other"] = Solid(s_other),
        ["monitor-c-idle"] = Solid(s_idle),
        ["monitor-c-0"] = Solid(s_info),
        ["monitor-c-1"] = Solid(Mix(s_network, s_physics, 0.6d)),
        ["monitor-c-2"] = Solid(Mix(s_session, s_idle, 0.6d)),
        ["monitor-c-3"] = Solid(Mix(s_other, s_physics, 0.6d)),
        ["monitor-c-4"] = Solid(Mix(s_idle, s_network, 0.6d)),
        ["monitor-c-5"] = Solid(Mix(s_grey, s_session, 0.8d)),
        ["monitor-c-6"] = Solid(Mix(s_info, s_other, 0.6d)),
        ["monitor-c-7"] = Solid(Mix(s_physics, s_session, 0.5d)),
    };

    public static Brush Of(string name) => s_brushes.TryGetValue(name, out Brush? brush) ? brush : s_brushes["monitor-c-5"];

    private static Color Rgb(byte red, byte green, byte blue) => Color.FromRgb(red, green, blue);

    private static Color Mix(Color first, Color second, double share) => Rgb(
        (byte)((first.R * share) + (second.R * (1d - share))),
        (byte)((first.G * share) + (second.G * (1d - share))),
        (byte)((first.B * share) + (second.B * (1d - share))));

    private static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
