using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Bridge;
using Magnetar.Protocol.Model;
using VRage.Plugins;

namespace SenX.Monitor.QuasarCompanion;

/// <summary>
/// Passes the Quasar plugin's requests to the Monitor Magnetar plugin. Magnetar compiles that plugin under a
/// name of its own, so this finds its bridge by name instead of referencing it. A server without the plugin
/// says so, and Quasar shows that on the page. Requests arrive off the game thread.
/// </summary>
public sealed class MonitorCompanion : IPlugin, IQuasarCompanionRequestHandler
{
    // Must match MonitorProtocol.PluginId, which is what the Quasar plugin asks for.
    private const string Id = "senx.monitor";
    private const string BridgeType = "SenX.Monitor.Magnetar.QuasarBridge";
    private const string BridgeMethod = "AnswerAsync";

    // Shown on Quasar's page, so it says how to fix it as well as what's wrong.
    private const string NotRunning =
        "Monitor's server plugin isn't running on this server. In Quasar, add Monitor (from MagnetarHub, or as a dev folder with Monitor.xml), " +
        "turn it on in this server's config, and restart the server. If it is on, check the server's log for a Monitor build error.";

    private MethodInfo? _answer;

    public string PluginId => Id;

    public void Init(object gameInstance)
    {
    }

    public void Update()
    {
    }

    public void Dispose()
    {
    }

    public async Task<CompanionPluginResponse> HandleQuasarCompanionRequestAsync(CompanionPluginRequest request, CancellationToken cancellationToken)
    {
        var response = new CompanionPluginResponse { SchemaVersion = request.SchemaVersion, CorrelationId = request.CorrelationId };

        try
        {
            MethodInfo answer = FindBridge() ?? throw new InvalidOperationException(NotRunning);
            var answering = (Task<string>)answer.Invoke(null, new object?[] { request.Operation, request.PayloadJson })!;

            // Stops the wait and nothing else, because the plugin can't be told to stop partway. The token lasts as
            // long as the agent, so a wait tied straight to it would be left behind after every request.
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task stopped = Task.Delay(Timeout.Infinite, waiting.Token);

                if (await Task.WhenAny(answering, stopped).ConfigureAwait(false) != answering)
                    cancellationToken.ThrowIfCancellationRequested();

                waiting.Cancel();
            }

            response.PayloadJson = await answering.ConfigureAwait(false);
            response.Success = true;
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            response.Error = exception.InnerException.Message;
        }
        catch (Exception exception) when (!(exception is OperationCanceledException))
        {
            response.Error = exception.Message;
        }

        return response;
    }

    private MethodInfo? FindBridge()
    {
        if (_answer != null)
            return _answer;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            MethodInfo? method = assembly.GetType(BridgeType, throwOnError: false)?.GetMethod(BridgeMethod, BindingFlags.Public | BindingFlags.Static);

            if (method != null)
                return _answer = method;
        }

        return null;
    }
}
