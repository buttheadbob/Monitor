using System;
using PluginSdk.Logging;

namespace SenX.Monitor.Magnetar;

/// <summary>
/// Plugin logging, through Magnetar's SDK logger. It falls back to the console when that isn't
/// available, such as under the offline verify tool. Never called on a hot path.
/// </summary>
internal static class MonitorLog
{
    private static Logger? s_logger;

    public static void UseSdkLogger() => s_logger = Logger.Create("Monitor");

    public static void Info(string message)
    {
        if (s_logger != null)
            s_logger.Info(message);
        else
            Console.WriteLine(message);
    }

    public static void Warning(string message)
    {
        if (s_logger != null)
            s_logger.Warning(message);
        else
            Console.WriteLine("WARNING: " + message);
    }

    public static void Error(string message, Exception exception)
    {
        if (s_logger != null)
            s_logger.Error(message, exception);
        else
            Console.WriteLine("ERROR: " + message + " " + exception);
    }
}
