using SenX.Monitor.Contracts;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

/// <summary>
/// The companion names these as text, because it can't reference a plugin the loader compiles under a name of its
/// own. So nothing but this notices if one side changes, and on a server that reads as the plugin not being there.
/// </summary>
public sealed class CompanionTests
{
    private static string Source(string path)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            string file = Path.Combine(folder.FullName, path);

            if (File.Exists(file))
                return File.ReadAllText(file);
        }

        throw new FileNotFoundException(path);
    }

    [Fact]
    public void The_companion_answers_to_the_id_the_quasar_plugin_asks_for()
    {
        Assert.Contains($"Id = \"{MonitorProtocol.PluginId}\"", Source("src/SenX.Monitor.QuasarCompanion/MonitorCompanion.cs"));
    }

    [Fact]
    public void The_bridge_is_where_the_companion_looks()
    {
        string companion = Source("src/SenX.Monitor.QuasarCompanion/MonitorCompanion.cs");
        string bridge = Source("src/SenX.Monitor.Magnetar/QuasarBridge.cs");

        Assert.Contains("BridgeType = \"SenX.Monitor.Magnetar.QuasarBridge\"", companion);
        Assert.Contains("BridgeMethod = \"AnswerAsync\"", companion);
        Assert.Contains("namespace SenX.Monitor.Magnetar;", bridge);
        Assert.Contains("public static class QuasarBridge", bridge);
        Assert.Contains("public static async Task<string> AnswerAsync(string? operation, string? payloadJson)", bridge);
    }

    [Fact]
    public void A_server_without_the_plugin_is_told_how_to_fix_it()
    {
        string companion = Source("src/SenX.Monitor.QuasarCompanion/MonitorCompanion.cs");

        Assert.Contains("throw new InvalidOperationException(NotRunning)", companion);
        Assert.Contains("isn't running on this server", companion);
        Assert.Contains("as a dev folder with Monitor.xml", companion);
        Assert.Contains("turn it on in this server's config, and restart the server", companion);
        Assert.Contains("check the server's log for a Monitor build error", companion);

        // The manifest it names is the one Magnetar loads.
        Assert.Contains("<Id>f58cf508-46b1-4964-9836-fd42429adbe0</Id>", Source("Monitor.xml"));
    }
}
