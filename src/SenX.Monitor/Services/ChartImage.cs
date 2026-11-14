using System.Globalization;

namespace SenX.Monitor.Services;

/// <summary>
/// Draws one chart as a PNG, for plugins that want a picture to send somewhere. It takes the same
/// <see cref="ChartData"/> as the browser, so it means the same thing: stacked bands, overlay lines, a second scale,
/// breaks at gaps and dashed lines for events. Times are UTC, because a picture has no reader's clock to go by.
/// </summary>
internal static class ChartImage
{
    public const int LeastWidth = 320;
    public const int MostWidth = 1920;
    public const int LeastHeight = 200;
    public const int MostHeight = 1080;

    private const int Background = 0x181B1F;
    private const int Grid = 0x24272D;
    private const int Text = 0xCCCCDC;
    private const int Muted = 0x8E8E9E;
    private const int Neutral = 0x8E8E9E;

    private const double TitleSize = 11d;
    private const double LabelSize = 8.5d;
    private const double Margin = 12d;
    private const double RowHeight = 16d;
    private const int MostLegendRows = 3;
    private const int MostTicks = 6;

    private static readonly Dictionary<string, int> s_palette = new(StringComparer.Ordinal)
    {
        ["monitor-c-physics"] = 0xF2495C,
        ["monitor-c-network"] = 0xFF9830,
        ["monitor-c-session"] = 0x5794F2,
        ["monitor-c-other"] = 0xB877D9,
        ["monitor-c-idle"] = 0x73BF69,
        ["monitor-c-0"] = 0x8AB8FF,
        ["monitor-c-1"] = 0xFADE2A,
        ["monitor-c-2"] = 0x37C9A4,
        ["monitor-c-3"] = 0xE0589F,
        ["monitor-c-4"] = 0xC4D44F,
        ["monitor-c-5"] = 0xA3A9B5,
        ["monitor-c-6"] = 0x6F7FF2,
        ["monitor-c-7"] = 0xFF7383,
    };

    private static readonly double[] s_timeSteps = [10d, 30d, 60d, 300d, 600d, 1800d, 3600d, 10800d, 21600d, 43200d, 86400d, 172800d];

    /// <summary>The picture, or null when there's nothing to draw.</summary>
    public static byte[]? Render(ChartData chart, string title, int width, int height, IReadOnlyList<ChartMarker> markers, IReadOnlyList<BoardLevel>? levels = null)
    {
        List<ChartLine> lines = Ordered(chart);

        if (lines.Count == 0 || chart.Times.Length < 2 || chart.Times[chart.Times.Length - 1] <= chart.Times[0])
            return null;

        width = Math.Min(Math.Max(width, LeastWidth), MostWidth);
        height = Math.Min(Math.Max(height, LeastHeight), MostHeight);

        var canvas = new ImageCanvas(width, height, Background);

        StrokeFont.Draw(canvas, title, (width - StrokeFont.Measure(title, TitleSize)) / 2d, Margin + TitleSize, TitleSize, Text, 1.25d);

        bool secondScale = lines.Exists(line => line.SecondScale);
        List<LegendItem> legend = LegendOf(chart, lines);
        int legendRows = Rows(legend, width);
        double start = chart.Times[0];
        double end = chart.Times[chart.Times.Length - 1];

        var bands = lines.Where(line => IsBand(chart, line)).ToList();
        var columns = new Dictionary<ChartLine, double[]>();
        double plotTop = (Margin * 2d) + TitleSize;
        double plotBottom = height - Margin - (legendRows * RowHeight) - 24d;

        double peak = Peak(chart, lines, bands);
        double top = chart.Maximum ?? NiceTop(peak);
        double[] ticks = Ticks(top);
        double axisWidth = Math.Max(26d, ticks.Max(tick => StrokeFont.Measure(Short(tick), LabelSize)) + 8d);
        double plotLeft = Margin + axisWidth;
        double plotRight = width - Margin - (secondScale ? 26d : 0d);
        int plotWidth = (int)(plotRight - plotLeft);

        double XOf(double time) => plotLeft + ((time - start) / (end - start) * (plotRight - plotLeft));
        double YOf(double value, double scale) => plotBottom - (value / scale * (plotBottom - plotTop));

        foreach (ChartLine line in lines)
            columns[line] = Columns(chart.Times, line.Values, start, end, plotWidth);

        DrawAxes(canvas, ticks, top, plotLeft, plotRight, plotTop, plotBottom, secondScale, start, end);

        var below = new double[plotWidth];

        foreach (ChartLine band in bands)
            DrawBand(canvas, columns[band], below, band, plotLeft, plotTop, plotBottom, top);

        foreach (ChartLine line in lines.Where(line => !IsBand(chart, line)))
            DrawLine(canvas, columns[line], line, plotLeft, plotTop, plotBottom, line.SecondScale ? 1.05d : top, !line.Overlay);

        foreach (BoardLevel level in levels ?? [])
        {
            double y = YOf(level.Value, top);

            if (y >= plotTop && y <= plotBottom)
                canvas.Dashed((plotLeft, y), (plotRight, y), 1d, ColorOf(level.Color));
        }

        foreach (ChartMarker marker in markers)
        {
            if (marker.Time >= start && marker.Time <= end)
                canvas.Dashed((XOf(marker.Time), plotTop), (XOf(marker.Time), plotBottom), 1d, ColorOf(marker.Color), 3d, 3d);
        }

        DrawLegend(canvas, legend, width, height);

        return canvas.ToPng();
    }

    private static bool IsBand(ChartData chart, ChartLine line) => chart.Stacked && !line.Overlay;

    private static List<ChartLine> Ordered(ChartData chart)
    {
        List<ChartLine> shown = chart.Lines
            .Where(line => line.Overlay || line.Values.Any(value => (value ?? 0d) >= chart.MinimumToShow))
            .Where(line => line.Values.Length == chart.Times.Length)
            .ToList();

        return [.. shown.Where(line => IsBand(chart, line)), .. shown.Where(line => !IsBand(chart, line))];
    }

    private static int ColorOf(string name) => s_palette.TryGetValue(name, out int color) ? color : Neutral;

    private static double Peak(ChartData chart, List<ChartLine> lines, List<ChartLine> bands)
    {
        double peak = 0d;

        foreach (ChartLine line in lines.Where(line => !IsBand(chart, line) && !line.SecondScale))
        {
            foreach (double? value in line.Values)
                peak = Math.Max(peak, Usable(value));
        }

        for (int index = 0; index < chart.Times.Length; index++)
        {
            double total = 0d;

            foreach (ChartLine band in bands)
                total += Usable(band.Values[index]);

            peak = Math.Max(peak, total);
        }

        return peak;
    }

    // A number that isn't one is a gap in the line, so it can't be allowed to set the scale.
    private static double Usable(double? value) => value is { } known && !double.IsNaN(known) && !double.IsInfinity(known) ? known : 0d;

    private static double NiceTop(double peak)
    {
        if (peak <= 0d)
            return 1d;

        double step = NiceStep(peak * 1.1d / MostTicks);
        return Math.Ceiling(peak * 1.05d / step) * step;
    }

    private static double NiceStep(double rough)
    {
        double power = Math.Pow(10d, Math.Floor(Math.Log10(rough)));
        double share = rough / power;

        return (share <= 1d ? 1d : share <= 2d ? 2d : share <= 5d ? 5d : 10d) * power;
    }

    private static double[] Ticks(double top)
    {
        double step = NiceStep(top / MostTicks);
        var ticks = new List<double>();

        for (double tick = 0d; tick <= top * 1.0001d; tick += step)
            ticks.Add(tick);

        return [.. ticks];
    }

    private static string Short(double value)
    {
        double size = Math.Abs(value);

        if (size >= 1e6d)
            return (value / 1e6d).ToString("0.#", CultureInfo.InvariantCulture) + "M";

        if (size >= 1e4d)
            return (value / 1e3d).ToString("0.#", CultureInfo.InvariantCulture) + "k";

        return value.ToString(size >= 100d ? "0" : "0.##", CultureInfo.InvariantCulture);
    }

    private static string Reading(double? value, string unit)
    {
        if (value is not { } known)
            return "-";

        double size = Math.Abs(known);
        return known.ToString(size >= 100d ? "#,0" : size >= 10d ? "0.0" : "0.00", CultureInfo.InvariantCulture) + unit;
    }

    // One value for each column of pixels. Many points to a column are averaged. Few are joined by a curve that
    // doesn't overshoot, the same kind the browser draws, and a missing value leaves its columns empty.
    private static double[] Columns(double[] times, double?[] values, double start, double end, int width)
    {
        var columns = new double[width];
        var sums = new double[width];
        var counts = new int[width];

        Array.Fill(columns, double.NaN);

        var runX = new List<double>();
        var runY = new List<double>();

        void Finish()
        {
            for (int column = 0; column < width; column++)
            {
                if (counts[column] > 0)
                {
                    runX.Add(column);
                    runY.Add(sums[column] / counts[column]);
                    sums[column] = 0d;
                    counts[column] = 0;
                }
            }

            Curve(runX, runY, columns);
            runX.Clear();
            runY.Clear();
        }

        for (int index = 0; index < times.Length; index++)
        {
            if (values[index] is not { } value || double.IsNaN(value) || double.IsInfinity(value))
            {
                Finish();
                continue;
            }

            int column = Math.Max(Math.Min((int)((times[index] - start) / (end - start) * (width - 1)), width - 1), 0);
            sums[column] += value;
            counts[column]++;
        }

        Finish();
        return columns;
    }

    private static void Curve(List<double> xs, List<double> ys, double[] columns)
    {
        int count = xs.Count;

        if (count == 0)
            return;

        if (count == 1)
        {
            columns[(int)xs[0]] = ys[0];
            return;
        }

        var slopes = new double[count - 1];

        for (int i = 0; i < count - 1; i++)
            slopes[i] = (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i]);

        // Fritsch and Carlson's tangents, which keep a curve between its two points.
        var tangents = new double[count];
        tangents[0] = slopes[0];
        tangents[count - 1] = slopes[count - 2];

        for (int i = 1; i < count - 1; i++)
            tangents[i] = slopes[i - 1] * slopes[i] <= 0d ? 0d : 2d / ((1d / slopes[i - 1]) + (1d / slopes[i]));

        for (int i = 0; i < count - 1; i++)
        {
            double span = xs[i + 1] - xs[i];

            for (int column = (int)xs[i]; column <= (int)xs[i + 1]; column++)
            {
                double t = (column - xs[i]) / span;
                double t2 = t * t;
                double t3 = t2 * t;

                columns[column] =
                    (((2d * t3) - (3d * t2) + 1d) * ys[i])
                    + ((t3 - (2d * t2) + t) * span * tangents[i])
                    + ((-(2d * t3) + (3d * t2)) * ys[i + 1])
                    + ((t3 - t2) * span * tangents[i + 1]);
            }
        }
    }

    private static void DrawAxes(ImageCanvas canvas, double[] ticks, double top, double left, double right, double plotTop, double plotBottom, bool secondScale, double start, double end)
    {
        foreach (double tick in ticks)
        {
            double y = plotBottom - (tick / top * (plotBottom - plotTop));
            string label = Short(tick);

            canvas.Stroke([(left, y), (right, y)], 1d, Grid);
            StrokeFont.Draw(canvas, label, left - 6d - StrokeFont.Measure(label, LabelSize), y + (LabelSize / 2d), LabelSize, Muted);
        }

        if (secondScale)
        {
            for (int tick = 0; tick <= 5; tick++)
            {
                double value = tick / 5d;
                double y = plotBottom - (value / 1.05d * (plotBottom - plotTop));

                StrokeFont.Draw(canvas, value.ToString("0.0", CultureInfo.InvariantCulture), right + 6d, y + (LabelSize / 2d), LabelSize, Muted);
            }
        }

        double span = end - start;
        double step = Array.Find(s_timeSteps, candidate => span / candidate <= (right - left) / 90d);

        if (step <= 0d)
            step = s_timeSteps[s_timeSteps.Length - 1];

        string format = span > 86400d ? "MM-dd HH:mm" : step < 60d ? "HH:mm:ss" : "HH:mm";

        for (double time = Math.Ceiling(start / step) * step; time <= end; time += step)
        {
            double x = left + ((time - start) / span * (right - left));
            string label = DateTimeOffset.FromUnixTimeSeconds((long)time).UtcDateTime.ToString(format, CultureInfo.InvariantCulture);

            canvas.Stroke([(x, plotTop), (x, plotBottom)], 1d, Grid);
            StrokeFont.Draw(canvas, label, x - (StrokeFont.Measure(label, LabelSize) / 2d), plotBottom + 8d + LabelSize, LabelSize, Muted);
        }

        const string Zone = "UTC";
        StrokeFont.Draw(canvas, Zone, right - StrokeFont.Measure(Zone, LabelSize), plotBottom + 8d + LabelSize + 13d, LabelSize, Muted);
    }

    private static void DrawBand(ImageCanvas canvas, double[] values, double[] below, ChartLine line, double left, double plotTop, double plotBottom, double top)
    {
        int color = ColorOf(line.Color);
        double scale = (plotBottom - plotTop) / top;
        var edge = new List<(double X, double Y)>();

        for (int column = 0; column < values.Length; column++)
        {
            if (double.IsNaN(values[column]))
            {
                canvas.Stroke(edge, 1d, color);
                edge.Clear();
                continue;
            }

            double upper = below[column] + values[column];
            double y = plotBottom - (upper * scale);

            canvas.FillColumn((int)left + column, Math.Max(y, plotTop), plotBottom - (below[column] * scale), color, 0.34d, 0.34d, plotTop, plotBottom);
            edge.Add((left + column + 0.5d, Math.Max(y, plotTop)));
            below[column] = upper;
        }

        canvas.Stroke(edge, 1d, color);
    }

    private static void DrawLine(ImageCanvas canvas, double[] values, ChartLine line, double left, double plotTop, double plotBottom, double top, bool fade)
    {
        int color = ColorOf(line.Color);
        double scale = (plotBottom - plotTop) / top;
        var edge = new List<(double X, double Y)>();

        for (int column = 0; column < values.Length; column++)
        {
            if (double.IsNaN(values[column]))
            {
                DrawEdge(canvas, edge, 1.6d, color, line.Dashed);
                edge.Clear();
                continue;
            }

            double y = Math.Max(plotBottom - (values[column] * scale), plotTop);

            if (fade)
                canvas.FillColumn((int)left + column, y, plotBottom, color, 0.3d, 0.01d, plotTop, plotBottom);

            edge.Add((left + column + 0.5d, y));
        }

        DrawEdge(canvas, edge, 1.6d, color, line.Dashed);
    }

    private static void DrawEdge(ImageCanvas canvas, List<(double X, double Y)> edge, double thickness, int color, bool dashed)
    {
        if (!dashed)
        {
            canvas.Stroke(edge, thickness, color);
            return;
        }

        if (edge.Count > 1)
            canvas.Dashed(edge[0], edge[edge.Count - 1], thickness, color);
    }

    private readonly record struct LegendItem(string Text, int Color);

    private static List<LegendItem> LegendOf(ChartData chart, List<ChartLine> lines) => lines
        .Select(line => new LegendItem(
            line.Name + "  " + Reading(Array.FindLast(line.Values, value => value.HasValue), line.Unit ?? chart.Unit),
            ColorOf(line.Color)))
        .ToList();

    private static double ItemWidth(LegendItem item) => StrokeFont.Measure(item.Text, LabelSize) + 34d;

    private static int Rows(List<LegendItem> legend, int width)
    {
        int rows = 1;
        double used = Margin;

        foreach (LegendItem item in legend)
        {
            if (used + ItemWidth(item) > width - Margin)
            {
                rows++;
                used = Margin;
            }

            used += ItemWidth(item);
        }

        return Math.Min(rows, MostLegendRows);
    }

    private static void DrawLegend(ImageCanvas canvas, List<LegendItem> legend, int width, int height)
    {
        int rows = Rows(legend, width);
        double x = Margin;
        int row = 0;

        foreach (LegendItem item in legend)
        {
            if (x + ItemWidth(item) > width - Margin)
            {
                row++;
                x = Margin;
            }

            if (row >= rows)
                return;

            double y = height - Margin - ((rows - 1 - row) * RowHeight);

            canvas.Stroke([(x, y - 3.5d), (x + 12d, y - 3.5d)], 2.5d, item.Color);
            StrokeFont.Draw(canvas, StrokeFont.Fit(item.Text, LabelSize, width - Margin - x - 18d), x + 18d, y, LabelSize, Text);
            x += ItemWidth(item);
        }
    }
}
