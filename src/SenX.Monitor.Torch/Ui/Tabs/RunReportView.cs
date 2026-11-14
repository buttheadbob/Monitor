using System.Windows;
using System.Windows.Controls;
using SenX.Monitor.Api;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>One finished run: what it measured, then every list it holds. Runs and incidents both show these.</summary>
public sealed class RunReportView : StackPanel
{
    private const int MostSlowCalls = 20;

    /// <summary>Every list a run can hold, in the order they're shown.</summary>
    public static readonly (string Title, string List)[] Lists =
    [
        ("Physics clusters", RunLists.Clusters),
        ("Grids", RunLists.Grids),
        ("Block types", RunLists.BlockTypes),
        ("Block definitions", RunLists.Definitions),
        ("Players", RunLists.Players),
        ("Factions", RunLists.Factions),
        ("Mods and plugins", RunLists.Mods),
        ("Scripts", RunLists.Scripts),
        ("Session components", RunLists.Session),
        ("Game logic", RunLists.Logic),
        ("Callbacks", RunLists.Callbacks),
        ("RPCs received", RunLists.Rpcs),
        ("RPCs sent", RunLists.RpcsSent),
        ("Entity types", RunLists.Entities),
    ];

    public RunReportView(RunResultResponse result)
    {
        Children.Add(Look.Row(
            Look.Action("Copy as text", (_, _) => Look.Copy(ReportText.ForRun(result))),
            Look.Hint(result.RequestedBy.Length > 0 ? "Started by " + result.RequestedBy + "." : string.Empty)));

        Children.Add(Look.Tiles(
            Figure("Measured", Look.Num(result.Seconds, "0.0") + " s"),
            Figure("Sim speed", Look.Num(result.SimSpeed, "0.00")),
            Figure("Frame average", Look.Num(result.AverageFrameMs, "0.0") + " ms"),
            Figure("Frame worst", Look.Num(result.WorstFrameMs, "0.0") + " ms"),
            Figure("Timing added", Look.Num(result.TimingMsPerFrame, "0.00") + " ms/f"),
            new Tile("Main waiting", ThreadTags.MainWaitTip) { Value = Look.Num(result.MainWaitMsPerFrame, "0.00") + " ms/f" },
            new Tile("Parallel work", ThreadTags.ParallelWorkTip) { Value = Look.Num(result.ParallelWorkerMsPerFrame, "0.00") + " ms/f" },
            Figure("Coverage", Look.Num(result.Coverage * 100d, "0.0") + "%")));

        if (result.EndedEarly)
            Children.Add(new Notice { Text = "The server paused, so the run stopped early." });

        if (result.LostSites != null)
            Children.Add(new Notice { Text = result.LostSites + "." });

        if (result.ClusterProblem != null)
            Children.Add(new Notice { Text = "Clusters weren't timed, because " + result.ClusterProblem + "." });

        var lists = new FlowGrid(380d, 14d);

        foreach ((string title, string list) in Lists)
        {
            List<RunRow> rows = result.Rows.Where(row => row.List == list).ToList();

            if (rows.Count > 0)
                lists.Children.Add(Look.Section(title, list == RunLists.RpcsSent ? SentTable(rows) : CostTable(rows, list)));
        }

        if (result.SlowCalls.Count > 0)
            lists.Children.Add(Look.Section("Slowest single calls", SlowCalls(result.SlowCalls)));

        Children.Add(lists);
    }

    /// <summary>The table of slow calls, which incidents show without a run too.</summary>
    public static ReportGrid SlowCalls(IReadOnlyList<SlowCallRow> calls)
    {
        ReportGrid table = new ReportGrid()
            .Text("Call", nameof(SlowLine.Name))
            .Text("Kind", nameof(SlowLine.Where), stretch: false)
            .Text("Thread", nameof(SlowLine.Thread), stretch: false)
            .Number("ms", nameof(SlowLine.Ms), "0.0");

        table.Show(calls.Take(MostSlowCalls).Select(call => new SlowLine(call.Name, call.Where, call.MainThread ? "Main" : "Worker", call.Ms)).ToList());
        return table;
    }

    private static Tile Figure(string label, string value) => new(label) { Value = value };

    private static ReportGrid SentTable(List<RunRow> rows)
    {
        ReportGrid table = new ReportGrid()
            .Text("Name", nameof(SentLine.Name))
            .Number("KB/s", nameof(SentLine.Kb), "0.0")
            .Number("Events/f", nameof(SentLine.CallsPerFrame), "0.#");

        table.Show(rows.Select(row => new SentLine(row.Name, row.BytesPerSecond / 1024d, row.CallsPerFrame)).ToList());
        return table;
    }

    private static ReportGrid CostTable(List<RunRow> rows, string list)
    {
        ReportGrid table = new ReportGrid()
            .Text("Name", nameof(CostLine.Name))
            .Number(list == RunLists.Clusters ? "ms alone" : "Main ms/f", nameof(CostLine.MainMs))
            .Number("Workers ms/f", nameof(CostLine.WorkerMs))
            .Number("Peak ms", nameof(CostLine.PeakMs));

        ThreadColumn.Add(table)
            .Button<CostLine>("GPS", row => Look.Copy(row.Gps!), nameof(CostLine.GpsShown));

        if (rows.Any(row => row.Members is { Count: > 0 }))
        {
            // Selecting a row opens what's in it, as with a cluster's grids.
            var members = new FrameworkElementFactory(typeof(TextBlock));
            members.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(CostLine.MembersText)));
            members.SetValue(MarginProperty, new Thickness(12, 2, 0, 6));
            members.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            table.RowDetailsTemplate = new DataTemplate { VisualTree = members };
            table.RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.VisibleWhenSelected;
        }

        table.Show(rows.Select(row => new CostLine(
            row.Name,
            row.MainMsPerFrame,
            row.WorkerMsPerFrame,
            row.PeakMs,
            row.Gps,
            Look.VisibleIf(row.Gps != null),
            row.Members == null ? string.Empty : string.Join("\n", row.Members),
            list != RunLists.Clusters)).ToList());

        return table;
    }

    public sealed record CostLine(string Name, double MainMs, double WorkerMs, double PeakMs, string? Gps, Visibility GpsShown, string MembersText, bool ThreadKnown)
    {
        // A cluster's time is one step with every physics thread to itself, so its thread means nothing.
        public string Thread => ThreadKnown ? ThreadTags.Name(MonitorThreads.Of(MainMs, WorkerMs)) : string.Empty;

        public double MainPercent => ThreadTags.MainPercent(MainMs, WorkerMs);
    }

    public sealed record SentLine(string Name, double Kb, double CallsPerFrame);

    public sealed record SlowLine(string Name, string Where, string Thread, double Ms);
}

/// <summary>Two sets of figures side by side, with what changed.</summary>
internal static class ComparedTable
{
    public static ReportGrid Build(IReadOnlyList<ComparedRow> rows)
    {
        ReportGrid table = new ReportGrid()
            .Text("Name", nameof(ComparedRow.Name))
            .Number("Before ms/f", nameof(ComparedRow.Before))
            .Number("After ms/f", nameof(ComparedRow.After))
            .Number("Change", nameof(ComparedRow.Change), "+0.00;-0.00;0.00");

        table.Show(rows);
        return table;
    }
}
