using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SenX.Monitor.Api;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>A moment on a chart. Times are ms per frame.</summary>
public sealed class HistoryPoint
{
    public DateTime Time { get; set; }

    public float SimSpeed { get; set; }

    public float FrameP50 { get; set; }

    public float FrameP95 { get; set; }

    public float FrameP99 { get; set; }

    public float FrameMax { get; set; }

    public int Gen0 { get; set; }

    public int Gen1 { get; set; }

    public int Gen2 { get; set; }

    /// <summary>What the main thread allocated, in KB.</summary>
    public uint AllocatedKb { get; set; }

    /// <summary>True when the server was paused just before this, so a chart breaks its line here.</summary>
    public bool AfterGap { get; set; }

    /// <summary>KB sent a second, of every kind.</summary>
    public float SentKb { get; set; }

    /// <summary>KB received a second.</summary>
    public float ReceivedKb { get; set; }

    public float StateSyncKb { get; set; }

    public float EventsKb { get; set; }

    public float ReplicationKb { get; set; }

    /// <summary>RPCs from clients a second.</summary>
    public float RpcsIn { get; set; }

    /// <summary>Time per tree scope, indexed as in <see cref="HistoryScopes"/>.</summary>
    public float[] Scopes { get; set; } = [];

    /// <summary>How long this point covers. A minute with a gap in it covers less than a minute.</summary>
    public float Seconds { get; set; }

    /// <summary>The frames simulated across it.</summary>
    public uint SimulatedFrames { get; set; }
}

/// <summary>A line of a background total, with its name resolved.</summary>
public sealed record StoredGroup(DateTime Time, HistoryDimension Dimension, string Name, float MainMs, float WorkerMs, float CallsPerFrame, float PeakMs)
{
    /// <summary>A grid, player, faction or script id. Zero elsewhere, because other keys don't outlast a restart.</summary>
    public long Key { get; init; }

    /// <summary>For rows that count traffic. Zero for the rest.</summary>
    public float BytesPerSecond { get; init; }

    /// <summary>For grids, owners and factions, the blocks the last census found. Zero for the rest.</summary>
    public uint Blocks { get; init; }

    /// <summary>For owners and factions, the grids the last census found. Zero for the rest.</summary>
    public uint Grids { get; init; }

    /// <summary>What tells this row apart from another with the same name: its id if it has one, or else its name.</summary>
    public string Identity => Key != 0L ? "#" + Key.ToString(System.Globalization.CultureInfo.InvariantCulture) : Name;

    /// <summary>Which threads its time went on, as one word, or empty with no time. For tables.</summary>
    public string Thread => ThreadTags.Name(MonitorThreads.Of(MainMs, WorkerMs));

    /// <summary>The game thread's share of its time, for a bar.</summary>
    public double MainPercent => ThreadTags.MainPercent(MainMs, WorkerMs);
}

/// <summary>
/// A player online at a moment. A row with no name marks a moment when nobody was, so a chart can
/// tell an empty server from one it has no data for.
/// </summary>
public sealed record StoredPlayer(DateTime Time, string Name, string Faction, uint PingMs)
{
    public uint SentBytesPerSecond { get; init; }

    public uint ReceivedBytesPerSecond { get; init; }

    public uint RpcsPerSecond { get; init; }

    public uint QueuedBytes { get; init; }
}

/// <summary>A faction at a moment, by name, so its line carries on across restarts.</summary>
public sealed record StoredFaction(DateTime Time, string Name, uint Blocks, uint Pcu, uint Grids, uint OnlineMembers);

/// <summary>A census pass, with the time worked out.</summary>
public sealed record StoredCensus(DateTime Time, HistoryCensus Row);

/// <summary>What a marker on the charts stands for.</summary>
public enum StoredMarkerKind
{
    Save,
    Restart,
    Change,
    Lag,

    /// <summary>Something that changes what timing costs, so a bump on a chart has a reason.</summary>
    Measuring,

    /// <summary>One long frame just after a character spawned. Recorded, but not lag that sets anything off.</summary>
    JoinHitch,

    /// <summary>Something's time moved between the game thread and worker threads, and stayed. Never sets anything off.</summary>
    ThreadJump,
}

/// <summary>A moment worth a line on the charts, in words.</summary>
public sealed record StoredMarker(DateTime Time, StoredMarkerKind Kind, string Text)
{
    /// <summary>For a thread jump, the list the thing is in, so its name can be filled in once it's known. None for the rest.</summary>
    public HistoryDimension JumpList { get; init; }

    public long JumpKey { get; init; }

    public MonitorThread JumpFrom { get; init; }

    public MonitorThread JumpTo { get; init; }
}

/// <summary>What a server last started with.</summary>
internal sealed class ServerRuntime
{
    public long StartedUtcTicks { get; set; }

    public List<RuntimeItem> Plugins { get; set; } = [];

    public List<RuntimeItem> Mods { get; set; } = [];
}

/// <summary>Says what differs between two starts of a server.</summary>
public static class RuntimeChanges
{
    /// <summary>One line per difference. Ids say what's the same thing, because names can change.</summary>
    public static List<string> Between(string label, IReadOnlyList<RuntimeItem> before, IReadOnlyList<RuntimeItem> after)
    {
        Dictionary<string, string> was = before.DistinctBy(item => item.Id).ToDictionary(item => item.Id, item => item.Name);
        Dictionary<string, string> now = after.DistinctBy(item => item.Id).ToDictionary(item => item.Id, item => item.Name);
        var changes = new List<string>();

        foreach ((string id, string name) in now.OrderBy(item => item.Value, StringComparer.Ordinal))
        {
            if (!was.TryGetValue(id, out string? previous))
                changes.Add(label + " added: " + name);
            else if (previous != name)
                changes.Add(label + " changed: " + previous + " to " + name);
        }

        foreach ((string id, string name) in was.OrderBy(item => item.Value, StringComparer.Ordinal))
        {
            if (!now.ContainsKey(id))
                changes.Add(label + " removed: " + name);
        }

        return changes;
    }
}

/// <summary>A server with history here, connected or not. Names are kept so one that's gone can still be listed.</summary>
public sealed record KnownServer(string Id, string Name, string Cluster, DateTime LastSeen);

/// <summary>The players and factions from the latest pass.</summary>
public sealed record WorldNow(IReadOnlyList<HistoryPlayer> Players, IReadOnlyList<HistoryFaction> Factions);

/// <summary>How far the collector has got with one server. Sequences restart when the server does.</summary>
public sealed class CollectorPosition
{
    public long StartedUtcTicks { get; set; }

    public long NextRollup { get; set; }

    public long NextCensus { get; set; }

    public long NextGroups { get; set; }

    public long NextDetail { get; set; }

    public long NextMarkers { get; set; }

    /// <summary>The latest sample kept, as UTC ticks. This and the next few let a restart of Quasar carry on the same.</summary>
    public long LastSample { get; set; }

    public long SampleBefore { get; set; }

    public long LagUntil { get; set; }

    /// <summary>A sample kept that nobody has been told about yet.</summary>
    public long PendingSample { get; set; }
}

/// <summary>
/// Quasar's copy of each server's history, in files under its install directory: seconds for 6 hours,
/// then minutes, census passes, players each minute, two-minute background totals and markers for as long as the
/// admin's retention setting says, which is 3 days unless changed and 7 at most. Old files are deleted, so
/// storage stays small. Recent rows stay in memory too, so a page refreshing every second never reads a
/// file. A storage failure is logged and never stops collection.
/// </summary>
public sealed class HistoryStore(string directory, MonitorSettings settings, ILogger<HistoryStore> logger) : IHistoryReads
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _damagedFiles = new();

    private const int MaxScopesInRow = 4096;

    private static readonly TimeSpan s_seconds = TimeSpan.FromHours(6);
    private static readonly TimeSpan s_playerStep = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_recent = TimeSpan.FromHours(1);
    private static readonly TimeSpan s_recentCensus = TimeSpan.FromHours(24);
    private static readonly TimeSpan s_pruneEvery = TimeSpan.FromHours(1);

    // File names sort in time order, so they must use the same calendar whatever the culture: a Thai or Persian
    // year would sort after or before every other file, and pruning would delete them all.
    private const string DayFiles = "yyyyMMdd";
    private const string HourFiles = "yyyyMMddHH";

    private static string FileName(DateTime time, string format) => time.ToString(format, CultureInfo.InvariantCulture);
    private static readonly TimeSpan s_noteLastSeenEvery = TimeSpan.FromHours(1);

    // Bytes in a stored group row after its name: four times, its key, its traffic and its two counts.
    private const int GroupBytesAfterName = (4 * sizeof(float)) + sizeof(long) + sizeof(float) + (2 * sizeof(uint));

    // Stands for a row that wasn't asked for. Its time is outside any range, so it's never kept.
    private static readonly StoredGroup s_skippedGroup = new(DateTime.MinValue, HistoryDimension.None, string.Empty, 0f, 0f, 0f, 0f);

    private readonly object _gate = new();
    private readonly Dictionary<string, ServerHistory> _servers = [];
    private readonly ConcurrentDictionary<string, Newest> _newest = new();
    private Dictionary<string, KnownServer>? _known;

    /// <summary>Raised when a server gets a new marker, with its server. Listeners mustn't wait, because this is under the store's lock.</summary>
    public event Action<string, StoredMarker>? MarkerAdded;

    /// <summary>
    /// A background total was kept as a sample and all of it is here, with its server, its time and the sample
    /// before it, which is default if there wasn't one. Raised under the store's lock, so nothing here may wait.
    /// </summary>
    public event Action<string, DateTime, DateTime>? SampleKept;

    // What the newest figures are, for reads that mustn't wait for a batch to be written.
    private sealed record Newest(HistoryPoint? Point, StoredCensus? Census, long StartedUtcTicks);

    private sealed class ServerHistory
    {
        public required string Id { get; init; }

        public required string Directory { get; init; }

        public CollectorPosition Position { get; set; } = new();

        public List<HistoryPoint> Recent { get; } = [];

        public List<StoredGroup> RecentGroups { get; } = [];

        public List<StoredCensus> RecentCensus { get; } = [];

        public List<StoredPlayer> RecentPlayers { get; } = [];

        public List<StoredFaction> RecentFactions { get; } = [];

        public string GcMode { get; set; } = string.Empty;

        /// <summary>Every marker kept, as they're few and each chart refresh wants them.</summary>
        public List<StoredMarker> Markers { get; } = [];

        /// <summary>Thread jumps still showing an id, newest last, until a normal row brings the name.</summary>
        public List<StoredMarker> UnnamedJumps { get; } = [];

        public DateTime LastStoredPlayers { get; set; }

        public SampleRate Rate { get; } = new();

        /// <summary>A sample nobody has been told about yet, because more of its rows could still arrive.</summary>
        public DateTime PendingSample { get; set; }

        /// <summary>The total whose rows are still arriving, if a batch filled up partway through one.</summary>
        public DateTime OpenTotal { get; set; }

        /// <summary>The latest sweep's grids by id, because an entity table asks for every row it draws.</summary>
        public Dictionary<long, StoredGroup> LatestGrids { get; } = [];

        public DateTime LatestGridsTime { get; set; }

        public WorldNow World { get; set; } = new([], []);

        public Bucket Minute { get; } = new();

        public DateTime LastPrune { get; set; }
    }

    /// <summary>Adds points up until their minute or hour ends.</summary>
    private sealed class Bucket
    {
        public DateTime Start;
        public int Count;
        public float Sim;
        public float P50;
        public float P95;
        public float P99;
        public float Max;
        public int Gen0;
        public int Gen1;
        public int Gen2;
        public uint AllocatedKb;
        public float SentKb;
        public float ReceivedKb;
        public float StateSyncKb;
        public float EventsKb;
        public float ReplicationKb;
        public float RpcsIn;
        public float Seconds;
        public uint SimulatedFrames;
        public float[] Scopes = [];

        public void Add(HistoryPoint point)
        {
            if (Scopes.Length != point.Scopes.Length)
                Scopes = new float[point.Scopes.Length];

            Count++;
            Sim += point.SimSpeed;
            P50 += point.FrameP50;
            P95 += point.FrameP95;
            P99 = Math.Max(P99, point.FrameP99);
            Max = Math.Max(Max, point.FrameMax);
            Gen0 += point.Gen0;
            Gen1 += point.Gen1;
            Gen2 += point.Gen2;
            AllocatedKb += point.AllocatedKb;
            SentKb += point.SentKb;
            ReceivedKb += point.ReceivedKb;
            StateSyncKb += point.StateSyncKb;
            EventsKb += point.EventsKb;
            ReplicationKb += point.ReplicationKb;
            RpcsIn += point.RpcsIn;
            Seconds += point.Seconds;
            SimulatedFrames += point.SimulatedFrames;

            for (int i = 0; i < Scopes.Length; i++)
                Scopes[i] += point.Scopes[i];
        }

        public HistoryPoint Finish()
        {
            var point = new HistoryPoint
            {
                Time = Start,
                SimSpeed = Sim / Count,
                FrameP50 = P50 / Count,
                FrameP95 = P95 / Count,
                FrameP99 = P99,
                FrameMax = Max,
                Gen0 = Gen0,
                Gen1 = Gen1,
                Gen2 = Gen2,
                AllocatedKb = AllocatedKb,
                SentKb = SentKb / Count,
                ReceivedKb = ReceivedKb / Count,
                StateSyncKb = StateSyncKb / Count,
                EventsKb = EventsKb / Count,
                ReplicationKb = ReplicationKb / Count,
                RpcsIn = RpcsIn / Count,
                Seconds = Seconds,
                SimulatedFrames = SimulatedFrames,
                Scopes = Scopes.Select(value => value / Count).ToArray(),
            };

            Count = 0;
            Sim = P50 = P95 = P99 = Max = 0f;
            Gen0 = Gen1 = Gen2 = 0;
            AllocatedKb = 0U;
            SentKb = ReceivedKb = StateSyncKb = EventsKb = ReplicationKb = RpcsIn = Seconds = 0f;
            SimulatedFrames = 0U;
            Array.Clear(Scopes);
            return point;
        }
    }

    /// <summary>Says a server is here, which is what lets it be listed after it's gone.</summary>
    public void NoteServer(MonitorServer server)
    {
        lock (_gate)
        {
            Dictionary<string, KnownServer> known = Known();
            DateTime now = DateTime.UtcNow;

            if (known.TryGetValue(server.HistoryId, out KnownServer? last)
                && last.Name == server.Name
                && last.Cluster == server.Cluster
                && now - last.LastSeen < s_noteLastSeenEvery)
            {
                return;
            }

            var noted = new KnownServer(server.HistoryId, server.Name, server.Cluster, now);
            known[server.HistoryId] = noted;

            ServerHistory history = Server(server.HistoryId);

            Guarded(() =>
            {
                Directory.CreateDirectory(history.Directory);
                StateFiles.Write(Path.Combine(history.Directory, "server.json"), JsonSerializer.Serialize(noted));
            });
        }
    }

    /// <summary>Every server there's history for, connected or not.</summary>
    public List<KnownServer> KnownServers()
    {
        lock (_gate)
            return Known().Values.OrderBy(server => server.Name, StringComparer.Ordinal).ToList();
    }

    private Dictionary<string, KnownServer> Known()
    {
        if (_known != null)
            return _known;

        _known = [];

        if (!Directory.Exists(directory))
            return _known;

        foreach (string folder in Directory.GetDirectories(directory))
        {
            string path = Path.Combine(folder, "server.json");

            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<KnownServer>(File.ReadAllText(path)) is { Id: not null } server)
                    _known[server.Id] = server;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Monitor could not read which server a folder of history belongs to.");
            }
        }

        return _known;
    }

    /// <summary>
    /// Keeps a server's files in check when no new data is arriving to do it, and removes a server
    /// nothing has been heard from for longer than history is kept. Without this, one that's gone
    /// would keep its files forever.
    /// </summary>
    public void PruneQuietServers()
    {
        lock (_gate)
        {
            DateTime oldest = DateTime.UtcNow - settings.Retention;

            foreach (KnownServer known in Known().Values.ToList())
            {
                ServerHistory server = Server(known.Id);

                if (known.LastSeen >= oldest)
                {
                    PruneIfDue(server);
                    continue;
                }

                bool gone = false;

                Guarded(() =>
                {
                    if (Directory.Exists(server.Directory))
                        Directory.Delete(server.Directory, recursive: true);

                    gone = true;
                });

                // Forgetting it now would leave its files, with players' names in them, for good. It's tried again next time.
                if (!gone)
                    continue;

                _known!.Remove(known.Id);
                _servers.Remove(known.Id);
                _newest.TryRemove(known.Id, out _);
            }

            PruneOrphans(oldest);
        }
    }

    // A folder whose server.json is gone or damaged belongs to no known server, so nothing else would ever clear it.
    // It's only touched if it has Monitor's own position file, and nothing in it is newer than the retention setting.
    private void PruneOrphans(DateTime oldest)
    {
        if (!Directory.Exists(directory))
            return;

        var owned = new HashSet<string>(Known().Keys.Select(id => Path.Combine(directory, StateFiles.FolderFor(id))), StringComparer.OrdinalIgnoreCase);

        foreach (string folder in Directory.GetDirectories(directory))
        {
            if (owned.Contains(folder) || !File.Exists(Path.Combine(folder, "position.json")))
                continue;

            if (Directory.GetFiles(folder, "*", SearchOption.AllDirectories).All(file => File.GetLastWriteTimeUtc(file) < oldest))
                Guarded(() => Directory.Delete(folder, recursive: true));
        }
    }

    /// <summary>Applies a shorter retention on the next batch, not up to an hour later.</summary>
    public void PruneSoon()
    {
        lock (_gate)
        {
            foreach (ServerHistory server in _servers.Values)
                server.LastPrune = default;
        }
    }

    public CollectorPosition Position(string serverId)
    {
        lock (_gate)
            return Server(serverId).Position;
    }

    /// <summary>Starts again from the first row, because a restarted server's sequence numbers did too.</summary>
    public void Restart(string serverId, long startedUtcTicks)
    {
        lock (_gate)
        {
            ServerHistory server = Server(serverId);
            server.Position = new CollectorPosition { StartedUtcTicks = startedUtcTicks };
            SavePosition(server);

            _newest[serverId] = _newest.TryGetValue(serverId, out Newest? newest)
                ? newest with { StartedUtcTicks = startedUtcTicks }
                : new Newest(null, null, startedUtcTicks);
        }
    }

    /// <summary>
    /// Stores a batch and remembers how far it went. Names must already be resolved, apart from thread jumps', which
    /// take a name only if <paramref name="knownName"/> already has it. Without that, a name that is just the key counts as unknown.
    /// </summary>
    public void Add(string serverId, HistoryBatch batch, Func<HistoryDimension, long, string> nameOf, Func<HistoryDimension, long, string?>? knownName = null)
    {
        string? Known(HistoryDimension list, long key)
        {
            if (knownName != null)
                return knownName(list, key);

            string name = nameOf(list, key);
            return name == key.ToString(System.Globalization.CultureInfo.InvariantCulture) ? null : name;
        }

        DateTime started = new(batch.StartedUtcTicks, DateTimeKind.Utc);

        lock (_gate)
        {
            ServerHistory server = Server(serverId);

            foreach (HistoryRollup row in batch.Rollups)
                AddPoint(server, ToPoint(row, started));

            foreach (HistoryCensus row in batch.Census)
                AddCensus(server, new StoredCensus(started.AddTicks(row.UnitsSinceStart), row));

            foreach (HistoryGroup row in batch.Groups)
            {
                if (row.Dimension != HistoryDimension.ThreadJumps)
                    AddGroup(server, ToGroup(row, started, nameOf(row.Dimension, row.Key)));
            }

            // After the costs, whose names may be the ones a jump needs. A jump isn't a cost, so it's a marker.
            foreach (HistoryGroup row in batch.Groups)
            {
                if (row.Dimension == HistoryDimension.ThreadJumps)
                    AddJump(server, row, started, Known(row.JumpedList, row.Key));
            }

            NameJumps(server, Known);

            foreach (HistoryMarker row in batch.Markers)
            {
                // That one only says a chat run is ready to collect, and the run's own start is already marked.
                if (row.Kind != HistoryMarkerKind.RunFinished)
                    AddMarker(server, ToMarker(row, started));
            }

            // Totals are only sent whole, so a batch that reaches the head has everything of the last one.
            bool caughtUp = batch.NextGroups >= batch.GroupHead;

            if (batch.Groups.Count > 0)
                server.OpenTotal = caughtUp ? default : started.AddTicks(batch.Groups[^1].UnitsSinceStart);

            if (caughtUp && server.PendingSample != default)
                TellOfSample(server);

            if (batch.HasDetail)
            {
                server.World = new WorldNow(batch.Players, batch.Factions);

                // Players come from the same pass as its census row, which says when that was.
                DateTime time = batch.Census.Count > 0 ? started.AddTicks(batch.Census[^1].UnitsSinceStart) : DateTime.UtcNow;
                AddPlayers(server, time, batch.Players, batch.Factions, nameOf);
            }

            server.Position = new CollectorPosition
            {
                StartedUtcTicks = batch.StartedUtcTicks,
                NextRollup = batch.NextRollup,
                NextCensus = batch.NextCensus,
                NextGroups = batch.NextGroups,
                NextDetail = batch.DetailSequence,
                NextMarkers = batch.NextMarkers,
                LastSample = server.Rate.Last.Ticks,
                SampleBefore = server.Rate.Before.Ticks,
                LagUntil = server.Rate.LagUntil.Ticks,
                PendingSample = server.PendingSample.Ticks,
            };

            _newest[serverId] = new Newest(
                server.Recent.Count > 0 ? server.Recent[^1] : null,
                server.RecentCensus.Count > 0 ? server.RecentCensus[^1] : null,
                batch.StartedUtcTicks);

            SavePosition(server);
            PruneIfDue(server);
        }
    }

    private void TellOfSample(ServerHistory server)
    {
        DateTime time = server.PendingSample;

        server.PendingSample = default;
        SampleKept?.Invoke(server.Id, time, server.Rate.Last == time ? server.Rate.Before : default);
    }

    /// <summary>
    /// Notes what a server started with. A new start gets a marker, and so does each plugin or mod that
    /// differs from the start before. Quasar restarting asks again, and that changes nothing.
    /// </summary>
    public void ServerStarted(string serverId, long startedUtcTicks, IReadOnlyList<RuntimeItem> plugins, IReadOnlyList<RuntimeItem> mods, string gcMode = "")
    {
        lock (_gate)
        {
            ServerHistory server = Server(serverId);
            ServerRuntime? last = LoadRuntime(server);

            server.GcMode = gcMode;

            if (last?.StartedUtcTicks == startedUtcTicks)
                return;

            var time = new DateTime(startedUtcTicks, DateTimeKind.Utc);
            AddMarker(server, new StoredMarker(time, StoredMarkerKind.Restart, "Server started"));

            if (last != null)
            {
                foreach (string change in RuntimeChanges.Between("Plugin", last.Plugins, plugins).Concat(RuntimeChanges.Between("Mod", last.Mods, mods)))
                    AddMarker(server, new StoredMarker(time, StoredMarkerKind.Change, change));
            }

            SaveRuntime(server, new ServerRuntime { StartedUtcTicks = startedUtcTicks, Plugins = [.. plugins], Mods = [.. mods] });
        }
    }

    /// <summary>What happened in a range, newest first.</summary>
    public List<StoredMarker> Markers(string serverId, DateTime from, DateTime to)
    {
        lock (_gate)
            return Server(serverId).Markers.Where(marker => marker.Time >= from && marker.Time <= to).OrderByDescending(marker => marker.Time).ToList();
    }

    /// <summary>
    /// Points for a chart, from the finest history that covers the range, thinned to fit. Files are read
    /// outside the lock, so a long range can't hold up collection or anyone else's page.
    /// </summary>
    public List<HistoryPoint> Points(string serverId, DateTime from, DateTime to, int maxPoints)
    {
        ServerHistory server;

        lock (_gate)
        {
            server = Server(serverId);

            // After a Quasar restart memory is empty until it fills, and the files have what it's missing.
            if (to - from <= s_recent && Covers(server.Recent, from, point => point.Time))
                return Thin(server.Recent.Where(point => point.Time >= from && point.Time <= to).ToList(), maxPoints);
        }

        // Seconds are only kept so long, so an old range needs minutes however short it is.
        bool seconds = to - from <= s_seconds && DateTime.UtcNow - from <= s_seconds;

        List<HistoryPoint> points = seconds
            ? ReadPoints(server, "seconds", from, to, HourFiles)
            : ReadPoints(server, "minutes", from, to, DayFiles);

        return Thin(points, maxPoints);
    }

    public List<StoredCensus> Census(string serverId, DateTime from, DateTime to)
    {
        ServerHistory server;

        lock (_gate)
        {
            server = Server(serverId);

            if (to - from <= s_recentCensus && Covers(server.RecentCensus, from, row => row.Time))
                return server.RecentCensus.Where(row => row.Time >= from && row.Time <= to).ToList();
        }

        return ReadDays(server, "census", from, to, ReadCensus, row => row.Time);
    }

    /// <summary>The latest whole total for one list.</summary>
    public List<StoredGroup> LatestGroups(string serverId, HistoryDimension list)
    {
        lock (_gate)
        {
            ServerHistory server = Server(serverId);
            List<StoredGroup> rows = server.RecentGroups;
            int last = rows.Count - 1;

            while (last >= 0 && rows[last].Time == server.OpenTotal)
                last--;

            if (last < 0)
                return [];

            DateTime latest = rows[last].Time;
            return rows.Where(row => row.Time == latest && row.Dimension == list).OrderByDescending(row => row.MainMs + row.WorkerMs).ThenByDescending(row => row.BytesPerSecond).ToList();
        }
    }

    /// <summary>A grid's cost in the latest total, if it was in that total's top 20.</summary>
    public StoredGroup? LatestGridCost(string serverId, long gridId)
    {
        lock (_gate)
            return Server(serverId).LatestGrids.GetValueOrDefault(gridId);
    }

    /// <summary>Who was online, a row per player per minute.</summary>
    public List<StoredPlayer> Players(string serverId, DateTime from, DateTime to)
    {
        ServerHistory server;

        lock (_gate)
        {
            server = Server(serverId);

            if (to - from <= s_recent && Covers(server.RecentPlayers, from, row => row.Time))
                return server.RecentPlayers.Where(row => row.Time >= from && row.Time <= to).ToList();
        }

        return ReadDays(server, "players", from, to, ReadPlayer, row => row.Time);
    }

    /// <summary>Each faction's size over time, a row per faction per minute.</summary>
    public List<StoredFaction> Factions(string serverId, DateTime from, DateTime to)
    {
        ServerHistory server;

        lock (_gate)
        {
            server = Server(serverId);

            if (to - from <= s_recent && Covers(server.RecentFactions, from, row => row.Time))
                return server.RecentFactions.Where(row => row.Time >= from && row.Time <= to).ToList();
        }

        return ReadDays(server, "factions", from, to, ReadFaction, row => row.Time);
    }

    /// <summary>How the server's garbage collector is set up, once it has said.</summary>
    public string GcMode(string serverId)
    {
        lock (_gate)
            return Server(serverId).GcMode;
    }

    /// <summary>One list over time, for its line charts.</summary>
    public List<StoredGroup> Groups(string serverId, HistoryDimension list, DateTime from, DateTime to) =>
        GroupsOf(serverId, [list], from, to);

    /// <summary>
    /// Several lists over time. A short range gets every total memory has, which is the last hour's, and
    /// files fill in what's older. That covers a restart of Quasar, when memory only has what's come since.
    /// </summary>
    public List<StoredGroup> GroupsOf(string serverId, IReadOnlyCollection<HistoryDimension> lists, DateTime from, DateTime to)
    {
        ServerHistory server;
        var recent = new List<StoredGroup>();
        DateTime lastInFiles = to;

        lock (_gate)
        {
            server = Server(serverId);

            if (to - from <= s_recent && server.RecentGroups.Count > 0)
            {
                recent.AddRange(server.RecentGroups.Where(row => row.Time >= from && row.Time <= to && lists.Contains(row.Dimension)));

                DateTime beforeMemory = server.RecentGroups[0].Time.AddTicks(-1L);

                if (beforeMemory < to)
                    lastInFiles = beforeMemory;
            }
        }

        if (lastInFiles < from)
            return recent;

        List<StoredGroup> rows = ReadGroups(server, lists, from, lastInFiles);
        rows.AddRange(recent);
        return rows;
    }

    /// <summary>Every list over a long stretch, which always comes from files.</summary>
    public List<StoredGroup> AllGroups(string serverId, DateTime from, DateTime to)
    {
        ServerHistory server;

        lock (_gate)
            server = Server(serverId);

        return ReadGroups(server, null, from, to);
    }

    /// <summary>
    /// The samples kept across a range, which are the totals in the files, without one still arriving. Every
    /// sample has at least a row with no list, so one with nothing in these lists still shows up.
    /// </summary>
    public List<StoredGroup> SampleGroups(string serverId, IReadOnlyCollection<HistoryDimension> lists, DateTime from, DateTime to)
    {
        ServerHistory server;
        DateTime open;

        lock (_gate)
        {
            server = Server(serverId);
            open = server.OpenTotal;
        }

        List<StoredGroup> rows = ReadGroups(server, lists, from, to, markTotals: true);

        return open == default ? rows : rows.Where(row => row.Time != open).ToList();
    }

    // Rows nobody asked for are skipped unread, because building every row of a long range would be a lot of garbage inside a game server.
    private List<StoredGroup> ReadGroups(ServerHistory server, IReadOnlyCollection<HistoryDimension>? lists, DateTime from, DateTime to, bool markTotals = false)
    {
        HashSet<HistoryDimension>? wanted = lists == null ? null : [.. lists];
        long marked = 0L;

        if (from <= s_skippedGroup.Time)
            from = s_skippedGroup.Time.AddTicks(1L);

        StoredGroup Read(BinaryReader reader)
        {
            var time = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var dimension = (HistoryDimension)reader.ReadUInt32();
            bool inRange = time >= from && time <= to;

            if (inRange && (wanted == null || wanted.Contains(dimension)))
                return ReadGroup(reader, time, dimension);

            SkipGroup(reader);

            if (!markTotals || !inRange || time.Ticks == marked)
                return s_skippedGroup;

            marked = time.Ticks;
            return new StoredGroup(time, HistoryDimension.None, string.Empty, 0f, 0f, 0f, 0f);
        }

        return ReadDays(server, "groups", from, to, Read, row => row.Time);
    }

    public WorldNow World(string serverId)
    {
        lock (_gate)
            return Server(serverId).World;
    }

    /// <summary>The newest second. This doesn't wait for a batch being written, so it's safe to call from anywhere.</summary>
    public HistoryPoint? Latest(string serverId) => _newest.GetValueOrDefault(serverId)?.Point;

    /// <summary>The newest census. This doesn't wait either.</summary>
    public HistoryCensus? LatestCensus(string serverId) => _newest.GetValueOrDefault(serverId)?.Census?.Row;

    /// <summary>The same with its time.</summary>
    public StoredCensus? LatestCensusRow(string serverId) => _newest.GetValueOrDefault(serverId)?.Census;

    /// <summary>When a server last started, as UTC ticks, or zero if that isn't known. This only waits the first time a server's asked about.</summary>
    public long StartedUtcTicks(string serverId)
    {
        if (_newest.TryGetValue(serverId, out Newest? newest))
            return newest.StartedUtcTicks;

        long started = Position(serverId).StartedUtcTicks;
        _newest.TryAdd(serverId, new Newest(null, null, started));
        return started;
    }

    private static bool Covers<T>(List<T> rows, DateTime from, Func<T, DateTime> timeOf) =>
        rows.Count > 0 && timeOf(rows[0]) <= from;

    private static HistoryPoint ToPoint(HistoryRollup row, DateTime started)
    {
        float frames = Math.Max(row.Frames, 1U);

        return new HistoryPoint
        {
            Time = started.AddTicks(row.UnitsSinceStart),
            SimSpeed = (float)row.SimSpeed,
            FrameP50 = row.FrameP50 / 10000f,
            FrameP95 = row.FrameP95 / 10000f,
            FrameP99 = row.FrameP99 / 10000f,
            FrameMax = row.FrameMax / 10000f,
            Gen0 = (int)row.Gen0,
            Gen1 = (int)row.Gen1,
            Gen2 = (int)row.Gen2,
            AllocatedKb = row.AllocatedKb,
            AfterGap = row.AfterGap,
            SentKb = row.SentKb,
            ReceivedKb = row.ReceivedKb,
            StateSyncKb = row.StateSyncKb,
            EventsKb = row.EventsKb,
            ReplicationKb = row.ReplicationKb,
            RpcsIn = row.RpcsIn,
            Seconds = row.WindowUnits / 10_000_000f,
            SimulatedFrames = row.SimulatedFrames,
            Scopes = row.ScopeUnits.Select(units => units / frames / 10000f).ToArray(),
        };
    }

    private static StoredGroup ToGroup(HistoryGroup row, DateTime started, string name) => new(
        started.AddTicks(row.UnitsSinceStart),
        row.Dimension,
        name,
        row.MainUnits / 10000f,
        row.WorkerUnits / 10000f,
        row.CallsPerFrame,
        row.MaxUnits / 10000f)
    {
        Key = row.Dimension is HistoryDimension.Grids or HistoryDimension.Owners or HistoryDimension.Factions or HistoryDimension.Scripts ? row.Key : 0L,
        BytesPerSecond = row.BytesPerSecond,
        Blocks = row.Blocks,
        Grids = row.Grids,
    };

    private const int MostUnnamedJumps = 256;

    private void AddJump(ServerHistory server, HistoryGroup row, DateTime started, string? name)
    {
        DateTime time = started.AddTicks(row.UnitsSinceStart);

        if (row.JumpsNotListed > 0U)
        {
            AddMarker(server, new StoredMarker(time, StoredMarkerKind.ThreadJump, ThreadTags.NotListedText(row.JumpsNotListed)));
            return;
        }

        var marker = new StoredMarker(time, StoredMarkerKind.ThreadJump, JumpText(row.JumpedList, row.Key, name, row.JumpFrom, row.JumpTo))
        {
            JumpList = row.JumpedList,
            JumpKey = row.Key,
            JumpFrom = row.JumpFrom,
            JumpTo = row.JumpTo,
        };

        AddMarker(server, marker);

        if (name != null)
            return;

        server.UnnamedJumps.Add(marker);
        if (server.UnnamedJumps.Count > MostUnnamedJumps)
            server.UnnamedJumps.RemoveAt(0);
    }

    // Fills in names that have turned up since, in memory. The file keeps the id, which a restart shows.
    private static void NameJumps(ServerHistory server, Func<HistoryDimension, long, string?> known)
    {
        for (int i = server.UnnamedJumps.Count - 1; i >= 0; i--)
        {
            StoredMarker marker = server.UnnamedJumps[i];
            if (known(marker.JumpList, marker.JumpKey) is not { } name)
                continue;

            server.UnnamedJumps.RemoveAt(i);

            int at = server.Markers.IndexOf(marker);
            if (at >= 0)
                server.Markers[at] = marker with { Text = JumpText(marker.JumpList, marker.JumpKey, name, marker.JumpFrom, marker.JumpTo) };
        }
    }

    private static string JumpText(HistoryDimension list, long key, string? name, MonitorThread from, MonitorThread to) =>
        ThreadTags.JumpText(list, name ?? "#" + key.ToString(System.Globalization.CultureInfo.InvariantCulture), from, to);

    private static string DetailName(uint mode) => mode switch
    {
        0U => "off",
        1U => "grouped",
        _ => "everything",
    };

    private static StoredMarker ToMarker(HistoryMarker row, DateTime started)
    {
        DateTime time = started.AddTicks(row.UnitsSinceStart);

        return row.Kind switch
        {
            HistoryMarkerKind.LagTripped => new StoredMarker(time, StoredMarkerKind.Lag, "Lag noticed"),
            HistoryMarkerKind.SaveFailed => new StoredMarker(time, StoredMarkerKind.Save, "World save failed"),
            HistoryMarkerKind.RunStarted => new StoredMarker(time, StoredMarkerKind.Measuring, "Run for " + row.Value + " s"),
            HistoryMarkerKind.DetailChanged => new StoredMarker(time, StoredMarkerKind.Measuring, "Detail set to " + DetailName(row.Value)),
            HistoryMarkerKind.JoinHitch => new StoredMarker(time, StoredMarkerKind.JoinHitch, "Join hitch (" + row.Value + " ms)"),
            _ => new StoredMarker(time, StoredMarkerKind.Save, row.Value > 0U ? "World saved (" + row.Value + " ms)" : "World saved"),
        };
    }

    private void AddPoint(ServerHistory server, HistoryPoint point)
    {
        server.Rate.NoteSimSpeed(point.Time, point.SimSpeed);

        server.Recent.Add(point);
        Trim(server.Recent, point.Time - s_recent, row => row.Time);

        Append(server, "seconds", FileName(point.Time, HourFiles), writer => WritePoint(writer, point));

        Roll(server, server.Minute, point, new DateTime(point.Time.Ticks - (point.Time.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc));
    }

    private void Roll(ServerHistory server, Bucket bucket, HistoryPoint point, DateTime start)
    {
        if (bucket.Count > 0 && start != bucket.Start)
        {
            HistoryPoint finished = bucket.Finish();
            Append(server, "minutes", FileName(finished.Time, DayFiles), writer => WritePoint(writer, finished));
        }

        bucket.Start = start;
        bucket.Add(point);
    }

    private void AddCensus(ServerHistory server, StoredCensus row)
    {
        server.RecentCensus.Add(row);
        Trim(server.RecentCensus, row.Time - s_recentCensus, stored => stored.Time);
        Append(server, "census", FileName(row.Time, DayFiles), writer => WriteCensus(writer, row));
    }

    private void AddGroup(ServerHistory server, StoredGroup row)
    {
        if (row.Dimension == HistoryDimension.Grids)
        {
            if (row.Time != server.LatestGridsTime)
            {
                server.LatestGrids.Clear();
                server.LatestGridsTime = row.Time;
            }

            server.LatestGrids[row.Key] = row;
        }

        // A later total means the last one is whole, which happens when a backlog comes in one batch.
        if (server.PendingSample != default && row.Time != server.PendingSample)
            TellOfSample(server);

        server.RecentGroups.Add(row);
        Trim(server.RecentGroups, row.Time - s_recent, stored => stored.Time);

        if (!server.Rate.Keep(row.Time))
            return;

        server.PendingSample = row.Time;
        Append(server, "groups", FileName(row.Time, DayFiles), writer => WriteGroup(writer, row));
    }

    private void AddMarker(ServerHistory server, StoredMarker row)
    {
        server.Markers.Add(row);
        Append(server, "markers", FileName(row.Time, DayFiles), writer => WriteMarker(writer, row));
        MarkerAdded?.Invoke(server.Id, row);
    }

    private void AddPlayers(ServerHistory server, DateTime time, IReadOnlyList<HistoryPlayer> players, IReadOnlyList<HistoryFaction> factions, Func<HistoryDimension, long, string> nameOf)
    {
        if (time - server.LastStoredPlayers < s_playerStep)
            return;

        server.LastStoredPlayers = time;

        var rows = new List<StoredPlayer>(Math.Max(players.Count, 1));

        foreach (HistoryPlayer player in players)
        {
            rows.Add(new StoredPlayer(time, nameOf(HistoryDimension.Owners, player.IdentityId), nameOf(HistoryDimension.Factions, player.FactionId), player.PingMs)
            {
                SentBytesPerSecond = player.SentBytesPerSecond,
                ReceivedBytesPerSecond = player.ReceivedBytesPerSecond,
                RpcsPerSecond = player.RpcsPerSecond,
                QueuedBytes = player.QueuedBytes,
            });
        }

        if (rows.Count == 0)
            rows.Add(new StoredPlayer(time, string.Empty, string.Empty, 0U));

        server.RecentPlayers.AddRange(rows);
        Trim(server.RecentPlayers, time - s_recent, row => row.Time);

        Append(server, "players", FileName(time, DayFiles), writer =>
        {
            foreach (StoredPlayer row in rows)
                WritePlayer(writer, row);
        });

        List<StoredFaction> factionRows = factions
            .Select(row => new StoredFaction(time, nameOf(HistoryDimension.Factions, row.FactionId), row.Blocks, row.Pcu, row.Grids, row.OnlineMembers))
            .ToList();

        server.RecentFactions.AddRange(factionRows);
        Trim(server.RecentFactions, time - s_recent, row => row.Time);

        Append(server, "factions", FileName(time, DayFiles), writer =>
        {
            foreach (StoredFaction row in factionRows)
                WriteFaction(writer, row);
        });
    }

    private static void Trim<T>(List<T> rows, DateTime oldest, Func<T, DateTime> timeOf)
    {
        // Trimming a block at a time keeps this from shifting the whole list on every row.
        if (rows.Count < 2 || timeOf(rows[0]) >= oldest - TimeSpan.FromMinutes(5))
            return;

        int stale = rows.FindIndex(row => timeOf(row) >= oldest);
        rows.RemoveRange(0, stale < 0 ? rows.Count : stale);
    }

    private static List<HistoryPoint> Thin(List<HistoryPoint> points, int maxPoints)
    {
        if (points.Count <= maxPoints)
            return points;

        int stride = (int)Math.Ceiling(points.Count / (double)maxPoints);
        var thinned = new List<HistoryPoint>(maxPoints + 1);

        // Keeps the worst of each stretch, because a dip is what someone is looking for.
        for (int i = 0; i < points.Count; i += stride)
        {
            int end = Math.Min(i + stride, points.Count);
            HistoryPoint worst = points[i];

            for (int j = i + 1; j < end; j++)
            {
                if (points[j].SimSpeed < worst.SimSpeed)
                    worst = points[j];
            }

            thinned.Add(worst);
        }

        return thinned;
    }

    private ServerHistory Server(string serverId)
    {
        if (_servers.TryGetValue(serverId, out ServerHistory? server))
            return server;

        server = new ServerHistory { Id = serverId, Directory = Path.Combine(directory, StateFiles.FolderFor(serverId)) };
        server.Position = LoadPosition(server);
        server.PendingSample = TimeOf(server.Position.PendingSample);
        server.Rate.Restore(TimeOf(server.Position.LastSample), TimeOf(server.Position.SampleBefore), TimeOf(server.Position.LagUntil));

        DateTime now = DateTime.UtcNow;
        server.Markers.AddRange(ReadDays(server, "markers", now - settings.Retention, now, ReadMarker, row => row.Time));

        _servers[serverId] = server;
        return server;
    }

    // These come from a file, and a value that isn't a time must not stop collection.
    private static DateTime TimeOf(long ticks) =>
        ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : default;

    private CollectorPosition LoadPosition(ServerHistory server)
    {
        try
        {
            string path = Path.Combine(server.Directory, "position.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<CollectorPosition>(File.ReadAllText(path)) ?? new CollectorPosition()
                : new CollectorPosition();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Monitor could not read where it got to, so it will collect again from the start.");
            return new CollectorPosition();
        }
    }

    private void SavePosition(ServerHistory server)
    {
        Guarded(() =>
        {
            Directory.CreateDirectory(server.Directory);
            StateFiles.Write(Path.Combine(server.Directory, "position.json"), JsonSerializer.Serialize(server.Position));
        });
    }

    private ServerRuntime? LoadRuntime(ServerHistory server)
    {
        try
        {
            string path = Path.Combine(server.Directory, "runtime.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<ServerRuntime>(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Monitor could not read what this server last started with, so it can't say what changed.");
            return null;
        }
    }

    private void SaveRuntime(ServerHistory server, ServerRuntime runtime)
    {
        Guarded(() =>
        {
            Directory.CreateDirectory(server.Directory);
            StateFiles.Write(Path.Combine(server.Directory, "runtime.json"), JsonSerializer.Serialize(runtime));
        });
    }

    private void Append(ServerHistory server, string tier, string file, Action<BinaryWriter> write)
    {
        Guarded(() =>
        {
            string folder = Path.Combine(server.Directory, tier);
            Directory.CreateDirectory(folder);

            using var stream = new FileStream(Path.Combine(folder, file + ".bin"), FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(stream);
            write(writer);
        });
    }

    private void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Charts can live without a file. Collection must not stop because a disk filled up.
            logger.LogWarning(exception, "Monitor could not write history.");
        }
    }

    private static void WritePoint(BinaryWriter writer, HistoryPoint point)
    {
        writer.Write(point.Time.Ticks);
        writer.Write(point.SimSpeed);
        writer.Write(point.FrameP50);
        writer.Write(point.FrameP95);
        writer.Write(point.FrameP99);
        writer.Write(point.FrameMax);
        writer.Write(point.Gen0);
        writer.Write(point.Gen1);
        writer.Write(point.Gen2);
        writer.Write(point.AllocatedKb);
        writer.Write(point.AfterGap);
        writer.Write(point.SentKb);
        writer.Write(point.ReceivedKb);
        writer.Write(point.StateSyncKb);
        writer.Write(point.EventsKb);
        writer.Write(point.ReplicationKb);
        writer.Write(point.RpcsIn);
        writer.Write(point.Seconds);
        writer.Write(point.SimulatedFrames);
        writer.Write(point.Scopes.Length);

        foreach (float scope in point.Scopes)
            writer.Write(scope);
    }

    private static HistoryPoint ReadPoint(BinaryReader reader)
    {
        var point = new HistoryPoint
        {
            Time = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
            SimSpeed = reader.ReadSingle(),
            FrameP50 = reader.ReadSingle(),
            FrameP95 = reader.ReadSingle(),
            FrameP99 = reader.ReadSingle(),
            FrameMax = reader.ReadSingle(),
            Gen0 = reader.ReadInt32(),
            Gen1 = reader.ReadInt32(),
            Gen2 = reader.ReadInt32(),
            AllocatedKb = reader.ReadUInt32(),
            AfterGap = reader.ReadBoolean(),
            SentKb = reader.ReadSingle(),
            ReceivedKb = reader.ReadSingle(),
            StateSyncKb = reader.ReadSingle(),
            EventsKb = reader.ReadSingle(),
            ReplicationKb = reader.ReadSingle(),
            RpcsIn = reader.ReadSingle(),
            Seconds = reader.ReadSingle(),
            SimulatedFrames = reader.ReadUInt32(),
        };

        // Read from a damaged file this could be any number, and it's about to size an array.
        int scopeCount = reader.ReadInt32();
        if (scopeCount is < 0 or > MaxScopesInRow)
            throw new InvalidDataException("This row's length can't be right.");

        var scopes = new float[scopeCount];
        for (int i = 0; i < scopes.Length; i++)
            scopes[i] = reader.ReadSingle();

        point.Scopes = scopes;
        return point;
    }

    private static void WriteCensus(BinaryWriter writer, StoredCensus stored)
    {
        HistoryCensus row = stored.Row;

        writer.Write(stored.Time.Ticks);
        writer.Write(row.Entities);
        writer.Write(row.Grids);
        writer.Write(row.Blocks);
        writer.Write(row.Pcu);
        writer.Write(row.Characters);
        writer.Write(row.FloatingObjects);
        writer.Write(row.Voxels);
        writer.Write(row.OnlinePlayers);
        writer.Write(row.Factions);
        writer.Write(row.PingMean);
        writer.Write(row.PingWorst);
        writer.Write(row.PassUnits);
        writer.Write(row.WorkingSetMb);
        writer.Write(row.ManagedMb);
        writer.Write(row.ConcealedGrids);
        writer.Write(row.ConcealedBlocks);
        writer.Write(row.StaticGrids);
        writer.Write(row.Asteroids);
        writer.Write(row.Planets);
        writer.Write(row.CpuPercent);
        writer.Write(row.UnitsSinceStart);
    }

    private static StoredCensus ReadCensus(BinaryReader reader)
    {
        var time = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);

        return new StoredCensus(time, new HistoryCensus
        {
            Entities = reader.ReadUInt32(),
            Grids = reader.ReadUInt32(),
            Blocks = reader.ReadUInt32(),
            Pcu = reader.ReadUInt32(),
            Characters = reader.ReadUInt32(),
            FloatingObjects = reader.ReadUInt32(),
            Voxels = reader.ReadUInt32(),
            OnlinePlayers = reader.ReadUInt32(),
            Factions = reader.ReadUInt32(),
            PingMean = reader.ReadDouble(),
            PingWorst = reader.ReadUInt32(),
            PassUnits = reader.ReadUInt32(),
            WorkingSetMb = reader.ReadUInt32(),
            ManagedMb = reader.ReadUInt32(),
            ConcealedGrids = reader.ReadUInt32(),
            ConcealedBlocks = reader.ReadUInt32(),
            StaticGrids = reader.ReadUInt32(),
            Asteroids = reader.ReadUInt32(),
            Planets = reader.ReadUInt32(),
            CpuPercent = reader.ReadDouble(),
            UnitsSinceStart = reader.ReadInt64(),
        });
    }

    private static void WritePlayer(BinaryWriter writer, StoredPlayer row)
    {
        writer.Write(row.Time.Ticks);
        writer.Write(row.Name);
        writer.Write(row.Faction);
        writer.Write(row.PingMs);
        writer.Write(row.SentBytesPerSecond);
        writer.Write(row.ReceivedBytesPerSecond);
        writer.Write(row.RpcsPerSecond);
        writer.Write(row.QueuedBytes);
    }

    private static StoredPlayer ReadPlayer(BinaryReader reader) => new(
        new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
        reader.ReadString(),
        reader.ReadString(),
        reader.ReadUInt32())
    {
        SentBytesPerSecond = reader.ReadUInt32(),
        ReceivedBytesPerSecond = reader.ReadUInt32(),
        RpcsPerSecond = reader.ReadUInt32(),
        QueuedBytes = reader.ReadUInt32(),
    };

    private static void WriteFaction(BinaryWriter writer, StoredFaction row)
    {
        writer.Write(row.Time.Ticks);
        writer.Write(row.Name);
        writer.Write(row.Blocks);
        writer.Write(row.Pcu);
        writer.Write(row.Grids);
        writer.Write(row.OnlineMembers);
    }

    private static StoredFaction ReadFaction(BinaryReader reader) => new(
        new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
        reader.ReadString(),
        reader.ReadUInt32(),
        reader.ReadUInt32(),
        reader.ReadUInt32(),
        reader.ReadUInt32());

    private static void WriteGroup(BinaryWriter writer, StoredGroup row)
    {
        writer.Write(row.Time.Ticks);
        writer.Write((uint)row.Dimension);
        writer.Write(row.Name);
        writer.Write(row.MainMs);
        writer.Write(row.WorkerMs);
        writer.Write(row.CallsPerFrame);
        writer.Write(row.PeakMs);
        writer.Write(row.Key);
        writer.Write(row.BytesPerSecond);
        writer.Write(row.Blocks);
        writer.Write(row.Grids);
    }

    // A group row carries on after its name with a fixed size, and the name starts with its length, seven bits at a time.
    private static void SkipGroup(BinaryReader reader)
    {
        int length = 0;
        int shift = 0;
        byte part;

        do
        {
            // A damaged file can say anything, and a seek backwards would read the same rows for ever.
            if (shift >= 35)
                throw new InvalidDataException("This row's name can't be that long.");

            part = reader.ReadByte();
            length |= (part & 0x7F) << shift;
            shift += 7;
        }
        while ((part & 0x80) != 0);

        if (length < 0)
            throw new InvalidDataException("This row's name can't be that long.");

        reader.BaseStream.Seek((long)length + GroupBytesAfterName, SeekOrigin.Current);
    }

    private static StoredGroup ReadGroup(BinaryReader reader, DateTime time, HistoryDimension dimension) => new(
        time,
        dimension,
        reader.ReadString(),
        reader.ReadSingle(),
        reader.ReadSingle(),
        reader.ReadSingle(),
        reader.ReadSingle())
    {
        Key = reader.ReadInt64(),
        BytesPerSecond = reader.ReadSingle(),
        Blocks = reader.ReadUInt32(),
        Grids = reader.ReadUInt32(),
    };

    private static void WriteMarker(BinaryWriter writer, StoredMarker row)
    {
        writer.Write(row.Time.Ticks);
        writer.Write((int)row.Kind);
        writer.Write(row.Text);
    }

    private static StoredMarker ReadMarker(BinaryReader reader) => new(
        new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
        (StoredMarkerKind)reader.ReadInt32(),
        reader.ReadString());

    private List<HistoryPoint> ReadPoints(ServerHistory server, string tier, DateTime from, DateTime to, string fileFormat) =>
        ReadFiles(server, tier, FileName(from, fileFormat), FileName(to, fileFormat), from, to, ReadPoint, point => point.Time);

    private List<T> ReadDays<T>(ServerHistory server, string tier, DateTime from, DateTime to, Func<BinaryReader, T> read, Func<T, DateTime> timeOf) =>
        ReadFiles(server, tier, FileName(from, DayFiles), FileName(to, DayFiles), from, to, read, timeOf);

    private List<T> ReadFiles<T>(ServerHistory server, string tier, string first, string last, DateTime from, DateTime to, Func<BinaryReader, T> read, Func<T, DateTime> timeOf)
    {
        var rows = new List<T>();
        string folder = Path.Combine(server.Directory, tier);

        if (!Directory.Exists(folder))
            return rows;

        foreach (string path in Directory.GetFiles(folder, "*.bin").OrderBy(file => file, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (string.CompareOrdinal(name, first) >= 0 && string.CompareOrdinal(name, last) <= 0)
                ReadFile(path, rows, from, to, read, timeOf);
        }

        return rows;
    }

    private void ReadFile<T>(string path, List<T> rows, DateTime from, DateTime to, Func<BinaryReader, T> read, Func<T, DateTime> timeOf)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);

            while (stream.Position < stream.Length)
            {
                T row = read(reader);
                DateTime time = timeOf(row);

                if (time >= from && time <= to)
                    rows.Add(row);
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException or FormatException or OverflowException)
        {
            // A row cut short by a crash. Everything before it is still good, but anything appended
            // after it no longer lines up, so reading it gives nonsense that fails in any of these ways.
            if (_damagedFiles.TryAdd(path, 0))
                logger.LogWarning("Part of a Monitor history file couldn't be read, so charts have a gap there: {File}.", Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Monitor could not read history.");
        }
    }

    private void PruneIfDue(ServerHistory server)
    {
        DateTime now = DateTime.UtcNow;
        if (now - server.LastPrune < s_pruneEvery)
            return;

        server.LastPrune = now;

        DateTime oldest = now - settings.Retention;
        server.Markers.RemoveAll(marker => marker.Time < oldest);

        Guarded(() =>
        {
            string oldestDay = FileName(now - settings.Retention, DayFiles);

            Prune(server, "seconds", FileName(now - s_seconds, HourFiles));
            Prune(server, "minutes", oldestDay);
            Prune(server, "census", oldestDay);
            Prune(server, "groups", oldestDay);
            Prune(server, "players", oldestDay);
            Prune(server, "markers", oldestDay);
            Prune(server, "factions", oldestDay);
        });
    }

    private static void Prune(ServerHistory server, string tier, string oldestToKeep)
    {
        string folder = Path.Combine(server.Directory, tier);
        if (!Directory.Exists(folder))
            return;

        foreach (string path in Directory.GetFiles(folder, "*.bin"))
        {
            if (string.CompareOrdinal(Path.GetFileNameWithoutExtension(path), oldestToKeep) < 0)
                File.Delete(path);
        }
    }
}
