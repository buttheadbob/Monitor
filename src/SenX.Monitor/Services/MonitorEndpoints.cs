using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

public static class MonitorEndpoints
{
    /// <summary>Where the browser fetches chart data.</summary>
    public const string ChartsRoute = "api/monitor/charts";

    /// <summary>
    /// Maps the chart data and the full board. Pass the policy that guards them. The preview host passes
    /// none, because it has nothing to protect.
    /// </summary>
    public static void MapMonitorApi(this IEndpointRouteBuilder endpoints, string? policy)
    {
        RouteHandlerBuilder route = endpoints.MapGet(
            "/" + ChartsRoute,
            (string? server, string view, int minutes, long? from, long? to, HistoryDimension? list, string? mod, ChartBuilder charts, MonitorCollector collector, MonitorSettings settings, HistoryStore store, IMonitorServers servers) =>
            {
                if (!BoardRequest.Known(store, servers, server))
                    return Results.NotFound();

                HistoryDimension shown = list ?? HistoryDimension.Grids;

                // A fixed stretch of the past needs nothing fresh, and is limited to what's kept.
                if (from.HasValue && to.HasValue)
                {
                    DateTime oldest = DateTime.UtcNow - settings.Retention;
                    DateTime start = BoardRequest.Moment(from.Value);
                    DateTime end = BoardRequest.Moment(to.Value);

                    if (start < oldest)
                        start = oldest;

                    return Reply(BoardRequest.Limited(start, end, () => charts.Build(view, server ?? string.Empty, start, end, shown, mod)));
                }

                // A browser asking for charts is what says someone's watching, so this server stays fresh.
                // The view of all servers names none, and uses the 30 second collection.
                if (!string.IsNullOrEmpty(server))
                    collector.Watch(server);

                int limit = settings.RetentionDays * 24 * 60;
                int range = Math.Clamp(minutes, 1, limit);
                DateTime now = DateTime.UtcNow;

                return Reply(BoardRequest.Limited(now.AddMinutes(-range), now, () => charts.Build(view, server ?? string.Empty, range, shown, mod)));
            });

        RouteHandlerBuilder board = endpoints.MapGet(
            "/" + BoardPage.DataRoute,
            (string? server, int? minutes, long? from, long? to, BoardBuilder boards, HistoryStore store, MonitorCollector collector, MonitorSettings settings) =>
            {
                BoardRequest.Answer<BoardResponse> answer = BoardRequest.Build(boards, store, collector, settings, server, minutes, from, to);

                return answer.Value == null ? Results.StatusCode(answer.Status) : Results.Json(answer.Value);
            });

        RouteHandlerBuilder page = endpoints.MapGet(
            "/" + BoardPage.PageRoute,
            (MonitorAssets assets) => Results.Content(BoardPage.Html(assets.Base), "text/html; charset=utf-8"));

        static IResult Reply(ChartsResponse? charts) => charts == null ? Results.StatusCode(StatusCodes.Status429TooManyRequests) : Results.Json(charts);

        if (policy == null)
            return;

        route.RequireAuthorization(policy);
        board.RequireAuthorization(policy);
        page.RequireAuthorization(policy);
    }
}
