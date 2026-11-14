using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Sandbox;
using SenX.Monitor.Contracts;
using SenX.Monitor.Magnetar.Patching;
using SenX.Monitor.Magnetar.Reporting;

namespace SenX.Monitor.Magnetar;

/// <summary>
/// The way in for the Quasar companion. Magnetar compiles this plugin itself, under a name of its own, so the
/// companion can't reference it. It finds this class by name and calls it through reflection, and only strings cross
/// over. This runs off the game thread, and anything that needs the game hops onto it.
/// </summary>
public static class QuasarBridge
{
    /// <summary>Answers one request with its JSON. Throws with a message for the admin if it can't.</summary>
    private const string ClosedMessage = "Monitor has shut itself down on this server. The server log says why.";

    public static async Task<string> AnswerAsync(string? operation, string? payloadJson)
    {
        try
        {
            switch (operation?.ToLowerInvariant())
            {
                case MonitorProtocol.RuntimeInfoOperation:
                    return JsonConvert.SerializeObject(Worker.ReadRuntimeInfo());
                case MonitorProtocol.HistoryPullOperation:
                    // This is how Quasar finds out, and the page says so, rather than just showing old numbers.
                    if (Worker.Closed)
                        throw new InvalidOperationException(ClosedMessage);

                    return PackHistory(payloadJson);
                case MonitorProtocol.IdentityLookupOperation:
                    return JsonConvert.SerializeObject(await IdentityService.LookupAsync(Read<IdentityRequest>(payloadJson)).ConfigureAwait(false));
                case MonitorProtocol.RunStartOperation:
                    return JsonConvert.SerializeObject(await RunService.StartAsync(Read<RunStartRequest>(payloadJson)).ConfigureAwait(false));
                case MonitorProtocol.RunResultOperation:
                    return RunService.ResultJson(Read<RunResultRequest>(payloadJson).RunId);
                case MonitorProtocol.IncidentResultOperation:
                    return IncidentService.ResultJson(Read<IncidentResultRequest>(payloadJson).Id);
                case MonitorProtocol.PatchListOperation:
                    return JsonConvert.SerializeObject(PatchList.Page(Read<PatchListRequest>(payloadJson).Skip));
                case MonitorProtocol.AuditStartOperation:
                    AuditStartRequest audit = Read<AuditStartRequest>(payloadJson);
                    return await OnGameThreadAsync(() => PatchAudit.Start(audit)).ConfigureAwait(false);
                case MonitorProtocol.AuditResultOperation:
                    return PatchAudit.ResultJson();
                case MonitorProtocol.DrillRootsOperation:
                    string? text = Read<DrillRootsRequest>(payloadJson).Text;
                    return await OnGameThreadAsync(() => new DrillRootsResponse { Roots = DrillDown.Roots(text) }).ConfigureAwait(false);
                case MonitorProtocol.DrillStartOperation:
                    DrillStartRequest drill = Read<DrillStartRequest>(payloadJson);
                    return await OnGameThreadAsync(() => DrillDown.Start(drill)).ConfigureAwait(false);
                case MonitorProtocol.DrillResultOperation:
                    return DrillDown.ResultJson();
                case MonitorProtocol.TriggerSetOperation:
                    IncidentService.Configure(Read<TriggerSettings>(payloadJson));
                    return JsonConvert.SerializeObject(new TriggerSetResponse { Applied = true });
                default:
                    throw new NotSupportedException($"Unsupported Monitor operation '{operation}'.");
            }
        }
        catch (Exception exception) when (!(exception is NotSupportedException))
        {
            MonitorLog.Error("Monitor could not answer Quasar.", exception);
            throw new InvalidOperationException(Worker.Closed ? ClosedMessage : "Monitor could not answer that request.");
        }
    }

    // Patching belongs on the game thread, between frames, and so does reading what's in the session. Quasar's
    // agent handles one command at a time, so a wait with no end here would stop everything else reaching this server.
    private static async Task<string> OnGameThreadAsync<T>(Func<T> work) =>
        JsonConvert.SerializeObject(await ServerApi.OnGameThreadAsync(work, CancellationToken.None).ConfigureAwait(false));

    private static T Read<T>(string? payloadJson) where T : class, new() =>
        (string.IsNullOrEmpty(payloadJson) ? null : JsonConvert.DeserializeObject<T>(payloadJson!)) ?? new T();

    // A JSON string, so the channel can read it straight into a byte array.
    private static string PackHistory(string? payloadJson) =>
        Worker.PackHistory(Read<HistoryPullRequest>(payloadJson), (bytes, length) => "\"" + Convert.ToBase64String(bytes, 0, length) + "\"");
}
