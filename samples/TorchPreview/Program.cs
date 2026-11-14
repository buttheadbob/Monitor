using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PreviewHost;
using SenX.Monitor.Api;
using SenX.Monitor.Services;
using SenX.Monitor.Torch;
using SenX.Monitor.Torch.Ui;

namespace TorchPreview;

/// <summary>
/// Opens the Torch pages in a window of their own, against a simulated server. With --board and a file,
/// it opens no window: it starts the full charts' web server, writes its address to the file and waits. With
/// --image and a folder, it opens no window either: it lets history build, then saves some charts through the API.
/// With --tabs and a file, it opens no window: it builds the pages off screen, shows each tab in turn, and writes
/// what each one put on screen and any binding errors, so a broken column fails without anyone looking.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        MonitorLogSink.Write = (text, exception) => System.Diagnostics.Debug.WriteLine(text + " " + exception);

        using var servers = new SimulatedServers();
        using var services = new MonitorServices(servers, Path.Combine(Path.GetTempPath(), "monitor-torch-preview"));

        services.Start();

        if (args.Length == 2 && args[0] == "--board")
        {
            File.WriteAllText(args[1], services.BoardAddress());
            Thread.Sleep(Timeout.Infinite);
        }

        if (args.Length == 2 && args[0] == "--image")
        {
            SaveImages(services, args[1]);
            return;
        }

        if (args.Length == 2 && args[0] == "--tabs")
        {
            CheckTabs(services, args[1]);
            return;
        }

        var view = new MonitorView(services);
        var window = new Window { Title = "Monitor preview", Width = 1280d, Height = 860d, Content = view };

        new Application().Run(window);
        view.Stop();
    }

    private static void CheckTabs(MonitorServices services, string file)
    {
        const int WaitSeconds = 45;
        var errors = new BindingErrors();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // Without Application.Run nothing sets this, and a page's awaits would come back on the wrong thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var view = new MonitorView(services);
        var size = new Size(1280d, 860d);
        var report = new List<string>();

        // Let history build, as a real server would have it.
        Pump(TimeSpan.FromSeconds(WaitSeconds));

        var tabs = (TabControl)typeof(MonitorView).GetField("_tabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;

        for (int i = 0; i < tabs.Items.Count; i++)
        {
            int before = errors.Lines.Count;
            tabs.SelectedIndex = i;

            // Off screen the view never counts as visible, so it won't fill a tab itself. This does what it would.
            object? page = (tabs.Items[i] as TabItem)?.Content;
            page?.GetType().GetMethod("Shown")?.Invoke(page, null);
            Pump(TimeSpan.FromSeconds(3));
            view.Measure(size);
            view.Arrange(new Rect(size));
            view.UpdateLayout();
            Pump(TimeSpan.FromSeconds(1));

            var header = (tabs.Items[i] as TabItem)?.Header?.ToString() ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var content = (tabs.Items[i] as TabItem)?.Content as DependencyObject;
            int grids = Count<DataGrid>(content);
            int bars = Count<ProgressBar>(content);
            List<string> threadWords = Texts(content).Where(text => text is "Main" or "Worker" or "Mixed" || text.StartsWith("Main, waits", StringComparison.Ordinal)).Distinct().ToList();

            report.Add(header + ": tables " + grids + ", thread bars " + bars + ", thread words [" + string.Join(", ", threadWords) + "], binding errors " + (errors.Lines.Count - before));
        }

        report.Add(string.Empty);
        report.Add("Binding errors: " + errors.Lines.Count);
        report.AddRange(errors.Lines);
        File.WriteAllLines(file, report);
        view.Stop();
    }

    private static void Pump(TimeSpan time)
    {
        DateTime until = DateTime.UtcNow + time;

        while (DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }

    private static int Count<T>(DependencyObject? root) where T : DependencyObject => Walk(root).OfType<T>().Count();

    private static IEnumerable<string> Texts(DependencyObject? root) => Walk(root).OfType<TextBlock>().Select(block => block.Text ?? string.Empty);

    private static IEnumerable<DependencyObject> Walk(DependencyObject? root)
    {
        if (root == null)
            yield break;

        yield return root;

        int visual = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (int i = 0; i < visual; i++)
        {
            foreach (DependencyObject child in Walk(VisualTreeHelper.GetChild(root, i)))
                yield return child;
        }
    }

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Lines { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message != null)
                Lines.Add(message);
        }
    }

    private static void SaveImages(MonitorServices services, string folder)
    {
        const int WaitSeconds = 45;
        MonitorTools tools = services.Api.ToolsFor(services.Servers.Connected()[0].HistoryId);

        Thread.Sleep(TimeSpan.FromSeconds(WaitSeconds));

        foreach (MonitorChart chart in new[] { MonitorChart.FrameParts, MonitorChart.SimSpeed, MonitorChart.Traffic })
        {
            DateTime to = DateTime.UtcNow;
            byte[]? image = tools.ChartImageAsync(chart, to.AddMinutes(-5d), to, 800, 400, CancellationToken.None).GetAwaiter().GetResult();

            File.WriteAllText(Path.Combine(folder, chart + ".txt"), image == null ? "null" : image.Length + " bytes");

            if (image != null)
                File.WriteAllBytes(Path.Combine(folder, chart + ".png"), image);
        }
    }
}
