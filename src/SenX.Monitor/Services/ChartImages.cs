using SenX.Monitor.Api;
using SenX.Monitor.Components.Charts;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>Which chart each <see cref="MonitorChart"/> is, so plugins depend on these names and not on the page's ids.</summary>
internal static class ChartImages
{
    private const string Overview = "overview";
    private const string World = "world";
    private const string Network = "network";
    private const string Costs = "costs";

    private static readonly BoardLevel[] s_budget = [new(FrameParts.BudgetMs, ChartColors.Bad)];

    private static readonly BoardLevel[] s_simLevels = [new(1d, ChartColors.Good), new(0.7d, ChartColors.Bad)];

    /// <summary>The picture, or null when that range has nothing to draw.</summary>
    public static byte[]? Draw(ChartBuilder charts, string server, MonitorChart chart, DateTime from, DateTime to, int width, int height)
    {
        (string view, string id, HistoryDimension list, string title, BoardLevel[]? levels) = Find(chart);

        ChartsResponse response = charts.Build(view, server, from, to, list);
        ChartData? data = response.Charts.FirstOrDefault(item => item.Id == id);

        return data == null ? null : ChartImage.Render(data, title, width, height, response.Markers, levels);
    }

    private static (string View, string Id, HistoryDimension List, string Title, BoardLevel[]? Levels) Find(MonitorChart chart) => chart switch
    {
        MonitorChart.SimSpeed => (Overview, "sim", HistoryDimension.Grids, "Sim speed", s_simLevels),
        MonitorChart.AvailableTime => (Overview, "headroom", HistoryDimension.Grids, "Available frame time", null),
        MonitorChart.FrameTime => (Overview, "frames", HistoryDimension.Grids, "Frame time", s_budget),
        MonitorChart.FrameParts => (Overview, "cause", HistoryDimension.Grids, "What's using the frame, against sim speed", null),
        MonitorChart.GameLoop => (Overview, "gameLoop", HistoryDimension.Grids, "Game loop time", null),
        MonitorChart.GarbageCollections => (Overview, "gc", HistoryDimension.Grids, "Garbage collections each second", null),
        MonitorChart.Allocation => (Overview, "allocated", HistoryDimension.Grids, "Main thread allocation", null),
        MonitorChart.Memory => (Overview, "memory", HistoryDimension.Grids, "Memory", null),
        MonitorChart.Players => (World, "players", HistoryDimension.Grids, "Players online", null),
        MonitorChart.Blocks => (World, "blocks", HistoryDimension.Grids, "Blocks", null),
        MonitorChart.Pcu => (World, "pcu", HistoryDimension.Grids, "PCU", null),
        MonitorChart.ServerPing => (World, "ping", HistoryDimension.Grids, "Server ping", null),
        MonitorChart.Objects => (World, "objects", HistoryDimension.Grids, "Grids and objects", null),
        MonitorChart.Traffic => (Network, "traffic", HistoryDimension.Grids, "Sent and received", null),
        MonitorChart.TrafficKinds => (Network, "trafficKinds", HistoryDimension.Grids, "What's being sent", null),
        MonitorChart.RpcRate => (Network, "rpcRate", HistoryDimension.Grids, "RPCs from clients", null),
        MonitorChart.GridCosts => (Costs, Costs, HistoryDimension.Grids, "Costliest grids", null),
        MonitorChart.OwnerCosts => (Costs, Costs, HistoryDimension.Owners, "Costliest owners", null),
        MonitorChart.BlockTypeCosts => (Costs, Costs, HistoryDimension.BlockTypes, "Costliest block types", null),
        MonitorChart.ModCosts => (Costs, Costs, HistoryDimension.Mods, "Costliest mods and plugins", null),
        MonitorChart.ScriptCosts => (Costs, Costs, HistoryDimension.Scripts, "Costliest scripts", null),
        MonitorChart.SessionCosts => (Costs, Costs, HistoryDimension.Session, "Costliest session components", null),
        MonitorChart.GameLogicCosts => (Costs, Costs, HistoryDimension.Logic, "Costliest game logic", null),
        MonitorChart.CallbackCosts => (Costs, Costs, HistoryDimension.Callbacks, "Costliest grid callbacks", null),
        _ => throw new ArgumentOutOfRangeException(nameof(chart), chart, "No such chart."),
    };
}
