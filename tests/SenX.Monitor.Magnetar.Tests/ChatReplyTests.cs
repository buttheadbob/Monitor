using System;
using System.Collections.Generic;
using PluginSdk.Commands;
using SenX.Monitor.Contracts;
using SenX.Monitor.Magnetar.Reporting;
using VRage.Game.ModAPI;
using Xunit;

namespace SenX.Monitor.Magnetar.Tests;

public sealed class ChatReplyTests
{
    private const ulong SteamId = 1001UL;

    private sealed class Capture : ICommandResponder
    {
        public List<string> Sent { get; } = new();

        public void Send(in CommandReply reply, in CommandCaller caller) => Sent.Add(reply.Text);
    }

    private static CommandContext ContextFor(in CommandCaller caller, ICommandResponder responder) =>
        new(caller, "monitor", Array.Empty<string>(), string.Empty, responder);

    [Fact]
    public void A_console_caller_gets_the_discord_layout()
    {
        // This is how Quasar's chat page calls, and how a bot on Magnetar would.
        CommandContext context = ContextFor(new CommandCaller(0UL, 0L, "Quasar", MyPromoteLevel.Owner, isConsole: true), new Capture());

        Assert.Equal(ChatStyle.Discord, ChatReply.StyleFor(context));
    }

    [Fact]
    public void A_caller_marked_as_the_console_is_never_a_player()
    {
        CommandContext context = ContextFor(new CommandCaller(SteamId, 5L, "Someone", MyPromoteLevel.Owner, isConsole: true), new Capture());

        Assert.Null(PlayerCommands.PlayerIn(context));
    }

    [Fact]
    public void Someone_with_no_game_to_be_in_isnt_in_game()
    {
        // A bot acting for a linked player looks like a player, but this has to check they're there.
        CommandContext context = ContextFor(new CommandCaller(SteamId, 5L, "Linked", MyPromoteLevel.Admin), new Capture());

        Assert.Equal(ChatStyle.Discord, ChatReply.StyleFor(context));
    }

    [Fact]
    public void A_report_is_sent_a_page_at_a_time()
    {
        var capture = new Capture();
        var report = new ChatReport().Title("Long");

        for (int row = 0; row < 200; row++)
            report.Item("A row with a reasonably long name in it " + row);

        ChatReply.Send(ContextFor(new CommandCaller(0UL, 0L, "Bot", MyPromoteLevel.Owner, isConsole: true), capture), report);

        Assert.True(capture.Sent.Count > 1);
        Assert.Equal(report.Render(ChatStyle.Discord), capture.Sent);
    }

    [Fact]
    public void A_list_of_pages_is_what_a_command_can_return()
    {
        // The dispatcher sends each item of a returned list as its own reply.
        var capture = new Capture();
        CommandContext context = ContextFor(new CommandCaller(0UL, 0L, "Bot", MyPromoteLevel.Owner, isConsole: true), capture);

        List<string> pages = ChatReply.For(context, new ChatReport().Title("Status"));

        Assert.Equal("### Status", Assert.Single(pages));
    }
}
