using System.Net;

namespace SenX.Monitor.Services;

/// <summary>
/// The page a full board opens in. It's plain HTML rather than a Blazor page, so Quasar and the small web
/// server in the Torch plugin serve exactly the same thing, and both get the same charts from one codebase.
/// </summary>
public static class BoardPage
{
    /// <summary>The page.</summary>
    public const string PageRoute = "monitor/board";

    /// <summary>The page's data.</summary>
    public const string DataRoute = "api/monitor/board";

    /// <summary>The page, finding its files under this path.</summary>
    public static string Html(string assetBase)
    {
        // The page lives at monitor/board, so a path relative to the site's root wouldn't work from there.
        string files = WebUtility.HtmlEncode(assetBase.StartsWith("/", StringComparison.Ordinal) ? assetBase : "../" + assetBase);

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Monitor</title>
            <link rel="stylesheet" href="{{files}}lib/uplot/uPlot.min.css">
            <link rel="stylesheet" href="{{files}}board.css">
            </head>
            <body data-api="../{{DataRoute}}">
            <div id="board"></div>
            <script src="{{files}}lib/uplot/uPlot.iife.min.js"></script>
            <script src="{{files}}board.js"></script>
            </body>
            </html>
            """;
    }
}

/// <summary>Turns a request for a board into the board, the same way on every host.</summary>
public static class BoardRequest
{
    private const int DefaultMinutes = 60;
    private const int MostLongReads = 2;
    private const long LatestSeconds = 253402300799L;

    private static readonly TimeSpan s_longRead = TimeSpan.FromHours(1d);
    private static readonly SemaphoreSlim s_longReads = new(MostLongReads, MostLongReads);

    /// <summary>What was asked for, with what to answer: 200, 404 for a server there's no record of, or 429 when busy.</summary>
    public readonly record struct Answer<T>(T? Value, int Status)
        where T : class;

    /// <summary>
    /// Whether this names a server there's history for. Every id asked about is remembered from then on, so
    /// anything from a request is checked first, or sending made-up ids would grow memory for as long as the host runs.
    /// </summary>
    public static bool Known(HistoryStore store, IMonitorServers servers, string? server) =>
        string.IsNullOrEmpty(server)
        || servers.Connected().Any(item => item.HistoryId == server)
        || store.KnownServers().Any(item => item.Id == server);

    /// <summary>
    /// Runs a read, turning it down if too many long ones are going. Past an hour they read files and can take a
    /// lot of memory, which on Torch is in the game's process, and a page simply tries again at its next refresh.
    /// </summary>
    public static T? Limited<T>(DateTime from, DateTime to, Func<T> read)
        where T : class
    {
        if (to - from <= s_longRead)
            return read();

        if (!s_longReads.Wait(0))
            return null;

        try
        {
            return read();
        }
        finally
        {
            s_longReads.Release();
        }
    }

    /// <summary>A time from a request, kept to what a date can hold.</summary>
    public static DateTime Moment(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(Math.Min(Math.Max(seconds, 0L), LatestSeconds)).UtcDateTime;

    public static Answer<BoardResponse> Build(BoardBuilder boards, HistoryStore store, MonitorCollector collector, MonitorSettings settings, string? server, int? minutes, long? from, long? to)
    {
        // A host with one server has no board of all of them, so naming none just means that one.
        IReadOnlyList<MonitorServer> connected = boards.Connected();
        string shown = server ?? (connected.Count == 1 ? connected[0].HistoryId : string.Empty);

        if (!boards.Knows(store, shown))
            return new Answer<BoardResponse>(null, 404);

        DateTime start;
        DateTime end;

        // A fixed stretch of the past needs nothing fresh, and is limited to what's kept.
        if (from.HasValue && to.HasValue)
        {
            DateTime oldest = DateTime.UtcNow - settings.Retention;
            start = Moment(from.Value);
            end = Moment(to.Value);

            if (start < oldest)
                start = oldest;
        }
        else
        {
            // Having a board open is what says someone's watching. The board of all servers names none, and uses the 30 second collection.
            if (shown.Length > 0)
                collector.Watch(shown);

            end = DateTime.UtcNow;
            start = end.AddMinutes(-Math.Clamp(minutes ?? DefaultMinutes, 1, settings.RetentionDays * 24 * 60));
        }

        BoardResponse? board = Limited(start, end, () => boards.Build(shown, start, end));

        return new Answer<BoardResponse>(board, board == null ? 429 : 200);
    }
}
