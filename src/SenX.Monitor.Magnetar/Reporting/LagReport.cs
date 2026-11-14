using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Patching;
using SenX.Monitor.Magnetar.Hooks;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ModAPI;
using static SenX.Monitor.Contracts.ChatFigure;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>
/// Turns a measuring run into a report. Names come from the live game, on the game thread,
/// for the rows shown and no others. Rows hold numbers only, so a grid removed during the run
/// still shows its time.
/// </summary>
internal static class LagReport
{
    private const int RemovedGridsKept = 1024;

    private static readonly RecentNames s_removedGrids = new(RemovedGridsKept);

    private static readonly (string Label, int Scope)[] s_frameParts =
    {
        ("Physics", Scope.Physics),
        ("Entities before", Scope.EntitiesBefore),
        ("Entities after", Scope.EntitiesAfter),
        ("Simulate", Scope.EntitiesSimulate),
        ("Once", Scope.EntitiesOnce),
        ("Network", Scope.Network),
        ("Replication", Scope.ReplicationSend),
        ("Callbacks", Scope.RunCallbacks),
        ("Plugins", Scope.Plugins),
        ("GPS", Scope.Gps),
    };

    private static readonly string[] s_columns = { "Main", "Frame", "Work", "Peak", ThreadTags.Header };

    private const int DrillRowsInChat = 15;
    private const int ModPartsInRun = 30;

    private static string Ms(double units) => (units / 10000d).ToString("0.00", CultureInfo.InvariantCulture);

    private static string N(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    public static ChatReport Build(LagView view, CaptureArgs args, CaptureResult result, string? clusterProblem)
    {
        var report = new ChatReport().Title(TitleOf(view));
        AddHeader(report, result);

        switch (view)
        {
            case LagView.Grids:
                AddRows(report, "Grid", result, result.Grids, args.Top, row => GridName(row.Key, args.Details));
                break;
            case LagView.BlockTypes:
                AddRows(report, "Block type", result, result.BlockTypes, args.Top, TypeName);
                break;
            case LagView.Blocks:
                AddRows(report, "Block", result, result.Definitions, args.Top, DefinitionName);
                break;
            case LagView.Players:
                AddRows(report, "Player", result, result.Owners, args.Top, row => OwnerName(row.Key));
                break;
            case LagView.Factions:
                AddFactions(report, result, args.Top);
                break;
            case LagView.Scripts:
                AddRows(report, "Script", result, result.Scripts, args.Top, row => ScriptName(row.Key));
                break;
            case LagView.Session:
                AddRows(report, "Component", result, result.Session, args.Top, row => KeyNames.Describe(row.Key));
                break;
            case LagView.Entities:
                AddRows(report, "Entity type", result, result.EntityTypes, args.Top, TypeName);
                break;
            case LagView.Logic:
                AddRows(report, "Game logic", result, result.Logic, args.Top, row => KeyNames.Describe(row.Key));
                break;
            case LagView.Callbacks:
                AddRows(report, "Callback", result, result.Callbacks, args.Top, row => KeyNames.Describe(row.Key));
                break;
            case LagView.Rpcs:
                report.Section("Received");
                AddRows(report, "RPC", result, result.Rpcs, args.Top, row => KeyNames.Describe(row.Key));
                report.Section("Sent");
                AddSent(report, result, args.Top);
                break;
            case LagView.Mods:
                AddCodeOwners(report, result, args.Top);
                break;
            case LagView.Physics:
                AddPhysicsStages(report, result);
                AddClusters(report, result, args, clusterProblem);
                break;
            default:
                AddDiagnosis(report, result, args, clusterProblem);
                break;
        }

        return report;
    }

    private static string TitleOf(LagView view)
    {
        switch (view)
        {
            case LagView.BlockTypes:
                return "Block types";
            case LagView.Blocks:
                return "Block definitions";
            case LagView.Session:
                return "Session components";
            case LagView.Entities:
                return "Entity types";
            case LagView.Logic:
                return "Game logic";
            case LagView.Rpcs:
                return "RPCs";
            case LagView.Mods:
                return "Mods and plugins";
            case LagView.Diagnose:
                return "Diagnosis";
            default:
                return view.ToString();
        }
    }

    private static string TitleOf(GroupDimension list)
    {
        switch (list)
        {
            case GroupDimension.BlockTypes:
                return "block types";
            case GroupDimension.Definitions:
                return "block definitions";
            case GroupDimension.Owners:
                return "players";
            case GroupDimension.EntityTypes:
                return "entity types";
            case GroupDimension.Session:
                return "session components";
            case GroupDimension.Logic:
                return "game logic";
            case GroupDimension.Rpcs:
                return "RPCs";
            case GroupDimension.RpcsSent:
                return "RPCs sent";
            case GroupDimension.Mods:
                return "mods and plugins";
            default:
                return list.ToString().ToLowerInvariant();
        }
    }

    /// <summary>A drill-down, numbered so the next can pick a row.</summary>
    public static ChatReport Drill(DrillResultResponse result)
    {
        var report = new ChatReport()
            .Title(result.Method)
            .Facts(
                Fig("Owner", result.Owner),
                Fig("Over", N(result.Seconds, "0"), "s"),
                Fig("Calls", N(result.CallsPerFrame, "0.##"), "/f"),
                Fig("Main", N(result.MainMs, "0.00"), "ms/f"),
                Fig("Workers", N(result.WorkerMs, "0.00"), "ms/f"),
                Fig("Own work", N(result.SelfMs, "0.00"), "ms/f"));

        if (result.CallsPerFrame == 0d)
            report.Text("It never ran, or the compiler built it into its caller. Try the caller.");

        if (result.EndedEarly)
            report.Warning("Stopped early, because timing this was costing the server too much.");
        else if (result.Inflated)
            report.Warning(FormattableString.Invariant($"Timing added about {result.TimingMsPerFrame:0.00} ms/f, so these read high."));

        ChatTable table = report.Table("Call", "Calls/f", "Main", "Work", "Peak", ThreadTags.Header);

        for (int row = 0; row < result.Rows.Count && row < DrillRowsInChat; row++)
        {
            DrillRow call = result.Rows[row];

            table.Row(
                FormattableString.Invariant($"[{row}] {call.Name} ({call.Owner})"),
                N(call.CallsPerFrame, "0.##"),
                N(call.MainMs, "0.00"),
                N(call.WorkerMs, "0.00"),
                N(call.PeakMs, "0.00"),
                ThreadTags.Tag(call.MainMs, call.WorkerMs));
        }

        return report.Note("Main and Work are ms/f, Peak is ms. " + ThreadTags.Note + " !monitor drill N looks inside row N.");
    }

    /// <summary>What a player sees after testing their grid. The run was limited to it, so nobody else's rows are here.</summary>
    public static ChatReport GridTest(CaptureResult result, string name, int top)
    {
        double seconds = result.ElapsedUnits / (double)Clock.UnitsPerSecond;
        var report = new ChatReport()
            .Title("'" + name + "'")
            .Facts(Fig("Measured", N(seconds, "0.0"), "s"), Fig("Server sim", N(result.SimSpeed, "0.00")));

        RollupRow[] grid = result.Grids.Top(1);

        if (grid.Length == 0)
            return report.Text("Nothing on this grid took any measurable time.");

        AddRow(report.Table("Grid", s_columns), result, "This grid", grid[0]);
        report.Section("Block types");
        AddRows(report, "Type", result, result.BlockTypes, top, TypeName);
        report.Section("Blocks");
        AddRows(report, "Block", result, result.Definitions, top, DefinitionName);

        if (result.Scripts.Count > 0)
        {
            report.Section("Scripts");
            AddRows(report, "Script", result, result.Scripts, top, row => ScriptName(row.Key));
        }

        return report.Note("Main and Work are ms/f, Peak is ms. " + ThreadTags.Note);
    }

    private static void AddHeader(ChatReport report, CaptureResult result)
    {
        double seconds = result.ElapsedUnits / (double)Clock.UnitsPerSecond;

        report.Facts(
            Fig("Measured", N(seconds, "0.0"), "s"),
            Fig("Frames", N(result.SimulatedFrames, "0")),
            Fig("Sim speed", N(result.SimSpeed, "0.00")),
            Fig("Frame avg", Ms(result.AverageFrameUnits), "ms"),
            Fig("Frame worst", Ms(result.WorstFrameUnits), "ms"),
            Fig("Timing cost", N(result.TimingNanosPerFrame / 1000000d, "0.00"), "ms/f"),
            Fig("Main waiting", Ms(result.PerFrame(result.MainWaitUnits)), "ms/f on workers"),
            Fig("Parallel work", Ms(result.PerFrame(result.ParallelWorkerUnits)), "ms/f across workers"));

        if (result.EndedEarly)
            report.Warning("The server paused, so the run stopped early.");

        if (PatchEngine.TakenOverNote() is { } lost)
            report.Warning(lost + ".");

        if (result.Coverage < 0.999d)
            report.Warning(FormattableString.Invariant($"Only {result.Coverage * 100d:0.0}% of measured time fitted in the tables, so some rows read low."));

        if (result.UnattachedCalls > 0L)
            report.Warning(FormattableString.Invariant($"{result.UnattachedCalls} calls ran on threads with no block, and aren't counted."));

        report.Note("Every call was timed. Main and Work are ms/f, Peak is ms. " + ThreadTags.Note);
    }

    private static void AddRows(ChatReport report, string header, CaptureResult result, RollupTable table, int top, Func<RollupRow, string> name)
    {
        RollupRow[] rows = table.Top(top);

        if (rows.Length == 0)
        {
            report.Text("Nothing recorded.");
            return;
        }

        ChatTable lines = report.Table(header, s_columns);

        foreach (RollupRow row in rows)
            AddRow(lines, result, name(row), row);
    }

    private static void AddRow(ChatTable table, CaptureResult result, string name, in RollupRow row)
    {
        double main = result.PerFrame(row.MainUnits);
        double worker = result.PerFrame(row.WorkerUnits);

        table.Row(name, Ms(main), ShareOf(main, result.AverageFrameUnits), Ms(worker), Ms(row.MaxUnits), ThreadTags.Tag(main, worker));
    }

    private static string ShareOf(double part, double frame) => N(frame > 0d ? part / frame * 100d : 0d, "0.0") + "%";

    private static void AddFactions(ChatReport report, CaptureResult result, int top, Dictionary<long, int>? gridsOwned = null)
    {
        var rows = FactionRows(result);

        if (rows.Count == 0)
        {
            report.Text("Nothing recorded.");
            return;
        }

        ChatTable table = report.Table("Faction", s_columns);

        foreach (KeyValuePair<string, RollupRow> faction in rows.OrderByDescending(r => r.Value.TotalUnits).Take(top))
            AddRow(table, result, faction.Key + (gridsOwned == null ? string.Empty : GridCount(GridsOfFaction(faction.Key, gridsOwned))), faction.Value);
    }

    private static string BlocksOf(long gridId) =>
        Find(gridId) is MyCubeGrid grid ? FormattableString.Invariant($" ({grid.BlocksCount} blocks)") : string.Empty;

    private static string GridCount(int count) => FormattableString.Invariant($" ({count} grids)");

    // Who owns how many, walking the world once. Only diagnose asks for this.
    private static Dictionary<long, int> GridsByOwner()
    {
        var owned = new Dictionary<long, int>();

        foreach (MyEntity entity in MyEntities.GetEntities())
        {
            if (!(entity is MyCubeGrid grid))
                continue;

            // Grids with no owner are counted too, so they sit beside the time their row shows.
            long owner = grid.BigOwners.Count == 0 ? RollupTable.NoKey : grid.BigOwners[0];
            owned[owner] = owned.TryGetValue(owner, out int count) ? count + 1 : 1;
        }

        return owned;
    }

    private static int GridsOfFaction(string tag, Dictionary<long, int> gridsOwned)
    {
        int count = 0;

        foreach (KeyValuePair<long, int> owner in gridsOwned)
        {
            if (string.Equals(FactionOfOwner(owner.Key), tag, StringComparison.Ordinal))
                count += owner.Value;
        }

        return count;
    }

    private static string FactionOfOwner(long owner) => MySession.Static.Factions.TryGetPlayerFaction(owner)?.Tag ?? "<no faction>";

    private static Dictionary<string, RollupRow> FactionRows(CaptureResult result)
    {
        var rows = new Dictionary<string, RollupRow>();
        result.Owners.Visit(owner => Merge(rows, FactionOfOwner(owner.Key), owner));
        return rows;
    }

    private static void Merge(Dictionary<string, RollupRow> rows, string name, in RollupRow row)
    {
        rows.TryGetValue(name, out RollupRow total);
        total.MainUnits += row.MainUnits;
        total.WorkerUnits += row.WorkerUnits;
        total.Calls += row.Calls;
        total.MaxUnits = Math.Max(total.MaxUnits, row.MaxUnits);
        rows[name] = total;
    }

    private static void AddCodeOwners(ChatReport report, CaptureResult result, int top)
    {
        Dictionary<string, RollupRow> rows = CodeOwnerRows(result);

        if (rows.Count == 0)
        {
            report.Text("Nothing recorded.");
            return;
        }

        ChatTable table = report.Table("Mod or plugin", s_columns);

        foreach (KeyValuePair<string, RollupRow> owner in rows.OrderByDescending(r => r.Value.TotalUnits).Take(top))
            AddRow(table, result, owner.Key, owner.Value);
    }

    // Session components, game logic and callbacks, all by whose code they are.
    private static Dictionary<string, RollupRow> CodeOwnerRows(CaptureResult result)
    {
        var rows = new Dictionary<string, RollupRow>();

        void Add(RollupRow row) => Merge(rows, CodeOwner(row.Key), row);

        result.Session.Visit(Add);
        result.Logic.Visit(Add);
        result.Callbacks.Visit(Add);
        result.Plugins.Visit(Add);
        return rows;
    }

    private static string CodeOwner(long key)
    {
        Type? type = KeyNames.TypeOf(key);
        return type == null ? "Unknown" : CodeOwner(type);
    }

    /// <summary>Whose code a type is: the game, a mod or a plugin.</summary>
    public static string CodeOwner(Type type)
    {
        if (MyScriptManager.Static != null && MyScriptManager.Static.TypeToModMap.TryGetValue(type, out var mod))
            return "Mod: " + (mod.ModName ?? mod.ModId ?? "unnamed");

        string assembly = type.Assembly.GetName().Name ?? string.Empty;
        bool game = assembly.StartsWith("Sandbox.", StringComparison.Ordinal)
                    || assembly.StartsWith("VRage", StringComparison.Ordinal)
                    || assembly.StartsWith("SpaceEngineers", StringComparison.Ordinal);

        return game ? "Game" : "Plugin: " + ModNames.PluginName(assembly);
    }

    /// <summary>The latest background total for one list. Names come from the live game.</summary>
    public static ChatReport Background(GroupDimension list)
    {
        if (!(LatestSweep(list, out long latest) is { } rows))
            return "No background totals yet. They arrive every 30 seconds while detail is on.";

        var report = new ChatReport()
            .Title("Background " + TitleOf(list))
            .Note(FormattableString.Invariant($"From {latest / (double)Clock.UnitsPerSecond:0} s after start. A run gives accurate numbers."));

        if (rows.Count == 0)
            return report.Text("Nothing recorded.");

        AddBackgroundRows(report, list, rows);
        return report;
    }

    /// <summary>The top of a list from the latest total, as a section of another report. Nothing to show adds nothing.</summary>
    public static void AddBackground(ChatReport report, GroupDimension list, string heading, int most)
    {
        if (!(LatestSweep(list, out long _) is { Count: > 0 } rows))
            return;

        report.Section(heading);
        AddBackgroundRows(report, list, rows.Take(most));
    }

    // Null until there's been a sweep. Every list of one sweep shares its time, so this stops at the first row of an older one.
    internal static unsafe List<GroupRecord>? LatestSweep(GroupDimension list, out long latest)
    {
        RecordRing ring = Sweep.Rows;
        long oldest = ring.Oldest;
        var rows = new List<GroupRecord>();

        latest = -1L;

        // Only whole totals, because the newest could still be being written.
        for (long sequence = Sweep.WholeRows - 1L; sequence >= oldest; sequence--)
        {
            GroupRecord row = default;
            if (!ring.TryCopy(sequence, (byte*)&row))
                break;

            if (latest < 0L)
                latest = row.UnitsSinceStart;
            else if (row.UnitsSinceStart != latest)
                break;

            if (row.Dimension == list)
                rows.Add(row);
        }

        if (latest < 0L)
            return null;

        rows.Reverse();
        return rows;
    }

    private static void AddBackgroundRows(ChatReport report, GroupDimension list, IEnumerable<GroupRecord> rows)
    {
        if (list == GroupDimension.RpcsSent)
        {
            ChatTable sent = report.Table("RPC", "KB/s", "Events/f");

            foreach (GroupRecord row in rows)
                sent.Row(BackgroundName(list, row), N(row.BytesPerSecond / 1024d, "0.0"), N(row.CentiCallsPerFrame / 100d, "0.##"));

            return;
        }

        ChatTable table = report.Table("Name", "Main", "Work", "Peak", ThreadTags.Header);

        foreach (GroupRecord row in rows)
            table.Row(BackgroundName(list, row), Ms(row.MainUnits), Ms(row.WorkerUnits), Ms(row.MaxUnits), ThreadTags.Tag(row.MainUnits, row.WorkerUnits));

        report.Note("Main and Work are ms/f, Peak is ms. " + ThreadTags.Note);
    }

    private static string BackgroundName(GroupDimension list, in GroupRecord row) =>
        NameFor((HistoryDimension)list, row.Key, row.Representative);

    /// <summary>A readable name for anything in history. Game thread only.</summary>
    public static string NameFor(HistoryDimension kind, long key, long representative)
    {
        switch (kind)
        {
            case HistoryDimension.Grids:
                return GridName(key, details: false);
            case HistoryDimension.Owners:
                return OwnerName(key);
            case HistoryDimension.Scripts:
                return ScriptName(key);
            case HistoryDimension.Definitions:
                return DefinitionName(new RollupRow { Key = key, Representative = representative });
            case HistoryDimension.BlockTypes:
            case HistoryDimension.EntityTypes:
                return TypeName(new RollupRow { Key = key, Representative = representative });
            case HistoryDimension.Mods:
                return CodeOwners.NameOf(key);
            case HistoryDimension.ModParts:
                return ModNames.Part(CodeOwners.NameOf(representative), KeyNames.Describe(key));
            case HistoryDimension.Factions:
                return key == SenX.Monitor.Core.Census.NoFaction
                    ? "<no faction>"
                    : MySession.Static.Factions.TryGetFactionById(key)?.Tag ?? "(faction " + key.ToString(CultureInfo.InvariantCulture) + ")";
            default:
                return KeyNames.Describe(key);
        }
    }

    /// <summary>A whole run as rows with names, for Quasar to build its report from.</summary>
    public static RunResultResponse BuildRun(CaptureResult result, int top, string? clusterProblem)
    {
        var response = new RunResultResponse
        {
            State = RunState.Finished,
            Seconds = result.ElapsedUnits / (double)Clock.UnitsPerSecond,
            Frames = result.SimulatedFrames,
            SimSpeed = result.SimSpeed,
            AverageFrameMs = result.AverageFrameUnits / 10000d,
            WorstFrameMs = result.WorstFrameUnits / 10000d,
            TimingMsPerFrame = result.TimingNanosPerFrame / 1000000d,
            MainWaitMsPerFrame = result.PerFrame(result.MainWaitUnits) / 10000d,
            ParallelWorkerMsPerFrame = result.PerFrame(result.ParallelWorkerUnits) / 10000d,
            Coverage = result.Coverage,
            EndedEarly = result.EndedEarly,
            ClusterProblem = clusterProblem,
            LostSites = PatchEngine.TakenOverNote(),
            ClustersTimed = result.ClustersTimed,
            EndedUtcTicks = DateTime.UtcNow.Ticks,
            Parts = PartsOf(result),
        };

        void AddList(string list, RollupTable table, Func<RollupRow, string> name)
        {
            foreach (RollupRow row in table.Top(top))
                response.Rows.Add(RunRowFor(result, list, name(row), row));
        }

        void AddMerged(string list, Dictionary<string, RollupRow> rows)
        {
            foreach (KeyValuePair<string, RollupRow> row in rows.OrderByDescending(r => r.Value.TotalUnits).Take(top))
                response.Rows.Add(RunRowFor(result, list, row.Key, row.Value));
        }

        AddList(RunLists.Grids, result.Grids, row => GridName(row.Key, details: true));

        foreach (RunRow row in response.Rows)
        {
            row.Gps = GpsLine(row.Key);
            row.PlainName = GridName(row.Key, details: false);
            row.Blocks = Find(row.Key) is MyCubeGrid grid ? grid.BlocksCount : 0L;
        }

        AddList(RunLists.BlockTypes, result.BlockTypes, TypeName);
        AddList(RunLists.Definitions, result.Definitions, DefinitionName);
        AddList(RunLists.Players, result.Owners, row => OwnerName(row.Key));
        AddMerged(RunLists.Factions, FactionRows(result));

        foreach (RunRow row in response.Rows)
        {
            if (row.List == RunLists.Players)
            {
                Size(row, Census.CountOfOwner(row.Key));
            }
            else if (row.List == RunLists.Factions)
            {
                row.Key = MySession.Static.Factions.TryGetFactionByTag(row.Name)?.FactionId ?? 0L;
                Size(row, Census.CountOf(row.Key == 0L ? Census.NoFaction : row.Key));
            }
        }

        AddList(RunLists.Scripts, result.Scripts, row => ScriptName(row.Key));
        AddList(RunLists.Session, result.Session, row => KeyNames.Describe(row.Key));
        AddList(RunLists.Logic, result.Logic, row => KeyNames.Describe(row.Key));
        AddList(RunLists.Callbacks, result.Callbacks, row => KeyNames.Describe(row.Key));
        AddList(RunLists.Rpcs, result.Rpcs, row => KeyNames.Describe(row.Key));

        foreach (RollupRow row in result.RpcsSent.Top(top))
        {
            response.Rows.Add(new RunRow
            {
                List = RunLists.RpcsSent,
                Name = KeyNames.Describe(row.Key),
                CallsPerFrame = result.PerFrame(row.Calls),
                BytesPerSecond = result.BytesPerSecond(row.MainUnits),
            });
        }

        AddList(RunLists.Entities, result.EntityTypes, TypeName);
        AddMerged(RunLists.Mods, CodeOwnerRows(result));
        AddModParts(response, result);

        if (result.ClustersTimed)
            ClusterReport.AddRunRows(response, result, top);

        response.SlowCalls = NameSlowCalls(result.Slowest);
        return response;
    }

    // Each mod's row lists what's inside it, so a run can be opened up the same way history can.
    private static void AddModParts(RunResultResponse response, CaptureResult result)
    {
        var parts = new Dictionary<string, List<(string Name, double Ms)>>();

        void Add(RollupRow row)
        {
            string owner = CodeOwner(row.Key);

            if (!parts.TryGetValue(owner, out List<(string Name, double Ms)>? list))
            {
                list = new List<(string Name, double Ms)>();
                parts[owner] = list;
            }

            list.Add((KeyNames.Describe(row.Key), result.PerFrame(row.TotalUnits) / 10000d));
        }

        result.Session.Visit(Add);
        result.Logic.Visit(Add);
        result.Callbacks.Visit(Add);
        result.Plugins.Visit(Add);

        foreach (RunRow row in response.Rows.Where(row => row.List == RunLists.Mods))
        {
            if (parts.TryGetValue(row.Name, out List<(string Name, double Ms)>? list))
            {
                row.Members = list
                    .GroupBy(part => part.Name)
                    .Select(part => (Name: part.Key, Ms: part.Sum(entry => entry.Ms)))
                    .OrderByDescending(part => part.Ms)
                    .Take(ModPartsInRun)
                    .Select(part => FormattableString.Invariant($"{part.Name}: {part.Ms:0.00} ms/f"))
                    .ToList();
            }
        }
    }

    /// <summary>Slow calls with names. Game thread only.</summary>
    public static List<SlowCallRow> NameSlowCalls(SlowestCalls calls)
    {
        var rows = new List<SlowCallRow>(calls.Count);

        for (int i = 0; i < calls.Count; i++)
        {
            SlowCall call = calls[i];

            rows.Add(new SlowCallRow
            {
                Frame = (long)call.Frame,
                Ms = call.Units / 10000d,
                Where = SiteGroups.Name(call.Site),
                Name = SlowCallName(call),
                MainThread = call.Main,
            });
        }

        return rows;
    }

    // A parallel updater that's an entity is recorded by its id, and only anything else by a key.
    private static string SlowCallName(in SlowCall call)
    {
        long value = SiteKeys.ValueOf(call.Id);
        bool parallel = call.Site == Site.ParallelBefore || call.Site == Site.ParallelAfter;

        return SlowestCalls.IsKeyed(call.Site) && (!parallel || KeyNames.Knows(value))
            ? KeyNames.Describe(value)
            : EntityName(call.Id);
    }

    private static string EntityName(long id)
    {
        switch (Find(id))
        {
            case MyCubeBlock block:
                string subtype = block.BlockDefinition?.Id.SubtypeName ?? string.Empty;
                string name = subtype.Length == 0 ? block.GetType().Name : subtype;
                return name + " on '" + (block.CubeGrid?.DisplayName ?? "<no grid>") + "'";
            case MyCubeGrid grid:
                return grid.DisplayName;
            case MyEntity entity:
                return entity.GetType().Name;
            default:
                return "(removed entity)";
        }
    }

    private static void Size(RunRow row, in CensusCount counted)
    {
        row.Blocks = counted.Blocks;
        row.Grids = counted.Grids;
    }

    private static Dictionary<string, double> PartsOf(CaptureResult result)
    {
        double[] split = FrameSplit.Of(scope => result.PerFrame(result.ScopeUnits(scope)) / 10000d);
        var parts = new Dictionary<string, double>(split.Length);

        for (int part = 0; part < split.Length; part++)
            parts[FrameSplit.Names[part]] = split[part];

        return parts;
    }

    private static RunRow RunRowFor(CaptureResult result, string list, string name, in RollupRow row) => new()
    {
        List = list,
        Name = name,
        Key = row.Key,
        MainMsPerFrame = result.PerFrame(row.MainUnits) / 10000d,
        WorkerMsPerFrame = result.PerFrame(row.WorkerUnits) / 10000d,
        PeakMs = row.MaxUnits / 10000d,
        CallsPerFrame = result.PerFrame(row.Calls),
    };

    private static string? GpsLine(long gridId)
    {
        if (!(Find(gridId) is MyCubeGrid grid))
            return null;

        var center = grid.PositionComp.WorldAABB.Center;
        string name = grid.DisplayName.Replace(":", " ");

        return FormattableString.Invariant($"GPS:{name}:{center.X:0.00}:{center.Y:0.00}:{center.Z:0.00}:#FF75C9F1:");
    }

    private static void AddPhysicsStages(ChatReport report, CaptureResult result)
    {
        double physics = result.PerFrame(result.ScopeUnits(Scope.Physics));
        ChatTable table = report.Table("Stage", "ms/f", "Frame");

        table.Row("Physics", Ms(physics), ShareOf(physics, result.AverageFrameUnits));

        foreach (ScopeDefinition scope in ScopeTable.All.Where(s => s.Parent == Scope.Physics))
        {
            double units = result.PerFrame(result.ScopeUnits(scope.Id));
            if (units >= 10d)
                table.Nested(1, scope.Name, Ms(units), ShareOf(units, result.AverageFrameUnits));
        }
    }

    private static void AddClusters(ChatReport report, CaptureResult result, CaptureArgs args, string? problem)
    {
        if (result.ClustersTimed)
            ClusterReport.Add(report, result, args.Top);
        else
            report.Text("Clusters weren't timed, because " + (problem ?? "the run didn't ask for them") + ".");
    }

    private static void AddDiagnosis(ChatReport report, CaptureResult result, CaptureArgs args, string? clusterProblem)
    {
        report.Section("Frame");
        ChatTable parts = report.Table("Part", "ms/f", "Frame");

        foreach ((string label, int scope) in s_frameParts)
        {
            double units = result.PerFrame(result.ScopeUnits(scope));
            if (units >= 100d)
                parts.Row(label, Ms(units), ShareOf(units, result.AverageFrameUnits));
        }

        report.Section("Top grids");
        AddRows(report, "Grid", result, result.Grids, args.Top, row => GridName(row.Key, args.Details) + BlocksOf(row.Key));
        report.Section("Top block types");
        AddRows(report, "Block type", result, result.BlockTypes, args.Top, TypeName);
        Dictionary<long, int> gridsOwned = GridsByOwner();

        report.Section("Top players");
        AddRows(report, "Player", result, result.Owners, args.Top, row => OwnerName(row.Key) + GridCount(gridsOwned.TryGetValue(row.Key, out int count) ? count : 0));
        report.Section("Top factions");
        AddFactions(report, result, args.Top, gridsOwned);
        report.Section("Top scripts");
        AddRows(report, "Script", result, result.Scripts, args.Top, row => ScriptName(row.Key));
        report.Section("Top session components");
        AddRows(report, "Component", result, result.Session, args.Top, row => KeyNames.Describe(row.Key));
        report.Section("Top RPCs received");
        AddRows(report, "RPC", result, result.Rpcs, args.Top, row => KeyNames.Describe(row.Key));
        report.Section("Top RPCs sent");
        AddSent(report, result, args.Top);
        report.Section("Top mods and plugins");
        AddCodeOwners(report, result, args.Top);
        report.Section("Top clusters");
        AddClusters(report, result, args, clusterProblem);
    }

    // The biggest senders, in bytes across everyone who got them.
    private static void AddSent(ChatReport report, CaptureResult result, int top)
    {
        RollupRow[] rows = result.RpcsSent.Top(top);

        if (rows.Length == 0)
        {
            report.Text("Nothing recorded.");
            return;
        }

        ChatTable table = report.Table("RPC", "KB/s", "Events/f");

        foreach (RollupRow row in rows)
            table.Row(KeyNames.Describe(row.Key), N(result.BytesPerSecond(row.MainUnits) / 1024d, "0.0"), N(result.PerFrame(row.Calls), "0.##"));
    }

    /// <summary>
    /// Keeps a grid's name when it leaves the world during a run. Rows only hold ids, so without this the report
    /// couldn't say which grid it was, and that may be the one causing the lag, removed by a cleanup plugin.
    /// </summary>
    public static void OnEntityRemoved(IMyEntity entity)
    {
        try
        {
            if (!Capture.Idle && entity is MyCubeGrid grid && grid.DisplayName is { } name)
                s_removedGrids.Note(grid.EntityId, name);
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not note a removed grid's name.", exception);
        }
    }

    private static string GridName(long id, bool details)
    {
        if (!(Find(id) is MyCubeGrid grid))
            return s_removedGrids.Find(id) is { } name ? name + " (removed)" : "(removed grid " + id.ToString(CultureInfo.InvariantCulture) + ")";

        if (!details)
            return grid.DisplayName;

        var owners = grid.BigOwners;
        return owners.Count == 0
            ? grid.DisplayName + " (no owners)"
            : grid.DisplayName + " (" + string.Join(", ", owners.Select(OwnerWithFaction)) + ")";
    }

    private static string OwnerWithFaction(long owner)
    {
        string tag = MySession.Static.Factions.TryGetPlayerFaction(owner)?.Tag ?? "<single>";
        return OwnerName(owner) + " [" + tag + "]";
    }

    /// <summary>Who someone is, from their identity. Game thread only.</summary>
    public static string OwnerName(long key)
    {
        if (key == RollupTable.NoKey)
            return "Nobody";

        return MySession.Static.Players.TryGetIdentity(key)?.DisplayName
               ?? "(identity " + key.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static string TypeName(RollupRow row)
    {
        if (row.Key == EntityKeys.Blocks)
            return "All blocks";

        if (row.Representative == 0L)
            return KeyNames.Describe(row.Key);

        return Find(row.Representative)?.GetType().Name ?? "(removed entity)";
    }

    private static string DefinitionName(RollupRow row)
    {
        if (!(Find(row.Representative) is MyCubeBlock block) || block.BlockDefinition == null)
            return "(removed block)";

        string subtype = block.BlockDefinition.Id.SubtypeName;
        return string.IsNullOrEmpty(subtype) ? block.GetType().Name : subtype;
    }

    private static string ScriptName(long blockId)
    {
        if (!(Find(blockId) is MyProgrammableBlock block))
            return "(removed programmable block)";

        return "'" + block.DisplayNameText + "' in '" + (block.CubeGrid?.DisplayName ?? "<no grid>") + "'";
    }

    private static MyEntity? Find(long id) =>
        MyEntities.TryGetEntityById(id, out MyEntity entity) ? entity : null;
}
