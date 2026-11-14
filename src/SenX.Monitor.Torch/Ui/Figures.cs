using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SenX.Monitor.Components.Charts;

namespace SenX.Monitor.Torch.Ui;

/// <summary>A titled frame, the same as a chart's, for a figure that isn't one.</summary>
public sealed class FigureBox : Border
{
    private readonly TextBlock _heading;

    public FigureBox(string title, UIElement figure)
    {
        Background = Look.Surface;
        BorderBrush = Look.Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5);
        Padding = new Thickness(11, 8, 11, 8);

        _heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

        var stack = new StackPanel();
        stack.Children.Add(_heading);
        stack.Children.Add(figure);
        Child = stack;
    }

    public string Title
    {
        set => _heading.Text = value;
    }
}

/// <summary>Shares of a whole, with a legend beside the ring. Small slices are left out, so it shows what matters.</summary>
public sealed class Donut : FrameworkElement
{
    private const double RingSize = 160d;
    private const double RingThickness = 26d;
    private const double RowHeight = 18d;
    private const double LegendGap = 14d;
    private const double TextSize = 12d;

    private static readonly Typeface s_typeface = new("Segoe UI");

    private readonly string _unit;
    private readonly double _smallest;
    private List<ChartSlice> _slices = [];

    public Donut(string unit = " ms", double smallest = 0.1d)
    {
        _unit = unit;
        _smallest = smallest;
    }

    /// <summary>Puts the slices in a frame with a title.</summary>
    public FigureBox InBox(string title) => new(title, this);

    public void Show(IEnumerable<ChartSlice> slices)
    {
        List<ChartSlice> shown = slices.Where(slice => slice.Value >= _smallest).OrderByDescending(slice => slice.Value).ToList();

        if (shown.SequenceEqual(_slices))
            return;

        _slices = shown;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? RingSize : availableSize.Width;
        return new Size(width, Math.Max(RingSize, _slices.Count * RowHeight));
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == TextElement.ForegroundProperty)
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        Brush text = (Brush)GetValue(TextElement.ForegroundProperty);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_slices.Count == 0)
        {
            context.PushOpacity(Look.SecondaryOpacity);
            context.DrawText(new FormattedText("No data yet.", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, s_typeface, TextSize, text, dpi), new Point(0d, 0d));
            context.Pop();
            return;
        }

        double total = Math.Max(_slices.Sum(slice => slice.Value), double.Epsilon);
        double angle = -90d;
        var center = new Point(RingSize / 2d, RingSize / 2d);
        double radius = (RingSize - RingThickness) / 2d;

        foreach (ChartSlice slice in _slices)
        {
            double sweep = slice.Value / total * 360d;
            var pen = new Pen(ChartPaints.Of(slice.Color), RingThickness);
            pen.Freeze();

            // An arc can't go all the way round, so one slice takes the whole ring as a circle.
            if (sweep >= 359.99d)
                context.DrawEllipse(null, pen, center, radius, radius);
            else
                context.DrawGeometry(null, pen, Arc(center, radius, angle, sweep));

            angle += sweep;
        }

        double top = 0d;

        foreach (ChartSlice slice in _slices)
        {
            context.DrawRectangle(ChartPaints.Of(slice.Color), null, new Rect(RingSize + LegendGap, top + 4d, 10d, 10d));

            string line = slice.Name + "  " + slice.Value.ToString("0.00", CultureInfo.InvariantCulture) + _unit;
            var label = new FormattedText(line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, s_typeface, TextSize, text, dpi)
            {
                MaxTextWidth = Math.Max(1d, ActualWidth - RingSize - LegendGap - 15d),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };

            context.DrawText(label, new Point(RingSize + LegendGap + 15d, top));
            top += RowHeight;
        }
    }

    internal static StreamGeometry Arc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();

        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(OnCircle(center, radius, startDegrees), isFilled: false, isClosed: false);
            path.ArcTo(
                OnCircle(center, radius, startDegrees + sweepDegrees),
                new Size(radius, radius),
                0d,
                sweepDegrees > 180d,
                SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point OnCircle(Point center, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180d;
        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }
}

/// <summary>A half circle showing a value from 0 to its maximum: green when healthy, amber, then red.</summary>
public sealed class Gauge : FrameworkElement
{
    private const double GaugeWidth = 150d;
    private const double GaugeHeight = 86d;
    private const double Radius = 62d;
    private const double RingWidth = 12d;

    private static readonly Typeface s_typeface = new("Segoe UI");
    private static readonly Pen s_track = FrozenPen(Look.Line);

    private double _value = double.NaN;

    /// <summary>Puts the gauge in a frame with a title.</summary>
    public FigureBox InBox(string title) => new(title, this) { Margin = new Thickness(0, 0, 10, 10) };

    public double Value
    {
        set
        {
            if (value.Equals(_value))
                return;

            _value = value;
            InvalidateVisual();
        }
    }

    private static Pen FrozenPen(Brush brush)
    {
        var pen = new Pen(brush, RingWidth) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();
        return pen;
    }

    protected override Size MeasureOverride(Size availableSize) => new(GaugeWidth, GaugeHeight);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == TextElement.ForegroundProperty)
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        var center = new Point(GaugeWidth / 2d, GaugeHeight - 8d);
        double value = double.IsNaN(_value) ? 0d : _value;
        double share = Math.Max(0d, Math.Min(1d, value));

        context.DrawGeometry(null, s_track, Donut.Arc(center, Radius, 180d, 180d));

        if (share > 0d)
        {
            string color = share >= 0.9d ? ChartColors.Good : share >= 0.6d ? ChartColors.Network : ChartColors.Bad;
            context.DrawGeometry(null, FrozenPen(ChartPaints.Of(color)), Donut.Arc(center, Radius, 180d, share * 180d));
        }

        var text = new FormattedText(
            value.ToString("0.00", CultureInfo.InvariantCulture),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            s_typeface,
            20d,
            (Brush)GetValue(TextElement.ForegroundProperty),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        context.DrawText(text, new Point(center.X - (text.Width / 2d), center.Y - text.Height));
    }
}
