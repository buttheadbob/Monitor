namespace SenX.Monitor.Api;

/// <summary>
/// What server plugins can read from Monitor about the server they're running on, and nothing else.
/// Every other server is Quasar's business, through <see cref="IMonitorApi"/>. These are the same tools
/// a Quasar plugin gets, with the same replies. On Torch they read Monitor's whole history. On a Magnetar
/// server, Quasar holds that, so history only goes back about an hour, which is what the worker keeps, and
/// <see cref="MonitorSource.SavedRun"/> only finds the latest run. <see cref="IMonitorTools.SampleRecorded"/>
/// is there for a plugin that wants to keep more than that itself.
///
/// Get it from <see cref="MonitorServerApi.Current"/>. Never wait on a task from this on the game
/// thread, because some answers need the game thread to finish.
/// </summary>
public interface IMonitorServerApi : IMonitorTools
{
    /// <summary>Goes up with each addition. Check it before using something newer than version 1.</summary>
    int Version { get; }
}

/// <summary>Where server plugins find Monitor. Magnetar has no shared services, so this is the meeting point.</summary>
public static class MonitorServerApi
{
    private static volatile IMonitorServerApi? s_current;

    /// <summary>Monitor on this server. Null until it has started, or if it has shut itself down.</summary>
    public static IMonitorServerApi? Current => s_current;

    /// <summary>Monitor calls this. Nothing else should.</summary>
    public static void Register(IMonitorServerApi? api) => s_current = api;
}
