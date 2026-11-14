using System.Windows;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Contracts;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Ui.Tabs;

/// <summary>Where the frame went over the range, as a tree that opens and closes.</summary>
public sealed class TreeTab : MonitorTab
{
    private const int MaxPoints = 1200;
    private const double IndentPerLevel = 16d;

    // Too small to read, and hiding these keeps the tree to what matters.
    private const double SmallestShownMs = 0.005d;

    private readonly HashSet<int> _closed = [];
    private readonly ReportGrid _table;
    private readonly Notice _nothing = new();
    private double[] _totals = [];
    private bool _loading;

    public TreeTab(MonitorView page) : base(page)
    {
        _table = new ReportGrid()
            .Toggled<TreeRow>("Part", nameof(TreeRow.Name), nameof(TreeRow.Glyph), row => Toggle(row.Id), nameof(TreeRow.Indent), nameof(TreeRow.ToggleShown))
            .Number("Total ms/f", nameof(TreeRow.TotalMs))
            .Number("Self ms/f", nameof(TreeRow.SelfMs))
            .Number("Share of frame", nameof(TreeRow.Share), "0.0'%'")
            .Text("Thread", nameof(TreeRow.Thread), stretch: false);

        Add(
            Look.Hint("Where the frame went over this range. Total includes what's inside a part, and Self is what's left after those, so a large Self means time the parts below don't explain. All of it is game thread time, and Thread says where some of that is the game thread waiting for others."),
            _nothing,
            _table);
    }

    // Averages over a range move slowly, and the whole table rebuilds when they do.
    protected override int RefreshSeconds => 10;

    public override async void Refresh()
    {
        if (_loading)
            return;

        _loading = true;
        string server = ServerId;
        int minutes = RangeMinutes;

        try
        {
            // A long range reads files, and the page shouldn't stop while it does.
            _totals = await Task.Run(() => Totals(server, minutes));
            Rebuild();
        }
        catch (Exception exception)
        {
            MonitorLogSink.Write?.Invoke("Monitor could not read the tree.", exception);
        }
        finally
        {
            _loading = false;
        }

        // The range changed while that was reading.
        if (minutes != RangeMinutes)
            Refresh();
    }

    private double[] Totals(string server, int minutes)
    {
        List<HistoryPoint> points = Services.Store.Points(server, DateTime.UtcNow.AddMinutes(-minutes), DateTime.UtcNow, MaxPoints);
        var totals = new double[TreeShape.All.Length + 1];

        foreach (HistoryPoint point in points)
        {
            for (int id = 1; id < totals.Length && id < point.Scopes.Length; id++)
                totals[id] += point.Scopes[id] / (double)points.Count;
        }

        return totals;
    }

    private void Toggle(int id)
    {
        if (!_closed.Remove(id))
            _closed.Add(id);

        Rebuild();
    }

    private void Rebuild()
    {
        var rows = new List<TreeRow>();

        if (_totals.Length > TreeShape.Frame && _totals[TreeShape.Frame] > 0d)
        {
            foreach (TreeNode top in TreeShape.ChildrenOf(0))
                AddRow(rows, top, 0);
        }

        _nothing.Text = rows.Count == 0 ? "Nothing collected for this range yet." : null;
        _table.Show(rows);
    }

    private void AddRow(List<TreeRow> rows, TreeNode node, int depth)
    {
        double total = _totals[node.Id];

        if (total < SmallestShownMs)
            return;

        List<TreeNode> children = TreeShape.ChildrenOf(node.Id).ToList();
        double self = Math.Max(0d, total - children.Sum(child => _totals[child.Id]));
        double share = total / _totals[TreeShape.Frame] * 100d;

        rows.Add(new TreeRow(
            node.Id,
            node.Name,
            total,
            self,
            share,
            new Thickness(depth * IndentPerLevel, 0d, 0d, 0d),
            _closed.Contains(node.Id) ? "+" : "-",
            children.Count > 0 ? Visibility.Visible : Visibility.Hidden));

        if (_closed.Contains(node.Id))
            return;

        foreach (TreeNode child in children.OrderByDescending(child => _totals[child.Id]))
            AddRow(rows, child, depth + 1);
    }

    public sealed record TreeRow(int Id, string Name, double TotalMs, double SelfMs, double Share, Thickness Indent, string Glyph, Visibility ToggleShown)
    {
        public string Thread => TreeShape.ThreadOf(Id);
    }
}
