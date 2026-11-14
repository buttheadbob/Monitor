using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>What the charts read from history.</summary>
public interface IHistoryReads
{
    List<HistoryPoint> Points(string serverId, DateTime from, DateTime to, int maxPoints);

    List<StoredCensus> Census(string serverId, DateTime from, DateTime to);

    List<StoredPlayer> Players(string serverId, DateTime from, DateTime to);

    List<StoredFaction> Factions(string serverId, DateTime from, DateTime to);

    List<StoredGroup> Groups(string serverId, HistoryDimension list, DateTime from, DateTime to);

    List<StoredGroup> LatestGroups(string serverId, HistoryDimension list);

    List<StoredMarker> Markers(string serverId, DateTime from, DateTime to);
}

/// <summary>
/// One board's reads. Its dozens of panels want the same few things, and a long range means reading files,
/// so each is read once and shared. Every list comes from one pass over the files rather than one pass each.
/// It lasts for one board, so nothing here is ever out of date or held on to.
/// </summary>
public sealed class BoardReads(HistoryStore store, IReadOnlyCollection<HistoryDimension> lists) : IHistoryReads
{
    private readonly Dictionary<(string Server, DateTime From, DateTime To, int Most), List<HistoryPoint>> _points = [];
    private readonly Dictionary<(string Server, DateTime From, DateTime To), List<StoredCensus>> _census = [];
    private readonly Dictionary<(string Server, DateTime From, DateTime To), List<StoredPlayer>> _players = [];
    private readonly Dictionary<(string Server, DateTime From, DateTime To), List<StoredFaction>> _factions = [];
    private readonly Dictionary<(string Server, DateTime From, DateTime To), Dictionary<HistoryDimension, List<StoredGroup>>> _groups = [];

    public List<HistoryPoint> Points(string serverId, DateTime from, DateTime to, int maxPoints) =>
        Once(_points, (serverId, from, to, maxPoints), () => store.Points(serverId, from, to, maxPoints));

    public List<StoredCensus> Census(string serverId, DateTime from, DateTime to) =>
        Once(_census, (serverId, from, to), () => store.Census(serverId, from, to));

    public List<StoredPlayer> Players(string serverId, DateTime from, DateTime to) =>
        Once(_players, (serverId, from, to), () => store.Players(serverId, from, to));

    public List<StoredFaction> Factions(string serverId, DateTime from, DateTime to) =>
        Once(_factions, (serverId, from, to), () => store.Factions(serverId, from, to));

    public List<StoredGroup> Groups(string serverId, HistoryDimension list, DateTime from, DateTime to)
    {
        Dictionary<HistoryDimension, List<StoredGroup>> rows = Once(_groups, (serverId, from, to), () => ByList(store.GroupsOf(serverId, lists, from, to)));

        // Shared, because a long range holds a lot of rows. Nothing that reads these changes them.
        return rows.TryGetValue(list, out List<StoredGroup>? found) ? found : [];
    }

    private static Dictionary<HistoryDimension, List<StoredGroup>> ByList(List<StoredGroup> rows)
    {
        var lists = new Dictionary<HistoryDimension, List<StoredGroup>>();

        foreach (StoredGroup row in rows)
        {
            if (!lists.TryGetValue(row.Dimension, out List<StoredGroup>? list))
            {
                list = [];
                lists[row.Dimension] = list;
            }

            list.Add(row);
        }

        return lists;
    }

    public List<StoredGroup> LatestGroups(string serverId, HistoryDimension list) => store.LatestGroups(serverId, list);

    public List<StoredMarker> Markers(string serverId, DateTime from, DateTime to) => store.Markers(serverId, from, to);

    private static TValue Once<TKey, TValue>(Dictionary<TKey, TValue> held, TKey key, Func<TValue> read)
        where TKey : notnull
    {
        if (!held.TryGetValue(key, out TValue? value))
        {
            value = read();
            held[key] = value;
        }

        return value;
    }
}
