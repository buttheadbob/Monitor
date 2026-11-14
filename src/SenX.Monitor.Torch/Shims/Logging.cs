// What the shared services take from Microsoft.Extensions.Logging. Messages end up in Torch's log through
// the sink the plugin sets, so the pages themselves can run with no Torch at all.
namespace Microsoft.Extensions.Logging;

public interface ILogger<out TCategory>
{
    void Write(Exception? exception, string message, object?[] values);
}

/// <summary>Where every message goes.</summary>
public static class MonitorLogSink
{
    /// <summary>Takes the text and any exception. Null drops messages.</summary>
    public static Action<string, Exception?>? Write { get; set; }
}

public sealed class SinkLogger<TCategory> : ILogger<TCategory>
{
    public void Write(Exception? exception, string message, object?[] values) =>
        MonitorLogSink.Write?.Invoke(Fill(message, values), exception);

    // Message templates name their holes, and values fill them in order.
    private static string Fill(string message, object?[] values)
    {
        foreach (object? value in values)
        {
            int open = message.IndexOf('{');
            int close = open < 0 ? -1 : message.IndexOf('}', open);

            if (close < 0)
                break;

            message = message.Substring(0, open) + value + message.Substring(close + 1);
        }

        return message;
    }
}

public static class LoggerExtensions
{
    // The collector logs this for every failed pull, which is every second while someone's watching.
    public static void LogDebug<T>(this ILogger<T> logger, Exception? exception, string message, params object?[] values)
    {
    }

    public static void LogInformation<T>(this ILogger<T> logger, string message, params object?[] values) =>
        logger.Write(null, message, values);

    public static void LogWarning<T>(this ILogger<T> logger, Exception? exception, string message, params object?[] values) =>
        logger.Write(exception, message, values);

    public static void LogWarning<T>(this ILogger<T> logger, string message, params object?[] values) =>
        logger.Write(null, message, values);
}
