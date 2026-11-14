using System;
using System.Reflection;
using PluginSdk.Commands;
using SenX.Monitor.Magnetar.Reporting;
using VRage.Plugins;

namespace SenX.Monitor.Magnetar;

/// <summary>
/// Monitor on Magnetar: the worker. Quasar reaches it through the companion, by way of <see cref="QuasarBridge"/>.
/// If anything here goes wrong, Monitor shuts itself down and the game carries on as if it were never installed.
/// </summary>
public sealed class MonitorPlugin : IPlugin
{
    private readonly Worker _worker = new();

    public void Init(object gameInstance)
    {
        try
        {
            MonitorLog.UseSdkLogger();
            _worker.Prepare();
            _worker.Start();
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor failed to start and has shut itself down.", exception);
            _worker.GiveUp();
            return;
        }

        try
        {
            ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(MonitorCommands));
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor's chat commands are unavailable, measuring continues.", exception);
        }
    }

    public void Update() => _worker.Update();

    public void Dispose() => _worker.Shutdown();
}
