using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui;

/// <summary>
/// Draws one chart. It takes the same data the browser's charts do, so a chart means the same thing
/// on both: stacked bands, overlay lines, a second scale on the right, broken lines where data is
/// missing, and timelines with a row per name.
///
/// There are two layers. The plot only redraws when the data or size changes, and the cursor and legend
/// sit on their own layer, so moving the mouse doesn't rebuild every line.
/// </summary>
public sealed class ChartView : FrameworkElement
{
    private const double PlotHeight = 220d;
    private const double LeftAxis = 56d;
    private const double TimelineAxis = 130d;
    private const double RightGap = 12d;
    private const double SecondAxis = 44d;
    private const double TopGap = 6d;
    private const double BottomAxis = 22d;
    private const double LegendRow = 17d;
    private const double RowHeight = 20d;
    private const double TimelineExtra = 44d;
    private const double TextSize = 11d;
    private const double SmallestLabelGap = 90d;
    private const double SecondScaleTop = 1.05d;
    private const double DragToZoom = 4d;
    private const int YTicks = 5;

    // The steps a time axis can use, in seconds. The first that leaves room for its labels wins.
    private static readonly double[] s_timeSteps =
    [
        1d, 5d, 10d, 15d, 30d, 60d, 120d, 300d, 600d, 900d, 1800d, 3600d, 7200d, 10800d, 21600d, 43200d, 86400d, 172800d, 604800d,
    ];

    private static readonly Typeface s_typeface = new("Segoe UI");
    private static readonly DashStyle s_dashed = Freeze(new DashStyle([6d, 4d], 0d));
    private static readonly DashStyle s_markerDash = Freeze(new DashStyle([3d, 3d], 0d));
    private static readonly Pen s_gridPen = Freeze(new Pen(Look.Line, 1d));

    private readonly ChartBoard _board;
    private readonly DrawingVisual _plot = new();
    private readonly DrawingVisual _overlay = new();
    private readonly VisualCollection _layers;
    private readonly List<Series> _series = [];
    private readonly List<LegendEntry> _legend = [];

    private ChartData? _chart;
    private double[] _times = [];
    private double _legendHeight;
    private double _legendWidth = -1d;
    private double? _dragStart;
    private double _dragNow;
    private double _from;
    private double _to = 1d;
    private double _yTop = 1d;
    private double _left = LeftAxis;
    private double _plotWidth = 1d;
    private double _plotBottom = PlotHeight - BottomAxis;
    private bool _hasSecondScale;

    public ChartView(ChartBoard board)
    {
        _board = board;
        _layers = new VisualCollection(this) { _plot, _overlay };
        ClipToBounds = true;
        Focusable = false;
    }

    private bool IsTimeline => _chart?.Rows != null;

    private double BodyHeight => IsTimeline ? TimelineExtra + (_chart!.Rows!.Count * RowHeight) : PlotHeight;

    protected override int VisualChildrenCount => _layers.Count;

    protected override Visual GetVisualChild(int index) => _layers[index];

    private static T Freeze<T>(T item) where T : Freezable
    {
        item.Freeze();
        return item;
    }

    /// <summary>Takes new data, and says whether there's anything in it to draw.</summary>
    public bool Show(ChartData chart)
    {
        _chart = chart;
        _times = chart.Times;
        _left = chart.Rows != null ? TimelineAxis : LeftAxis;
        _series.Clear();

        List<ChartLine> shown = chart.Lines
            .Where(line => line.Overlay || line.Values.Any(value => (value ?? 0d) >= chart.MinimumToShow))
            .ToList();

        // Bands come first, so overlays draw over them.
        double?[]? below = null;

        foreach (ChartLine line in shown.Where(line => IsBand(chart, line)))
        {
            double?[] total = Total(Finite(line.Values), below);
            _series.Add(new Series(line, ChartPaints.Of(line.Color), band: true, total, below));
            below = total;
        }

        foreach (ChartLine line in shown.Where(line => !IsBand(chart, line)))
            _series.Add(new Series(line, ChartPaints.Of(line.Color), band: false, Finite(line.Values), null));

        _hasSecondScale = _series.Any(series => series.Line.SecondScale);
        _legendWidth = -1d;
        InvalidateMeasure();
        Redraw();
        return _series.Count > 0 && _times.Length > 0;
    }

    /// <summary>Lets go of what it held, so a chart with no data costs nothing.</summary>
    public void Clear()
    {
        _chart = null;
        _times = [];
        _series.Clear();
        _legend.Clear();
        _hasSecondScale = false;
        _legendWidth = -1d;
        InvalidateMeasure();
        Redraw();
    }

    private static bool IsBand(ChartData chart, ChartLine line) => chart.Stacked && !line.Overlay;

    // A coordinate that isn't a number can stop WPF's render thread, which takes the whole process with
    // it. A gap is also the honest way to show one.
    private static double?[] Finite(double?[] values)
    {
        if (!values.Any(value => value is { } number && !IsFinite(number)))
            return values;

        return values.Select(value => value is { } number && !IsFinite(number) ? null : value).ToArray();
    }

    private static bool IsFinite(double number) => !double.IsNaN(number) && !double.IsInfinity(number);

    // Bands sit on top of each other, so each is the running total of those before it.
    private static double?[] Total(double?[] values, double?[]? below)
    {
        var total = new double?[values.Length];

        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is { } value)
                total[i] = value + (below?[i] ?? 0d);
        }

        return total;
    }

    /// <summary>Redraws everything, after new data or a zoom.</summary>
    public void Redraw()
    {
        DrawPlot();
        DrawOverlay();
    }

    /// <summary>Redraws only the cursor and legend, after the mouse moved on any chart on the board.</summary>
    public void RedrawCursor() => DrawOverlay();

    protected override Size MeasureOverride(Size availableSize)
    {
        // Nothing to draw takes no room, and the box says why.
        if (_chart == null || _series.Count == 0 || _times.Length == 0)
            return new Size(0d, 0d);

        double width = double.IsInfinity(availableSize.Width) ? 400d : availableSize.Width;
        LayOutLegend(width);
        return new Size(width, BodyHeight + _legendHeight);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Redraw();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Text is drawn by hand, so a theme change has to reach it from here.
        if (e.Property == TextElement.ForegroundProperty)
            Redraw();
    }

    private Brush TextBrush => (Brush)GetValue(TextElement.ForegroundProperty);

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private FormattedText Text(string text, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, s_typeface, TextSize, brush, Dpi);

    private double X(double time) => _left + ((time - _from) / (_to - _from) * _plotWidth);

    private double Y(double value, double top) => _plotBottom - (value / top * (_plotBottom - TopGap));

    // Held in fields, because every point on every line reads these.
    private void WorkOutSize()
    {
        _plotWidth = Math.Max(1d, ActualWidth - _left - (_hasSecondScale ? SecondAxis : RightGap));
        _plotBottom = BodyHeight - BottomAxis;
    }

    private void WorkOutRange()
    {
        if (_times.Length == 0)
            return;

        (_from, _to) = _board.Zoom ?? (_times[0], _times[_times.Length - 1]);

        if (_to <= _from)
            _to = _from + 1d;

        if (_chart!.Maximum is { } maximum)
        {
            _yTop = maximum;
            return;
        }

        double highest = 0d;

        foreach (Series series in _series)
        {
            if (series.Line.SecondScale)
                continue;

            for (int i = 0; i < series.Top.Length && i < _times.Length; i++)
            {
                if (_times[i] >= _from && _times[i] <= _to && series.Top[i] is { } value && value > highest)
                    highest = value;
            }
        }

        _yTop = highest > 0d ? highest * 1.1d : 1d;
    }

    private void DrawPlot()
    {
        using DrawingContext context = _plot.RenderOpen();

        if (_chart == null || _times.Length == 0 || _series.Count == 0 || ActualWidth < 1d)
            return;

        WorkOutSize();
        WorkOutRange();

        // Mouse events only land on what's drawn, and the gaps between lines have to take them too.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0d, 0d, ActualWidth, BodyHeight));

        DrawTimeAxis(context);

        var clip = new RectangleGeometry(new Rect(_left, 0d, _plotWidth, _plotBottom));
        clip.Freeze();
        context.PushClip(clip);

        if (IsTimeline)
        {
            DrawTimeline(context);
        }
        else
        {
            foreach (Series series in _series)
            {
                if (series.Band)
                    DrawBand(context, series);
            }

            foreach (Series series in _series)
                DrawLine(context, series);
        }

        DrawMarkers(context);
        context.Pop();

        if (IsTimeline)
            DrawRowNames(context);
        else
            DrawValueAxes(context);
    }

    private void DrawTimeAxis(DrawingContext context)
    {
        double span = _to - _from;
        double step = s_timeSteps[s_timeSteps.Length - 1];

        foreach (double candidate in s_timeSteps)
        {
            if (span / candidate * SmallestLabelGap <= _plotWidth)
            {
                step = candidate;
                break;
            }
        }

        // Ticks land on round local times, so a label reads 14:30 and not 14:27.
        double offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalSeconds;
        string format = step < 60d ? "HH:mm:ss" : step < 86400d ? "HH:mm" : "MMM d";

        context.PushOpacity(Look.SecondaryOpacity);

        for (double tick = (Math.Ceiling((_from + offset) / step) * step) - offset; tick <= _to; tick += step)
        {
            double x = X(tick);
            context.DrawLine(s_gridPen, new Point(x, TopGap), new Point(x, _plotBottom));

            FormattedText label = Text(DateTimeOffset.FromUnixTimeMilliseconds((long)(tick * 1000d)).ToLocalTime().ToString(format, CultureInfo.CurrentCulture), TextBrush);
            context.DrawText(label, new Point(x - (label.Width / 2d), _plotBottom + 4d));
        }

        context.Pop();
    }

    private void DrawValueAxes(DrawingContext context)
    {
        double step = NiceStep(_yTop, YTicks);

        context.PushOpacity(Look.SecondaryOpacity);

        for (double tick = 0d; tick <= _yTop * 1.0001d; tick += step)
        {
            double y = Y(tick, _yTop);
            context.DrawLine(s_gridPen, new Point(_left, y), new Point(_left + _plotWidth, y));

            FormattedText label = Text(Number(tick, string.Empty), TextBrush);
            context.DrawText(label, new Point(_left - label.Width - 6d, y - (label.Height / 2d)));
        }

        if (_hasSecondScale)
        {
            for (double tick = 0d; tick <= 1.0001d; tick += 0.2d)
            {
                FormattedText label = Text(Look.Num(tick, "0.0"), TextBrush);
                context.DrawText(label, new Point(_left + _plotWidth + 6d, Y(tick, SecondScaleTop) - (label.Height / 2d)));
            }
        }

        context.Pop();
    }

    private static double NiceStep(double span, int ticks)
    {
        double rough = span / ticks;
        double power = Math.Pow(10d, Math.Floor(Math.Log10(rough)));
        double unit = rough / power;
        double nice = unit <= 1d ? 1d : unit <= 2d ? 2d : unit <= 2.5d ? 2.5d : unit <= 5d ? 5d : 10d;
        return nice * power;
    }

    // A band fills between its running total and the one below it, and a gap in the data leaves a gap.
    private void DrawBand(DrawingContext context, Series series)
    {
        var geometry = new StreamGeometry();

        using (StreamGeometryContext path = geometry.Open())
        {
            int index = 0;

            while (NextRun(series.Top, ref index, out int first, out int last))
            {
                path.BeginFigure(new Point(X(_times[first]), Y(series.Top[first]!.Value, _yTop)), isFilled: true, isClosed: true);

                for (int i = first + 1; i <= last; i++)
                    path.LineTo(new Point(X(_times[i]), Y(series.Top[i]!.Value, _yTop)), isStroked: false, isSmoothJoin: false);

                for (int i = last; i >= first; i--)
                    path.LineTo(new Point(X(_times[i]), Y(series.Below?[i] ?? 0d, _yTop)), isStroked: false, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        context.PushOpacity(0.85d);
        context.DrawGeometry(series.Brush, null, geometry);
        context.Pop();
    }

    private void DrawLine(DrawingContext context, Series series)
    {
        double top = series.Line.SecondScale ? SecondScaleTop : _yTop;
        var geometry = new StreamGeometry();
        var alone = new List<Point>();

        using (StreamGeometryContext path = geometry.Open())
        {
            int index = 0;

            while (NextRun(series.Top, ref index, out int first, out int last))
            {
                var start = new Point(X(_times[first]), Y(series.Top[first]!.Value, top));

                // One value between gaps has no line to be on, so it gets a dot, or it would never show.
                if (first == last)
                {
                    alone.Add(start);
                    continue;
                }

                path.BeginFigure(start, isFilled: false, isClosed: false);

                for (int i = first + 1; i <= last; i++)
                    path.LineTo(new Point(X(_times[i]), Y(series.Top[i]!.Value, top)), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        context.DrawGeometry(null, series.Band ? series.ThinPen : series.Pen, geometry);

        foreach (Point point in alone)
            context.DrawEllipse(series.Brush, null, point, 2d, 2d);
    }

    // Finds the next stretch with no gap, inside what's on show.
    private bool NextRun(double?[] values, ref int index, out int first, out int last)
    {
        int count = Math.Min(values.Length, _times.Length);

        while (index < count && !values[index].HasValue)
            index++;

        first = index;

        while (index < count && values[index].HasValue)
            index++;

        last = index - 1;
        return first < count;
    }

    // One row per name, drawn wherever that row has a value. The first name is the most online, so it goes on top.
    private void DrawTimeline(DrawingContext context)
    {
        for (int row = 0; row < _series.Count; row++)
        {
            Series series = _series[row];
            double y = TopGap + ((row + 0.5d) * RowHeight);
            int index = 0;

            while (NextRun(series.Top, ref index, out int first, out int last))
            {
                if (first == last)
                    context.DrawEllipse(series.Brush, null, new Point(X(_times[first]), y), 4.5d, 4.5d);
                else
                    context.DrawLine(series.RowPen, new Point(X(_times[first]), y), new Point(X(_times[last]), y));
            }
        }
    }

    private void DrawRowNames(DrawingContext context)
    {
        context.PushOpacity(Look.SecondaryOpacity);

        for (int row = 0; row < _chart!.Rows!.Count; row++)
        {
            FormattedText name = Text(_chart.Rows[row], TextBrush);
            name.MaxTextWidth = TimelineAxis - 8d;
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(name, new Point(0d, TopGap + ((row + 0.5d) * RowHeight) - (name.Height / 2d)));
        }

        context.Pop();
    }

    // Dashed lines for saves, restarts and plugin or mod changes, the same on every chart on the board.
    private void DrawMarkers(DrawingContext context)
    {
        foreach (ChartMarker marker in _board.Markers)
        {
            if (marker.Time < _from || marker.Time > _to)
                continue;

            var pen = new Pen(ChartPaints.Of(marker.Color), 1d) { DashStyle = s_markerDash };
            pen.Freeze();
            double x = X(marker.Time);
            context.DrawLine(pen, new Point(x, TopGap), new Point(x, _plotBottom));
        }
    }

    // A timeline names its rows on the axis, so only charts have a legend.
    private void LayOutLegend(double width)
    {
        if (Math.Abs(width - _legendWidth) < 1d)
            return;

        _legendWidth = width;
        _legend.Clear();
        _legendHeight = 0d;

        if (IsTimeline || _series.Count == 0)
            return;

        // Room for a figure, so a row doesn't jump around as values change.
        double figure = Text("0,000.00 ms/f", Brushes.Black).WidthIncludingTrailingWhitespace;
        double x = 0d;
        double y = 0d;

        foreach (Series series in _series)
        {
            string name = series.Line.Name + ":";
            double slot = 14d + Text(name, Brushes.Black).WidthIncludingTrailingWhitespace + 4d + figure;

            if (x > 0d && x + slot > width)
            {
                x = 0d;
                y += LegendRow;
            }

            _legend.Add(new LegendEntry(series, name, x, y));
            x += slot;
        }

        _legendHeight = y + LegendRow + 4d;
    }

    private void DrawOverlay()
    {
        using DrawingContext context = _overlay.RenderOpen();

        if (_chart == null || _times.Length == 0 || _series.Count == 0 || ActualWidth < 1d)
            return;

        WorkOutSize();

        int? cursor = _board.Cursor is { } time && time >= _from && time <= _to ? NearestIndex(time) : null;

        if (cursor is { } index && !IsTimeline)
            DrawCursor(context, index);

        if (_dragStart is { } start)
        {
            double left = Math.Min(start, _dragNow);
            context.DrawRectangle(Look.Surface, null, new Rect(left, TopGap, Math.Abs(_dragNow - start), _plotBottom - TopGap));
        }

        LayOutLegend(ActualWidth);
        DrawLegend(context, cursor);
    }

    private void DrawCursor(DrawingContext context, int index)
    {
        double x = X(_times[index]);
        context.DrawLine(s_gridPen, new Point(x, TopGap), new Point(x, _plotBottom));

        foreach (Series series in _series)
        {
            if (index < series.Top.Length && series.Top[index] is { } value)
                context.DrawEllipse(series.Brush, null, new Point(x, Y(value, series.Line.SecondScale ? SecondScaleTop : _yTop)), 3d, 3d);
        }

        string when = DateTimeOffset.FromUnixTimeMilliseconds((long)(_times[index] * 1000d)).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        FormattedText label = Text(when, TextBrush);
        context.DrawRectangle(Look.Surface, null, new Rect(_left + 4d, TopGap, label.Width + 8d, label.Height + 2d));
        context.DrawText(label, new Point(_left + 8d, TopGap + 1d));
    }

    private void DrawLegend(DrawingContext context, int? cursor)
    {
        double top = BodyHeight + 2d;

        foreach (LegendEntry entry in _legend)
        {
            Series series = entry.Series;
            context.DrawRectangle(series.Brush, null, new Rect(entry.X, top + entry.Y + 3d, 10d, 10d));

            // A band holds a running total, so this reads the line's own value.
            double? value = cursor is { } index ? At(series.Line.Values, index) : Latest(series.Line.Values);
            string text = entry.Name + " " + Number(value, series.Line.Unit ?? _chart!.Unit);

            context.DrawText(Text(text, TextBrush), new Point(entry.X + 14d, top + entry.Y));
        }
    }

    private static double? At(double?[] values, int index) => index < values.Length ? values[index] : null;

    private static double? Latest(double?[] values)
    {
        for (int i = values.Length - 1; i >= 0; i--)
        {
            if (values[i].HasValue)
                return values[i];
        }

        return null;
    }

    // Same decimals as the page: fewer as a number grows.
    private static string Number(double? value, string unit)
    {
        if (value is not { } number)
            return "-";

        double size = Math.Abs(number);
        return number.ToString(size >= 100d ? "N0" : size >= 10d ? "N1" : "N2", CultureInfo.CurrentCulture) + unit;
    }

    private int NearestIndex(double time)
    {
        int found = Array.BinarySearch(_times, time);

        if (found >= 0)
            return found;

        int after = ~found;

        if (after == 0)
            return 0;

        if (after >= _times.Length)
            return _times.Length - 1;

        return time - _times[after - 1] < _times[after] - time ? after - 1 : after;
    }

    private double TimeAt(double x) => _from + ((x - _left) / _plotWidth * (_to - _from));

    private bool InsidePlot(Point point) => point.X >= _left && point.X <= _left + _plotWidth && point.Y <= _plotBottom;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_times.Length == 0)
            return;

        Point point = e.GetPosition(this);

        if (_dragStart != null)
        {
            _dragNow = Math.Max(_left, Math.Min(_left + _plotWidth, point.X));
            DrawOverlay();
        }

        _board.MoveCursor(InsidePlot(point) ? TimeAt(point.X) : null);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _board.MoveCursor(null);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (_times.Length == 0)
            return;

        // A double click goes back to the whole range, and live updates follow the latest data again.
        if (e.ClickCount == 2)
        {
            _board.ZoomOut();
            return;
        }

        Point point = e.GetPosition(this);

        if (!InsidePlot(point))
            return;

        _dragStart = point.X;
        _dragNow = point.X;
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_dragStart is not { } start)
            return;

        _dragStart = null;
        ReleaseMouseCapture();

        if (Math.Abs(_dragNow - start) >= DragToZoom)
            _board.ZoomTo(TimeAt(Math.Min(start, _dragNow)), TimeAt(Math.Max(start, _dragNow)));
        else
            DrawOverlay();
    }

    private readonly record struct LegendEntry(Series Series, string Name, double X, double Y);

    private sealed class Series
    {
        public Series(ChartLine line, Brush brush, bool band, double?[] top, double?[]? below)
        {
            Line = line;
            Brush = brush;
            Band = band;
            Top = top;
            Below = below;

            Pen = new Pen(brush, 1.5d) { LineJoin = PenLineJoin.Round };

            if (line.Dashed)
                Pen.DashStyle = s_dashed;

            Pen.Freeze();

            ThinPen = new Pen(brush, 1d);
            ThinPen.Freeze();

            RowPen = new Pen(brush, 10d);
            RowPen.Freeze();
        }

        public ChartLine Line { get; }

        public Brush Brush { get; }

        public bool Band { get; }

        /// <summary>What's drawn: the running total for a band, the value otherwise.</summary>
        public double?[] Top { get; }

        public double?[]? Below { get; }

        public Pen Pen { get; }

        public Pen ThinPen { get; }

        public Pen RowPen { get; }
    }
}
