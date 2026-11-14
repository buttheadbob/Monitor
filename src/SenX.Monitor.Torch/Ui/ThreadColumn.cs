using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using SenX.Monitor.Components.Charts;

namespace SenX.Monitor.Torch.Ui;

/// <summary>
/// Which threads a row ran on: a word, and a bar of game thread time against worker time, as on Quasar's page.
/// Rows need a <c>Thread</c> word, empty when there's nothing to say, and a <c>MainPercent</c>.
/// </summary>
internal static class ThreadColumn
{
    public const string ThreadProperty = "Thread";
    public const string MainPercentProperty = "MainPercent";

    private static readonly IValueConverter s_shownWhenSet = new ShownWhenSet();

    public static ReportGrid Add(ReportGrid table)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 3, 6, 3));

        var word = new FrameworkElementFactory(typeof(TextBlock));
        word.SetBinding(TextBlock.TextProperty, new Binding(ThreadProperty));
        word.SetValue(FrameworkElement.MinWidthProperty, 48d);
        word.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(word);

        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetBinding(RangeBase.ValueProperty, new Binding(MainPercentProperty) { Mode = BindingMode.OneWay });
        bar.SetBinding(UIElement.VisibilityProperty, new Binding(ThreadProperty) { Converter = s_shownWhenSet });
        bar.SetValue(RangeBase.MaximumProperty, 100d);
        bar.SetValue(FrameworkElement.WidthProperty, 48d);
        bar.SetValue(FrameworkElement.HeightProperty, 6d);
        bar.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bar.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        bar.SetValue(Control.ForegroundProperty, ChartPaints.Of(ChartColors.Session));
        bar.SetValue(Control.BackgroundProperty, ChartPaints.Of(ChartColors.Join));
        bar.SetValue(FrameworkElement.ToolTipProperty, "Game thread against worker time");
        panel.AppendChild(bar);

        return table.Custom("Thread", panel, ThreadProperty);
    }

    private sealed class ShownWhenSet : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string text && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
