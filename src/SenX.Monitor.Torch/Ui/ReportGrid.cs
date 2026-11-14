using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace SenX.Monitor.Torch.Ui;

/// <summary>
/// A plain table of rows. It shows every row and leaves scrolling to the page, and it only rebuilds when
/// its rows really changed, because most lists change once in 30 seconds and this runs in the game's process.
/// </summary>
public sealed class ReportGrid : DataGrid
{
    private static readonly Thickness s_cellPadding = new(6, 3, 6, 3);
    private static readonly Style s_cell = BuildCell();
    private static readonly Style s_header = BuildHeader();
    private static readonly Style s_numberHeader = BuildNumberHeader();
    private static readonly Style s_text = BuildText(TextAlignment.Left);
    private static readonly Style s_number = BuildText(TextAlignment.Right);

    private System.Collections.IEnumerable? _rows;

    public ReportGrid()
    {
        AutoGenerateColumns = false;
        IsReadOnly = true;
        CanUserAddRows = false;
        CanUserDeleteRows = false;
        CanUserResizeRows = false;
        CanUserReorderColumns = false;
        HeadersVisibility = DataGridHeadersVisibility.Column;
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        HorizontalGridLinesBrush = Look.Line;
        Background = System.Windows.Media.Brushes.Transparent;
        RowBackground = System.Windows.Media.Brushes.Transparent;
        AlternatingRowBackground = null;
        BorderThickness = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        SelectionMode = DataGridSelectionMode.Single;
        CellStyle = s_cell;
        ColumnHeaderStyle = s_header;
        RowHeaderWidth = 0d;
        Margin = new Thickness(0, 0, 0, 8);
        SetResourceReference(ForegroundProperty, Look.TextKey);
    }

    private static Style BuildCell()
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FocusVisualStyleProperty, null));

        // Rows aren't chosen for anything, so selecting one shouldn't paint it. The colour comes from the
        // table rather than a theme key, because Torch's own theme has no such key, and the default
        // here is white text for a highlight that's no longer there.
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        selected.Setters.Add(new Setter(BorderBrushProperty, System.Windows.Media.Brushes.Transparent));
        selected.Setters.Add(new Setter(ForegroundProperty, TableForeground()));
        style.Triggers.Add(selected);
        return style;
    }

    private static Binding TableForeground() =>
        new(nameof(Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1) };

    // The default cell has no padding to set, so the space goes on the text.
    private static Style BuildText(TextAlignment alignment)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, alignment));
        style.Setters.Add(new Setter(MarginProperty, s_cellPadding));
        return style;
    }

    private static Style BuildHeader()
    {
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        style.Setters.Add(new Setter(ForegroundProperty, TableForeground()));
        style.Setters.Add(new Setter(BorderBrushProperty, Look.Line));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(new Setter(PaddingProperty, s_cellPadding));
        return style;
    }

    private static Style BuildNumberHeader()
    {
        var style = new Style(typeof(DataGridColumnHeader), s_header);
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
        return style;
    }

    /// <summary>A column of text that takes up what's spare. Most tables have one, for the name.</summary>
    public ReportGrid Text(string header, string property, bool stretch = true)
    {
        Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(property),
            ElementStyle = s_text,
            Width = stretch ? new DataGridLength(1d, DataGridLengthUnitType.Star) : DataGridLength.Auto,
            SortMemberPath = property,
        });

        return this;
    }

    /// <summary>A number. It stays a number underneath, so clicking the header sorts it properly.</summary>
    public ReportGrid Number(string header, string property, string format = "0.00")
    {
        Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(property) { StringFormat = format, TargetNullValue = "-" },
            ElementStyle = s_number,
            HeaderStyle = s_numberHeader,
            Width = DataGridLength.Auto,
            SortMemberPath = property,
        });

        return this;
    }

    /// <summary>A button on each row, which gets that row.</summary>
    public ReportGrid Button<T>(string text, Action<T> click, string? onlyWhen = null) where T : class
    {
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(ContentControl.ContentProperty, text);
        button.SetValue(PaddingProperty, new Thickness(7, 1, 7, 1));
        button.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is FrameworkElement { DataContext: T row })
                click(row);
        }));

        if (onlyWhen != null)
            button.SetBinding(VisibilityProperty, new Binding(onlyWhen));

        Columns.Add(new DataGridTemplateColumn
        {
            CellTemplate = new DataTemplate { VisualTree = button },
            Width = DataGridLength.Auto,
            CanUserSort = false,
        });

        return this;
    }

    /// <summary>A name with a small button before it that opens or closes something.</summary>
    public ReportGrid Toggled<T>(string header, string name, string glyph, Action<T> click, string? indent = null, string? toggleShown = null) where T : class
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        if (indent != null)
            panel.SetBinding(MarginProperty, new Binding(indent));

        var toggle = new FrameworkElementFactory(typeof(Button));
        toggle.SetValue(WidthProperty, 18d);
        toggle.SetValue(HeightProperty, 18d);
        toggle.SetValue(PaddingProperty, new Thickness(0));
        toggle.SetValue(MarginProperty, new Thickness(0, 0, 4, 0));
        toggle.SetBinding(ContentControl.ContentProperty, new Binding(glyph));
        toggle.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is FrameworkElement { DataContext: T row })
                click(row);
        }));

        if (toggleShown != null)
            toggle.SetBinding(VisibilityProperty, new Binding(toggleShown));

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(name));
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

        panel.AppendChild(toggle);
        panel.AppendChild(text);
        return Custom(header, panel, stretch: true);
    }

    /// <summary>A tick box on each row. The handler gets the row and the new state, and false puts the box back.</summary>
    public ReportGrid Tick<T>(string chosen, Func<T, bool, bool> changed) where T : class
    {
        var box = new FrameworkElementFactory(typeof(CheckBox));
        box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(chosen) { Mode = BindingMode.OneWay });
        box.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is CheckBox { DataContext: T row } check && !changed(row, check.IsChecked == true))
                check.IsChecked = !check.IsChecked;
        }));

        return Custom(string.Empty, box);
    }

    /// <summary>A column built by the caller, for anything the others don't do.</summary>
    public ReportGrid Custom(string header, FrameworkElementFactory cell, string? sortBy = null, bool stretch = false)
    {
        Columns.Add(new DataGridTemplateColumn
        {
            Header = header,
            CellTemplate = new DataTemplate { VisualTree = cell },
            Width = stretch ? new DataGridLength(1d, DataGridLengthUnitType.Star) : DataGridLength.Auto,
            SortMemberPath = sortBy,
            CanUserSort = sortBy != null,
        });

        return this;
    }

    /// <summary>Shows these rows, keeping any sort an admin chose. Rows that compare equal aren't rebuilt.</summary>
    public void Show<T>(IReadOnlyList<T> rows)
    {
        if (_rows is IReadOnlyList<T> shown && shown.SequenceEqual(rows))
            return;

        SortDescription[] sorts = Items.SortDescriptions.ToArray();

        _rows = rows;
        ItemsSource = rows;

        foreach (SortDescription sort in sorts)
        {
            Items.SortDescriptions.Add(sort);

            foreach (DataGridColumn column in Columns)
            {
                if (column.SortMemberPath == sort.PropertyName)
                    column.SortDirection = sort.Direction;
            }
        }
    }

    // The table holds its own scroller, which would eat the wheel even with nothing to scroll.
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);

        if (e.Handled || Parent is not UIElement parent)
            return;

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
    }
}
