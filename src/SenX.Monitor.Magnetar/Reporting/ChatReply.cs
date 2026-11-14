using System.Collections.Generic;
using PluginSdk.Commands;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>
/// Sends a report the way its reader can use it. Someone playing gets game chat, and anyone else gets
/// the Discord layout, because bots run commands as the console and relay the reply. The test is
/// whether a real player online asked, so a bot acting for a linked player who's offline counts as a bot.
/// </summary>
internal static class ChatReply
{
    public static ChatStyle StyleFor(CommandContext context) =>
        PlayerCommands.PlayerIn(context) != null ? ChatStyle.InGame : ChatStyle.Discord;

    /// <summary>Pages for a command to return, each one sent as a reply.</summary>
    public static List<string> For(CommandContext context, ChatReport report) => report.Render(StyleFor(context));

    /// <summary>Sends a report later, once a run has finished.</summary>
    public static void Send(CommandContext context, ChatReport report)
    {
        foreach (string page in For(context, report))
            context.Respond(page);
    }
}
