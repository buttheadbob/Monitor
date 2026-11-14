using System.Windows;
using System.Windows.Controls;

namespace SenX.Monitor.Torch.Ui;

/// <summary>Columns that fit the width, with a child able to take a whole row.</summary>
public sealed class FlowGrid : Panel
{
    public static readonly DependencyProperty WideProperty = DependencyProperty.RegisterAttached(
        "Wide",
        typeof(bool),
        typeof(FlowGrid),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public FlowGrid(double smallestColumn = 420d, double gap = 10d)
    {
        SmallestColumn = smallestColumn;
        Gap = gap;
    }

    public double SmallestColumn { get; }

    public double Gap { get; }

    public static void SetWide(UIElement element, bool wide) => element.SetValue(WideProperty, wide);

    public static bool GetWide(UIElement element) => (bool)element.GetValue(WideProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? SmallestColumn : availableSize.Width;
        double height = Lay(width, measuring: true);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Lay(finalSize.Width, measuring: false);
        return finalSize;
    }

    // Measuring and arranging walk the same rows, so they can't disagree about where anything goes.
    private double Lay(double width, bool measuring)
    {
        int columns = Math.Max(1, (int)((width + Gap) / (SmallestColumn + Gap)));
        double cell = (width - (Gap * (columns - 1))) / columns;
        var row = new List<UIElement>(columns);
        double top = 0d;

        void Finish()
        {
            if (row.Count == 0)
                return;

            double tallest = 0d;
            double slot = row.Count == 1 && GetWide(row[0]) ? width : cell;

            foreach (UIElement child in row)
            {
                if (measuring)
                    child.Measure(new Size(slot, double.PositiveInfinity));

                tallest = Math.Max(tallest, child.DesiredSize.Height);
            }

            if (!measuring)
            {
                for (int i = 0; i < row.Count; i++)
                    row[i].Arrange(new Rect(i * (slot + Gap), top, slot, tallest));
            }

            top += tallest + Gap;
            row.Clear();
        }

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            if (GetWide(child))
                Finish();

            row.Add(child);

            if (GetWide(child) || row.Count == columns)
                Finish();
        }

        Finish();
        return Math.Max(0d, top - Gap);
    }
}

/// <summary>A small figure with its name over it.</summary>
public sealed class Tile : Border
{
    private readonly TextBlock _value = new() { FontSize = 20d, FontWeight = FontWeights.SemiBold };

    public Tile(string label, string? tip = null)
    {
        Background = Look.Surface;
        BorderBrush = Look.Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5);
        Padding = new Thickness(11, 8, 11, 8);
        Margin = new Thickness(0, 0, 10, 10);
        MinWidth = 110d;
        ToolTip = tip;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, Opacity = Look.SecondaryOpacity });
        stack.Children.Add(_value);
        Child = stack;
    }

    public string Value
    {
        set
        {
            if (_value.Text != value)
                _value.Text = value;
        }
    }
}

/// <summary>Something an admin should read. It takes no room while it has nothing to say.</summary>
public sealed class Notice : Border
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };

    public Notice()
    {
        Background = Look.Surface;
        BorderBrush = Look.Info;
        BorderThickness = new Thickness(3, 0, 0, 0);
        Padding = new Thickness(11, 7, 11, 7);
        Margin = new Thickness(0, 0, 0, 10);
        Visibility = Visibility.Collapsed;
        Child = _text;
    }

    /// <summary>What it says, or null to go away.</summary>
    public string? Text
    {
        set
        {
            if (value != null && _text.Text != value)
                _text.Text = value;

            Visibility visibility = Look.VisibleIf(value != null);

            if (Visibility != visibility)
                Visibility = visibility;
        }
    }
}

/// <summary>A box for a number. What can't be read as one leaves the value as it was.</summary>
public sealed class NumberBox : TextBox
{
    private double _value;

    public NumberBox(double value, double width = 70d)
    {
        Width = width;
        VerticalContentAlignment = VerticalAlignment.Center;
        Number = value;
    }

    public double Number
    {
        get
        {
            if (double.TryParse(Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double read))
                _value = read;

            return _value;
        }

        set
        {
            _value = value;
            Text = Look.Num(value, "0.##");
        }
    }

    public int Whole => (int)Math.Round(Number);
}
