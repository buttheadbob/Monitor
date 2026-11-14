using System.Windows;
using System.Windows.Controls;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>One tab. It scrolls for itself, and it's only called while it's the one on screen.</summary>
public abstract class MonitorTab : ContentControl
{
    private readonly List<ChartBoard> _boards = [];

    protected MonitorTab(MonitorView page)
    {
        Page = page;
        Services = page.Services;

        // A tab's content inherits from its TabItem, whose own style sets black text on every theme.
        SetResourceReference(ForegroundProperty, Look.TextKey);

        Body = new StackPanel { Margin = new Thickness(2, 8, 10, 10) };
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = Body,
        };
    }

    protected MonitorView Page { get; }

    protected MonitorServices Services { get; }

    protected StackPanel Body { get; }

    protected string ServerId => Page.Server.HistoryId;

    protected int RangeMinutes => Page.RangeMinutes;

    /// <summary>How often its tiles and tables refresh. Zero leaves it to refresh itself.</summary>
    protected virtual int RefreshSeconds => 2;

    /// <summary>Called when it comes on screen, or what's asked of it changed.</summary>
    public virtual void Shown() => Refresh();

    /// <summary>Reads the latest figures into its tiles and tables.</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>Called each second while live.</summary>
    public void Second(int second)
    {
        foreach (ChartBoard board in _boards)
            board.Tick(second);
    }

    /// <summary>Whether it's time for <see cref="Refresh"/>.</summary>
    public bool Refreshes(int second) => RefreshSeconds > 0 && second % RefreshSeconds == 0;

    protected ChartBoard NewBoard()
    {
        var board = new ChartBoard(Services);
        _boards.Add(board);
        return board;
    }

    protected void Add(params UIElement[] items)
    {
        foreach (UIElement item in items)
            Body.Children.Add(item);
    }

    /// <summary>Runs an action that talks to the server, saying what went wrong rather than letting it escape.</summary>
    protected async void Guarded(Func<Task> action, Action<string> report)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            report("The server didn't answer: " + exception.Message);
        }
        catch (OperationCanceledException)
        {
            // The page closed.
        }
        catch (Exception exception)
        {
            report("That failed: " + exception.Message);
        }
    }
}
