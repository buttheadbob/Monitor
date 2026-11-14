using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>One part of the frame, now against what's normal for this hour of the day.</summary>
public sealed record BaselineRow(string Name, double NowMs, double NormalMs, double HighMs)
{
    // A tiny part doubling is noise, so it has to have grown by a real amount too.
    private const double SmallestChangeMs = 1d;
    private const double Margin = 1.25d;

    public bool Unusual => NowMs > HighMs * Margin && NowMs > NormalMs + SmallestChangeMs;
}

/// <summary>What's normal now. While learning, there isn't enough history to say.</summary>
public sealed record BaselineReport(bool Learning, IReadOnlyList<BaselineRow> Rows)
{
    public static readonly BaselineReport NotYet = new(true, []);
}

/// <summary>Something costing far more than it usually does, or that wasn't around before. No normal means it's new.</summary>
public sealed record NewCost(string List, string Name, double NowMs, double? NormalMs);

/// <summary>
/// Learns what's normal for each server, hour by hour, because a full server at peak isn't a problem
/// and the same load at 4am might be. It uses the middle of each hour's minutes, so past lag can't
/// drag "normal" up with it, and a server that's often slow still gets a fair answer. Worked out in
/// the background and reused for an hour, so asking never waits for files.
/// </summary>
public sealed class MonitorBaselines(HistoryStore store, MonitorSettings settings, ILogger<MonitorBaselines> logger)
{
    private const int HoursInDay = 24;
    private const double NormalAt = 0.5d;
    private const double HighAt = 0.9d;

    // An hour seen once could be anything, so it takes most of two days' worth of that hour.
    private const int LeastMinutes = 90;
    private const int LeastDays = 2;

    // Too small to matter, however much it grew.
    private const double LeastNewCostMs = 1d;
    private const double GrownBy = 2d;

    private static readonly TimeSpan s_rebuildEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan s_nowWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_newCostsFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_notRecent = TimeSpan.FromHours(1);

    private static readonly (string Name, HistoryDimension List)[] s_lists =
    [
        ("Grid", HistoryDimension.Grids),
        ("Player", HistoryDimension.Owners),
        ("Faction", HistoryDimension.Factions),
        ("Mod or plugin", HistoryDimension.Mods),
        ("Block type", HistoryDimension.BlockTypes),
        ("Script", HistoryDimension.Scripts),
        ("Session component", HistoryDimension.Session),
        ("Game logic", HistoryDimension.Logic),
        ("RPC", HistoryDimension.Rpcs),
    ];

    private readonly ConcurrentDictionary<string, Profile> _profiles = new();
    private readonly ConcurrentDictionary<string, byte> _building = new();
    private readonly ConcurrentDictionary<string, (DateTime Time, List<NewCost> Rows)> _newCosts = new();

    private sealed record Profile(DateTime Built, HourProfile?[] Hours, Dictionary<(HistoryDimension, string), double> Usual, bool ListsLearned);

    private sealed record HourProfile(double[] Normal, double[] High);

    /// <summary>How a server compares with normal right now. Starts learning in the background if it needs to.</summary>
    public BaselineReport Report(string serverId) => Report(serverId, DateTime.UtcNow);

    /// <summary>
    /// What's new or has jumped in the latest totals, which is often the first sign of what changed.
    /// Kept for 30 seconds, because a page asks every couple of seconds and this scans every list.
    /// </summary>
    public List<NewCost> NewCosts(string serverId)
    {
        DateTime now = DateTime.UtcNow;

        if (_newCosts.TryGetValue(serverId, out (DateTime Time, List<NewCost> Rows) kept) && now - kept.Time < s_newCostsFor)
            return kept.Rows;

        List<NewCost> rows = FindNewCosts(serverId, now);
        _newCosts[serverId] = (now, rows);
        return rows;
    }

    internal List<NewCost> FindNewCosts(string serverId, DateTime now)
    {
        if (!_profiles.TryGetValue(serverId, out Profile? profile) || now - profile.Built > s_rebuildEvery)
            RebuildSoon(serverId, now);

        var rows = new List<NewCost>();

        if (profile == null || !profile.ListsLearned)
            return rows;

        foreach ((string name, HistoryDimension list) in s_lists)
        {
            foreach (StoredGroup row in store.LatestGroups(serverId, list))
            {
                double cost = row.MainMs + row.WorkerMs;

                if (cost < LeastNewCostMs)
                    continue;

                bool known = profile.Usual.TryGetValue((list, row.Identity), out double usual);

                if (!known)
                    rows.Add(new NewCost(name, row.Name, cost, null));
                else if (cost > usual * GrownBy)
                    rows.Add(new NewCost(name, row.Name, cost, usual));
            }
        }

        return rows.OrderByDescending(row => row.NowMs).ToList();
    }

    /// <summary>The parts of the frame that look unusual right now, in words.</summary>
    public List<string> UnusualNow(string serverId) => UnusualNow(serverId, DateTime.UtcNow);

    internal List<string> UnusualNow(string serverId, DateTime now)
    {
        return Report(serverId, now).Rows
            .Where(row => row.Unusual)
            .OrderByDescending(row => row.NowMs - row.NormalMs)
            .Select(row => row.Name + ": " + Ms(row.NowMs) + " ms/f, normally " + Ms(row.NormalMs))
            .ToList();
    }

    internal BaselineReport Report(string serverId, DateTime now)
    {
        if (!_profiles.TryGetValue(serverId, out Profile? profile) || now - profile.Built > s_rebuildEvery)
            RebuildSoon(serverId, now);

        if (profile == null || profile.Hours[now.Hour] is not { } hour)
            return BaselineReport.NotYet;

        List<HistoryPoint> recent = store.Points(serverId, now - s_nowWindow, now, int.MaxValue);
        if (recent.Count == 0)
            return BaselineReport.NotYet;

        double[] totals = new double[FrameParts.All.Length];

        foreach (HistoryPoint point in recent)
        {
            double[] parts = FrameParts.Split(point);

            for (int part = 0; part < totals.Length; part++)
                totals[part] += parts[part];
        }

        var rows = new List<BaselineRow>(totals.Length);

        for (int part = 0; part < totals.Length; part++)
        {
            // Idle growing is good news, and it's what's left rather than something using the frame.
            if (FrameParts.All[part].Name != FrameParts.IdleName)
                rows.Add(new BaselineRow(FrameParts.All[part].Name, totals[part] / recent.Count, hour.Normal[part], hour.High[part]));
        }

        return new BaselineReport(false, rows);
    }

    private void RebuildSoon(string serverId, DateTime now)
    {
        if (!_building.TryAdd(serverId, 0))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                Rebuild(serverId, now);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Monitor could not work out what's normal for a server.");
            }
            finally
            {
                _building.TryRemove(serverId, out _);
            }
        });
    }

    /// <summary>Learns from the history kept. A test calls this directly.</summary>
    internal void Rebuild(string serverId, DateTime now)
    {
        List<HistoryPoint> points = store.Points(serverId, now - settings.Retention, now, int.MaxValue);

        var byHour = new List<HistoryPoint>[HoursInDay];
        for (int hour = 0; hour < HoursInDay; hour++)
            byHour[hour] = [];

        foreach (HistoryPoint point in points)
            byHour[point.Time.Hour].Add(point);

        var hours = new HourProfile?[HoursInDay];

        for (int hour = 0; hour < HoursInDay; hour++)
        {
            List<HistoryPoint> rows = byHour[hour];

            if (rows.Count >= LeastMinutes && rows.Select(row => row.Time.Date).Distinct().Count() >= LeastDays)
                hours[hour] = Learn(rows);
        }

        List<StoredGroup> groups = store.AllGroups(serverId, now - settings.Retention, now - s_notRecent);
        var usual = new Dictionary<(HistoryDimension, string), double>();

        // Lag keeps samples twice as often, and counting those twice would make lag look normal.
        Dictionary<DateTime, double> weights = SampleRate.Weights(groups.Select(row => row.Time));

        foreach (IGrouping<(HistoryDimension Dimension, string Identity), StoredGroup> rows in groups.GroupBy(row => (row.Dimension, row.Identity)))
            usual[rows.Key] = rows.Sum(row => (row.MainMs + row.WorkerMs) * weights[row.Time]) / rows.Sum(row => weights[row.Time]);

        bool learned = groups.Select(row => row.Time.Date).Distinct().Count() >= LeastDays;

        _profiles[serverId] = new Profile(now, hours, usual, learned);
    }

    private static HourProfile Learn(List<HistoryPoint> rows)
    {
        int parts = FrameParts.All.Length;
        var normal = new double[parts];
        var high = new double[parts];

        double[][] split = rows.Select(FrameParts.Split).ToArray();
        var values = new double[rows.Count];

        for (int part = 0; part < parts; part++)
        {
            for (int row = 0; row < values.Length; row++)
                values[row] = split[row][part];

            Array.Sort(values);
            normal[part] = values[(int)(NormalAt * (values.Length - 1))];
            high[part] = values[(int)(HighAt * (values.Length - 1))];
        }

        return new HourProfile(normal, high);
    }

    private static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
