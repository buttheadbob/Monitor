using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui;

/// <summary>What a board shows. <see cref="From"/> and <see cref="To"/> pick a stretch of the past instead of the latest minutes.</summary>
public sealed record ChartRequest(string View, string ServerId, int Minutes, HistoryDimension List = HistoryDimension.Grids, string? Mod = null, DateTime? From = null, DateTime? To = null);

/// <summary>
/// The charts on one tab. They share a cursor, a zoom and their dashed markers, and they fetch together.
/// Charts are built off the UI thread, and a fetch is skipped while the last is still going, so a long
/// range reading files can't pile up. Building charts allocates, and on Torch it does so in the game's
/// process, so nothing here runs unless the tab is on screen.
/// </summary>
public sealed class ChartBoard(MonitorServices services)
{
    // A page in a browser asks every second. This is slower because it's built inside the game's process.
    private const int ShortRangeRefreshSeconds = 2;
    private const int LongRangeRefreshSeconds = 30;
    private const int LongRangeMinutes = 60;

    private readonly List<ChartBox> _boxes = [];
    private ChartRequest? _request;
    private bool _busy;
    private int _nextRefresh;

    public double? Cursor { get; private set; }

    /// <summary>The stretch zoomed in on, from dragging across any chart. Null shows everything.</summary>
    public (double From, double To)? Zoom { get; private set; }

    public IReadOnlyList<ChartMarker> Markers { get; private set; } = [];

    /// <summary>A titled chart that belongs to this board.</summary>
    public ChartBox Chart(string id, string title, bool wide = false)
    {
        var box = new ChartBox(this, id, title);
        FlowGrid.SetWide(box, wide);
        _boxes.Add(box);
        return box;
    }

    /// <summary>Sets what's on show. Asking for what's already there changes nothing, so a tab can call this freely.</summary>
    public void Show(ChartRequest request)
    {
        if (request == _request)
            return;

        _request = request;
        Zoom = null;
        Markers = [];

        foreach (ChartBox box in _boxes)
            box.Clear();

        Refresh();
    }

    /// <summary>Stops fetching and lets go of what's drawn, for charts that are no longer on show.</summary>
    public void Hide()
    {
        _request = null;
        Zoom = null;
        Markers = [];

        foreach (ChartBox box in _boxes)
            box.Clear();
    }

    /// <summary>Called each second while the tab is on show and live.</summary>
    public void Tick(int second)
    {
        // A fixed stretch of the past isn't going to change.
        if (_request == null || _request.To != null || second < _nextRefresh)
            return;

        _nextRefresh = second + (_request.Minutes > LongRangeMinutes ? LongRangeRefreshSeconds : ShortRangeRefreshSeconds);
        Refresh();
    }

    public void MoveCursor(double? time)
    {
        if (time == Cursor)
            return;

        Cursor = time;

        foreach (ChartBox box in _boxes)
            box.Chart.RedrawCursor();
    }

    public void ZoomTo(double from, double to)
    {
        Zoom = (from, to);
        Redraw();
    }

    public void ZoomOut()
    {
        if (Zoom == null)
            return;

        Zoom = null;
        Redraw();
    }

    private void Redraw()
    {
        foreach (ChartBox box in _boxes)
            box.Chart.Redraw();
    }

    // An event handler in all but name, so nothing may escape it.
    private async void Refresh()
    {
        if (_busy || _request is not { } request)
            return;

        _busy = true;

        try
        {
            ChartsResponse charts = await Task.Run(() => Build(request));

            if (request == _request)
                Apply(charts);
        }
        catch (Exception exception)
        {
            MonitorLogSink.Write?.Invoke("Monitor could not build its charts.", exception);
        }
        finally
        {
            _busy = false;
        }

        // What's wanted changed while that was building, and a fixed stretch gets no second chance from the clock.
        if (_request != null && request != _request)
            Refresh();
    }

    private ChartsResponse Build(ChartRequest request)
    {
        if (request.From is { } from && request.To is { } to)
        {
            // A fixed stretch of the past is limited to what's kept.
            DateTime oldest = DateTime.UtcNow - services.Settings.Retention;
            return services.Charts.Build(request.View, request.ServerId, from < oldest ? oldest : from, to, request.List, request.Mod);
        }

        int limit = services.Settings.RetentionDays * 24 * 60;
        return services.Charts.Build(request.View, request.ServerId, Math.Clamp(request.Minutes, 1, limit), request.List, request.Mod);
    }

    private void Apply(ChartsResponse charts)
    {
        Markers = charts.Markers;

        foreach (ChartData chart in charts.Charts)
        {
            foreach (ChartBox box in _boxes)
            {
                if (box.Id == chart.Id)
                    box.Show(chart);
            }
        }
    }
}

/// <summary>A chart with its title, and a line saying so when there's nothing to draw.</summary>
public sealed class ChartBox : Border
{
    private readonly TextBlock _empty = new() { Text = "No data for this range yet.", Opacity = Look.SecondaryOpacity, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _title;

    public ChartBox(ChartBoard board, string id, string title)
    {
        Id = id;
        Chart = new ChartView(board);

        Background = Look.Surface;
        BorderBrush = Look.Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5);
        Padding = new Thickness(11, 8, 11, 8);

        _title = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis };

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(Chart);
        stack.Children.Add(_empty);
        Child = stack;
    }

    /// <summary>Matches a chart in the data.</summary>
    public string Id { get; }

    public ChartView Chart { get; }

    public string Title
    {
        set => _title.Text = value;
    }

    public void Show(ChartData chart) => _empty.Visibility = Look.VisibleIf(!Chart.Show(chart));

    public void Clear()
    {
        Chart.Clear();
        _empty.Visibility = Visibility.Visible;
    }
}
