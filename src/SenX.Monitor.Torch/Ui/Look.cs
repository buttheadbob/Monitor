using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SenX.Monitor.Torch.Ui;

/// <summary>
/// How everything looks, built in code rather than XAML so the compiler checks every name.
/// Torch shows a plugin inside a Frame, which stops text colour being inherited, and it rewrites the
/// control's merged dictionaries on each theme change. So colours are either read from theme keys, which
/// are missing on Torch's own theme and fall back to the system's, or a grey that suits light and dark.
/// </summary>
public static class Look
{
    /// <summary>MahApps inverts this one on dark themes, so it's always the text colour.</summary>
    public const string TextKey = "BlackBrush";

    public const string BackgroundKey = "WhiteBrush";

    public const double SecondaryOpacity = 0.72d;

    public static readonly Brush Surface = Frozen(0x16, 0x80, 0x80, 0x80);

    public static readonly Brush Line = Frozen(0x48, 0x80, 0x80, 0x80);

    public static readonly Brush Info = Frozen(0xFF, 0x56, 0xC8, 0xDC);

    private static readonly Thickness s_rowGap = new(0, 0, 14, 6);

    public static Brush Frozen(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    public static string Num(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    public static string Num(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "-";

    /// <summary>The page's own heading size, for a section.</summary>
    public static TextBlock Heading(string text = "") => new()
    {
        Text = text,
        FontSize = 15d,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 10, 0, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Quiet text that explains what's nearby.</summary>
    public static TextBlock Hint(string text = "") => new()
    {
        Text = text,
        Opacity = SecondaryOpacity,
        Margin = new Thickness(0, 0, 0, 8),
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Label(string text) => new()
    {
        Text = text,
        Opacity = SecondaryOpacity,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0),
    };

    public static CheckBox Check(string text, bool on = false, string? tip = null)
    {
        var box = new CheckBox { Content = text, IsChecked = on, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        box.SetResourceReference(Control.ForegroundProperty, TextKey);
        return box;
    }

    public static Button Action(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center };
        button.Click += click;
        return button;
    }

    public static TextBox Input(double width = 160d, string text = "") => new()
    {
        Width = width,
        Text = text,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>A label and its control, kept together when a row wraps.</summary>
    public static StackPanel Field(string label, FrameworkElement control, string? tip = null)
    {
        var field = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = tip };
        field.Children.Add(Label(label));
        field.Children.Add(control);
        return field;
    }

    /// <summary>Controls side by side, wrapping when the window is narrow.</summary>
    public static WrapPanel Row(params UIElement[] items)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };

        foreach (UIElement item in items)
        {
            if (item is FrameworkElement element)
                element.Margin = s_rowGap;

            row.Children.Add(item);
        }

        return row;
    }

    /// <summary>A group of tiles.</summary>
    public static WrapPanel Tiles(params UIElement[] tiles)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };

        foreach (UIElement tile in tiles)
            row.Children.Add(tile);

        return row;
    }

    /// <summary>Things stacked down the page.</summary>
    public static StackPanel Stack(params UIElement[] items)
    {
        var stack = new StackPanel();

        foreach (UIElement item in items)
            stack.Children.Add(item);

        return stack;
    }

    /// <summary>A titled part of a page, for use in a <see cref="FlowGrid"/>.</summary>
    public static StackPanel Section(string title, params UIElement[] items)
    {
        StackPanel section = Stack(Heading(title));

        foreach (UIElement item in items)
            section.Children.Add(item);

        return section;
    }

    /// <summary>Puts text on the clipboard. Another program can hold it for a moment, and nothing is lost if so.</summary>
    public static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // The text is still on the page.
        }
    }

    public static Visibility VisibleIf(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
