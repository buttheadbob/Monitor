using System.Windows.Controls;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>Which plugins change the game's own methods, and what an audit says those changes cost.</summary>
public sealed class PatchesTab : MonitorTab
{
    private const int MaxChosen = 8;
    private const int MaxRowsShown = 200;

    private static readonly TimeSpan s_pollEvery = TimeSpan.FromSeconds(2);

    private readonly Notice _message = new();
    private readonly Notice _lost = new();
    private readonly StackPanel _audit = new();
    private readonly ReportGrid _plugins;
    private readonly ReportGrid _methods;
    private readonly StackPanel _lists;
    private readonly TextBox _filter = Look.Input();
    private readonly NumberBox _seconds = new(30d);
    private readonly Button _start;
    private readonly HashSet<string> _chosen = [];
    private List<PatchedMethodRow> _loaded = [];
    private bool _read;
    private bool _auditing;

    public PatchesTab(MonitorView page) : base(page)
    {
        _start = Look.Action("Audit 0 chosen", (_, _) => StartAudit());
        _filter.TextChanged += (_, _) => ShowMethods();

        _plugins = new ReportGrid()
            .Text("Plugin", nameof(PluginLine.Plugin))
            .Number("Methods", nameof(PluginLine.Methods), "0")
            .Number("Prefixes", nameof(PluginLine.Prefixes), "0")
            .Number("Postfixes", nameof(PluginLine.Postfixes), "0")
            .Number("Transpilers", nameof(PluginLine.Transpilers), "0");

        _methods = new ReportGrid()
            .Tick<MethodLine>(nameof(MethodLine.Chosen), Choose)
            .Text("Method", nameof(MethodLine.Method))
            .Text("Patched by", nameof(MethodLine.PatchedBy));

        _lists = Look.Stack(
            Look.Section("By plugin", _plugins),
            Look.Row(Look.Field("Find", _filter), Look.Field("Audit for (seconds)", _seconds), _start),
            _methods);

        _lists.Visibility = System.Windows.Visibility.Collapsed;

        Add(
            Look.Hint("Which plugins change the game's own methods. An audit measures what those changes cost, by splitting a call into plugin prefixes, the original and plugin postfixes."),
            _message,
            _lost,
            _audit,
            _lists);
    }

    protected override int RefreshSeconds => 0;

    public override void Shown()
    {
        if (!_read)
            Read();
    }

    private void Read() => Guarded(
        async () =>
        {
            _read = true;

            var methods = new List<PatchedMethodRow>();

            while (true)
            {
                PatchListResponse list = await Services.Servers.ListPatchesAsync(Page.Server.Id, new PatchListRequest { Skip = methods.Count }, Page.Closing);
                methods.AddRange(list.Methods);

                _lost.Text = list.LostSites.Count == 0
                    ? null
                    : "Monitor can't time these calls, because they've gone from methods another plugin rewrote, so what runs there reads low or empty: "
                      + string.Join("; ", list.LostSites.Select(site => site.Site + " in " + site.Method + " (" + site.TakenBy + ")")) + ".";

                if (list.Methods.Count == 0 || methods.Count >= list.Total)
                    break;
            }

            _loaded = methods;

            ShowAudit(await Services.Servers.AuditResultAsync(Page.Server.Id, new AuditResultRequest(), Page.Closing));

            _message.Text = methods.Count == 0 ? "No other plugin has patched anything on this server." : null;
            _lists.Visibility = Look.VisibleIf(methods.Count > 0);

            _plugins.Show(methods
                .SelectMany(method => method.Patches.Select(patch => (method, patch)))
                .GroupBy(row => row.patch.Plugin)
                .OrderByDescending(group => group.Count())
                .Select(group => new PluginLine(
                    group.Key,
                    group.Select(row => row.method).Distinct().Count(),
                    group.Count(row => row.patch.Kind == PatchKinds.Prefix),
                    group.Count(row => row.patch.Kind == PatchKinds.Postfix),
                    group.Count(row => row.patch.Kind == PatchKinds.Transpiler)))
                .ToList());

            ShowMethods();
        },
        message =>
        {
            _read = false;
            _message.Text = message;
        });

    private void ShowMethods()
    {
        string filter = _filter.Text;

        _methods.Show(_loaded
            .Where(method => filter.Length == 0
                || method.Method.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || method.Patches.Any(patch => patch.Plugin.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxRowsShown)
            .Select(method => new MethodLine(
                method.Method,
                string.Join(", ", method.Patches.Select(patch => patch.Plugin + " " + patch.Kind.ToLowerInvariant())),
                _chosen.Contains(method.Method)))
            .ToList());
    }

    private bool Choose(MethodLine line, bool chosen)
    {
        if (chosen && _chosen.Count >= MaxChosen)
            return false;

        if (chosen)
            _chosen.Add(line.Method);
        else
            _chosen.Remove(line.Method);

        _start.Content = "Audit " + _chosen.Count + " chosen";
        return true;
    }

    private void StartAudit()
    {
        if (_auditing || _chosen.Count == 0)
            return;

        _auditing = true;
        _start.IsEnabled = false;
        _start.Content = "Auditing...";
        _message.Text = null;

        Guarded(
            async () =>
            {
                try
                {
                    await RunAudit();
                }
                finally
                {
                    _auditing = false;
                    _start.IsEnabled = true;
                    _start.Content = "Audit " + _chosen.Count + " chosen";
                }
            },
            message => _message.Text = message);
    }

    private async Task RunAudit()
    {
        var request = new AuditStartRequest { Methods = [.. _chosen], Seconds = Math.Clamp(_seconds.Whole, 1, 120) };
        AuditStartResponse started = await Services.Servers.StartAuditAsync(Page.Server.Id, request, Page.Closing);

        if (!started.Started)
        {
            _message.Text = started.Message ?? "The audit didn't start.";
            return;
        }

        while (true)
        {
            await Task.Delay(s_pollEvery, Page.Closing);

            AuditResultResponse result = await Services.Servers.AuditResultAsync(Page.Server.Id, new AuditResultRequest(), Page.Closing);

            if (result.State == RunState.Running)
                continue;

            ShowAudit(result);
            return;
        }
    }

    private void ShowAudit(AuditResultResponse audit)
    {
        _audit.Children.Clear();

        if (audit.State != RunState.Finished)
            return;

        ReportGrid table = new ReportGrid()
            .Text("Method", nameof(AuditLine.Method))
            .Text("Plugins", nameof(AuditLine.Plugins))
            .Number("Calls/f", nameof(AuditLine.CallsPerFrame), "0.#")
            .Number("Prefixes ms/f", nameof(AuditLine.PrefixMs), "0.000")
            .Number("Original ms/f", nameof(AuditLine.OriginalMs), "0.000")
            .Number("Postfixes ms/f", nameof(AuditLine.PostfixMs), "0.000");

        table.Show(audit.Rows.Select(row => new AuditLine(
            row.Method + (row.Transpiled ? " (transpiled)" : string.Empty),
            row.Problem ?? string.Join(", ", row.Plugins),
            row.CallsPerFrame,
            row.PrefixMs,
            row.OriginalMs,
            row.PostfixMs)).ToList());

        _audit.Children.Add(Look.Section(
            "Last audit, over " + audit.Seconds + " s",
            Look.Hint("Times are per frame, added up across threads. A method with several plugins has their time together, because Monitor can't tell one plugin's prefix from another's. A transpiler's changes are inside the original, so they can't be split out."),
            table));
    }

    public sealed record PluginLine(string Plugin, int Methods, int Prefixes, int Postfixes, int Transpilers);

    public sealed record MethodLine(string Method, string PatchedBy, bool Chosen);

    public sealed record AuditLine(string Method, string Plugins, double CallsPerFrame, double PrefixMs, double OriginalMs, double PostfixMs);
}
