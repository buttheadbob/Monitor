namespace SenX.Monitor.Services;

/// <summary>Quasar's policy names, written out because this project also runs in the preview host without Quasar.</summary>
public static class MonitorPolicies
{
    public const string View = "CanView";

    /// <summary>Starting a run changes how a server is timed, and clusters how physics is scheduled.</summary>
    public const string Control = "CanControlServers";

    /// <summary>Changing how long history is kept deletes what's older, so it's not for viewers.</summary>
    public const string Settings = "CanEditConfigs";

    /// <summary>A webhook URL lets anyone who has it post to that channel, so it's treated like Quasar's own Discord settings.</summary>
    public const string Discord = "CanManageDiscord";
}
