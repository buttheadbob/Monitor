using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>How a stretch of history went. Times are ms per frame, and <see cref="Seconds"/> is how much of it has data.</summary>
public sealed record WindowSummary(
    DateTime From,
    DateTime To,
    double SimSpeed,
    double AverageFrameMs,
    double WorstFrameMs,
    double AvailableMs,
    IReadOnlyList<double> Parts,
    int Points)
{
    public bool HasData => Points > 0;

    /// <summary>How much time the points really cover, which leaves out a server being away or paused.</summary>
    public double Seconds { get; init; }

    /// <summary>The frames simulated across them.</summary>
    public long Frames { get; init; }
}

/// <summary>
/// One row of a list over a stretch of history. Background totals keep each sweep's top 20, so a row
/// outside that in some sweeps has no figure there. Its times are averaged over the sweeps it appeared
/// in, and <see cref="SeenShare"/> says what share that was.
/// </summary>
public sealed record WindowRow(string Name, long Key, double MainMs, double WorkerMs, double PeakMs, double CallsPerFrame, double SeenShare)
{
    public double TotalMs => MainMs + WorkerMs;

    /// <summary>For rows that count traffic. Zero for the rest.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>From the latest total it was in. See <see cref="StoredGroup.Blocks"/>.</summary>
    public long Blocks { get; init; }

    /// <summary>From the latest total it was in. See <see cref="StoredGroup.Grids"/>.</summary>
    public long Grids { get; init; }
}

/// <summary>How frames went at one level of players.</summary>
public sealed record CapacityRow(int PlayersFrom, int PlayersTo, double FrameMs, double WorstMs, int Moments);

/// <summary>
/// How frame time has moved with players. <see cref="PlayersAtBudget"/> is where a straight line through
/// that history reaches the 16.67 ms budget, and it's null unless the fit is good enough to be worth saying.
/// </summary>
public sealed record CapacityReport(IReadOnlyList<CapacityRow> Rows, int? PlayersAtBudget);

/// <summary>Reads stretches of history as totals, for comparing two of them and for other plugins.</summary>
public sealed class HistoryWindows(HistoryStore store)
{
    private const int CapacityBands = 8;
    private const int LeastCapacitySamples = 60;
    private const int LeastPlayerLevels = 3;
    private const double LeastFit = 0.3d;

    /// <summary>What history can answer about a list, biggest first.</summary>
    public List<WindowRow> List(string serverId, HistoryDimension list, DateTime from, DateTime to) =>
        ToRows(store.Groups(serverId, list, from, to));

    /// <summary>The same for several lists, reading history once.</summary>
    public Dictionary<HistoryDimension, List<WindowRow>> Lists(string serverId, IReadOnlyCollection<HistoryDimension> lists, DateTime from, DateTime to)
    {
        ILookup<HistoryDimension, StoredGroup> rows = store.GroupsOf(serverId, lists, from, to).ToLookup(row => row.Dimension);

        return lists.Distinct().ToDictionary(list => list, list => ToRows(rows[list].ToList()));
    }

    /// <summary>The same from rows already read.</summary>
    public static List<WindowRow> ToRows(List<StoredGroup> rows)
    {
        // Lag keeps totals twice as often, and a short range can mix both rates, so each counts for the time it stands for.
        Dictionary<DateTime, double> weights = SampleRate.Weights(rows.Select(row => row.Time));
        double all = weights.Values.Sum();

        // Two grids can share a name, so anything with an id is told apart by it.
        return rows
            .GroupBy(row => row.Identity)
            .Select(group =>
            {
                List<IGrouping<DateTime, StoredGroup>> totals = group.GroupBy(row => row.Time).ToList();
                double seen = totals.Sum(total => weights[total.Key]);

                double Mean(Func<StoredGroup, double> value) => totals.Sum(total => total.Sum(value) * weights[total.Key]) / seen;

                StoredGroup latest = group.Last();

                return new WindowRow(
                    latest.Name,
                    latest.Key,
                    Mean(row => row.MainMs),
                    Mean(row => row.WorkerMs),
                    group.Max(row => row.PeakMs),
                    Mean(row => row.CallsPerFrame),
                    seen / all)
                {
                    BytesPerSecond = Mean(row => row.BytesPerSecond),
                    Blocks = latest.Blocks,
                    Grids = latest.Grids,
                };
            })
            .OrderByDescending(row => row.TotalMs)
            .ThenByDescending(row => row.BytesPerSecond)
            .ToList();
    }

    public WindowSummary Summarize(string serverId, DateTime from, DateTime to) =>
        Summarize(from, to, store.Points(serverId, from, to, int.MaxValue));

    /// <summary>The same, from points already read.</summary>
    public static WindowSummary Summarize(DateTime from, DateTime to, IReadOnlyList<HistoryPoint> points)
    {
        if (points.Count == 0)
            return new WindowSummary(from, to, 0d, 0d, 0d, 0d, new double[FrameParts.All.Length], 0);

        var parts = new double[FrameParts.All.Length];

        foreach (HistoryPoint point in points)
        {
            double[] split = FrameParts.Split(point);

            for (int part = 0; part < parts.Length; part++)
                parts[part] += split[part] / points.Count;
        }

        return new WindowSummary(
            from,
            to,
            points.Average(point => point.SimSpeed),
            points.Average(point => HistoryScopes.Frame < point.Scopes.Length ? point.Scopes[HistoryScopes.Frame] : 0f),
            points.Max(point => point.FrameMax),
            points.Average(FrameParts.Available),
            parts,
            points.Count)
        {
            Seconds = points.Sum(point => point.Seconds),
            Frames = points.Sum(point => (long)point.SimulatedFrames),
        };
    }

    /// <summary>Sets frame time against players online, from census passes and the timings nearest them.</summary>
    public CapacityReport Capacity(string serverId, DateTime from, DateTime to)
    {
        List<HistoryPoint> points = store.Points(serverId, from, to, int.MaxValue);
        List<StoredCensus> census = store.Census(serverId, from, to);

        if (points.Count == 0 || census.Count == 0)
            return new CapacityReport([], null);

        var samples = new List<(int Players, double FrameMs, double WorstMs)>(census.Count);
        int index = 0;

        foreach (StoredCensus row in census)
        {
            while (index + 1 < points.Count && points[index + 1].Time <= row.Time)
                index++;

            HistoryPoint point = points[index];
            double frame = HistoryScopes.Frame < point.Scopes.Length ? point.Scopes[HistoryScopes.Frame] : 0d;
            samples.Add(((int)row.Row.OnlinePlayers, frame, point.FrameP99));
        }

        int most = samples.Max(sample => sample.Players);
        int step = Math.Max(1, (int)Math.Ceiling((most + 1) / (double)CapacityBands));

        List<CapacityRow> rows = samples
            .GroupBy(sample => sample.Players / step)
            .OrderBy(band => band.Key)
            .Select(band => new CapacityRow(band.Key * step, (band.Key * step) + step - 1, band.Average(sample => sample.FrameMs), band.Max(sample => sample.WorstMs), band.Count()))
            .ToList();

        return new CapacityReport(rows, PlayersAtBudget(samples));
    }

    // A line through scattered points always gives a number, so this only speaks when the line fits.
    private static int? PlayersAtBudget(List<(int Players, double FrameMs, double WorstMs)> samples)
    {
        if (samples.Count < LeastCapacitySamples || samples.Select(sample => sample.Players).Distinct().Count() < LeastPlayerLevels)
            return null;

        double meanX = samples.Average(sample => sample.Players);
        double meanY = samples.Average(sample => sample.FrameMs);
        double sumXY = samples.Sum(sample => (sample.Players - meanX) * (sample.FrameMs - meanY));
        double sumXX = samples.Sum(sample => (sample.Players - meanX) * (sample.Players - meanX));
        double sumYY = samples.Sum(sample => (sample.FrameMs - meanY) * (sample.FrameMs - meanY));

        if (sumXX <= 0d || sumYY <= 0d)
            return null;

        double slope = sumXY / sumXX;
        double fit = sumXY * sumXY / (sumXX * sumYY);

        if (slope <= 0d || fit < LeastFit)
            return null;

        return (int)((FrameParts.BudgetMs - (meanY - (slope * meanX))) / slope);
    }

    /// <summary>One list from two stretches, biggest change first. A row only one has comes last, with no change.</summary>
    public static List<ComparedRow> Compare(IEnumerable<WindowRow> before, IEnumerable<WindowRow> after)
    {
        // Rows with an id join on it, so two grids with one name stay two grids.
        static string IdentityOf(WindowRow row) => row.Key != 0L ? "#" + row.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) : row.Name;

        List<WindowRow> first = before.ToList();
        List<WindowRow> second = after.ToList();
        Dictionary<string, string> names = first.Concat(second).GroupBy(IdentityOf).ToDictionary(row => row.Key, row => row.First().Name);

        return RunComparison.Join(first.ToDictionary(IdentityOf, row => row.TotalMs), second.ToDictionary(IdentityOf, row => row.TotalMs), identity => names[identity]);
    }
}
