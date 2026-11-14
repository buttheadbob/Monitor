// What the shared reports take from Magnetar's PluginSdk: who asked, and how to answer them. Torch's
// commands in MonitorCommands.cs fill these in, so the reports run as they are.
namespace PluginSdk.Commands;

internal sealed class CommandCaller(bool isConsole, ulong steamId, long identityId, string name)
{
    public bool IsConsole { get; } = isConsole;

    public ulong SteamId { get; } = steamId;

    public long IdentityId { get; } = identityId;

    public string Name { get; } = name;
}

internal sealed class CommandContext(CommandCaller caller, Action<string> respond)
{
    public CommandCaller Caller { get; } = caller;

    public void Respond(string message) => respond(message);
}
