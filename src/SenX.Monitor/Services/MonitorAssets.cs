namespace SenX.Monitor.Services;

/// <summary>
/// Where this plugin's browser files are served from. It differs between Quasar and the preview host,
/// so a hard-coded path would work in one and break in the other.
/// </summary>
public sealed record MonitorAssets(string Base);
