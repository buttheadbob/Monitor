using System.Globalization;
using System.Text;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>Turns a result into plain text to paste somewhere, such as a support ticket or a chat.</summary>
public static class ReportText
{
    private const int RowsPerList = 5;

    private static readonly (string Title, string List)[] s_lists =
    [
        ("Physics clusters", RunLists.Clusters),
        ("Grids", RunLists.Grids),
        ("Block types", RunLists.BlockTypes),
        ("Players", RunLists.Players),
        ("Factions", RunLists.Factions),
        ("Mods and plugins", RunLists.Mods),
        ("Scripts", RunLists.Scripts),
        ("Session components", RunLists.Session),
        ("RPCs received", RunLists.Rpcs),
        ("RPCs sent", RunLists.RpcsSent),
    ];

    public static string ForRun(RunResultResponse run)
    {
        var text = new StringBuilder();
        text.AppendLine(Invariant($"Measured {run.Seconds:0.0} s, sim {run.SimSpeed:0.00}. Frame {run.AverageFrameMs:0.0} ms average, {run.WorstFrameMs:0.0} ms worst."));

        if (run.RequestedBy.Length > 0)
            text.AppendLine("Started by " + run.RequestedBy + ".");

        if (run.LostSites != null)
            text.AppendLine(run.LostSites + ".");

        text.AppendLine(Invariant($"Game thread waiting for workers {run.MainWaitMsPerFrame:0.00} ms/f, parallel work {run.ParallelWorkerMsPerFrame:0.00} ms/f across workers."));

        foreach ((string title, string list) in s_lists)
        {
            List<RunRow> rows = run.Rows.Where(row => row.List == list).Take(RowsPerList).ToList();
            if (rows.Count == 0)
                continue;

            text.AppendLine();
            text.AppendLine(title);

            foreach (RunRow row in rows)
            {
                text.AppendLine(list == RunLists.RpcsSent
                    ? Invariant($"  {row.Name}: {row.BytesPerSecond / 1024d:0.0} KB/s, {row.CallsPerFrame:0.##} events/f")
                    : list == RunLists.Clusters
                        ? Invariant($"  {row.Name}: {row.MainMsPerFrame:0.00} ms alone, peak {row.PeakMs:0.00} ms")
                        : Invariant($"  {ThreadTags.Tag(row.MainMsPerFrame, row.WorkerMsPerFrame)} {row.Name}: main {row.MainMsPerFrame:0.00}, workers {row.WorkerMsPerFrame:0.00}, peak {row.PeakMs:0.00} ms"));
            }
        }

        return text.ToString().TrimEnd();
    }

    public static string ForIncident(StoredIncident incident)
    {
        var text = new StringBuilder();
        text.AppendLine(MonitorIncidents.ReasonOf(incident) + " at " + incident.Opened.ToString("u", CultureInfo.InvariantCulture));
        text.AppendLine(incident.Closed == null
            ? "Still going."
            : Invariant($"{incident.ClosedBy} after {(incident.Closed.Value - incident.Opened).TotalSeconds:0} s."));
        text.AppendLine(Invariant($"Lowest sim {incident.LowestSim:0.00}, worst frame {incident.WorstFrameMs:0} ms."));

        if (incident.Unusual.Count > 0)
            text.AppendLine("Unusual: " + string.Join("; ", incident.Unusual) + ".");

        foreach (IncidentResultResponse report in incident.Evidence.Where(evidence => evidence.Report != null).Select(evidence => evidence.Report!))
        {
            if (report.Run != null)
            {
                text.AppendLine();
                text.AppendLine(ForRun(report.Run));
            }
            else if (report.SlowCalls.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Slowest calls");

                foreach (SlowCallRow call in report.SlowCalls.Take(RowsPerList))
                    text.AppendLine(Invariant($"  {call.Name} ({call.Where}): {call.Ms:0.0} ms"));
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
