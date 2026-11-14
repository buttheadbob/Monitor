using System.Text.Json;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Services;

/// <summary>What an admin can change, kept beside the history it applies to.</summary>
public sealed class MonitorSettings(string directory)
{
    public const int DefaultRetentionDays = 3;
    public const int MaxRetentionDays = 7;

    private const int MaxTriggerSeconds = 600;
    private const int MaxTriggeredRunSeconds = 60;
    private const int MinCooldownSeconds = 60;
    private const int MaxCooldownSeconds = 86400;
    private const int MaxJoinGraceSeconds = 120;
    private const int ReadAttempts = 3;
    private const int ReadRetryMs = 50;

    private readonly object _gate = new();
    private Saved? _saved;
    private string? _problem;
    private bool _holdSaves;
    private long _triggersVersion;

    // Kept apart, because this gets read from places that mustn't wait for settings being saved.
    private volatile int _retentionDays;

    private sealed class Saved
    {
        public int RetentionDays { get; set; } = DefaultRetentionDays;

        public TriggerSettings Triggers { get; set; } = new();

        /// <summary>For every server without its own.</summary>
        public string? DefaultWebhook { get; set; }

        public Dictionary<string, string> Webhooks { get; set; } = [];
    }

    private string Path => System.IO.Path.Combine(directory, "settings.json");

    /// <summary>How many days of history to keep, from 1 to <see cref="MaxRetentionDays"/>.</summary>
    public int RetentionDays
    {
        get
        {
            int days = _retentionDays;

            if (days != 0)
                return days;

            lock (_gate)
                return _retentionDays = Current.RetentionDays;
        }

        set
        {
            lock (_gate)
            {
                Current.RetentionDays = Limit(value);
                _retentionDays = Current.RetentionDays;
                Save();
            }
        }
    }

    public TimeSpan Retention => TimeSpan.FromDays(RetentionDays);

    /// <summary>What counts as lag on every server. This is a copy, so changing it takes setting it back.</summary>
    public TriggerSettings Triggers
    {
        get
        {
            lock (_gate)
                return Limit(Current.Triggers);
        }

        set
        {
            lock (_gate)
            {
                Current.Triggers = Limit(value);
                _triggersVersion++;
                Save();
            }
        }
    }

    /// <summary>Where a server's alerts go, or null if nowhere. The URL is a secret, so it never goes back to a page.</summary>
    public string? WebhookFor(string serverId)
    {
        lock (_gate)
            return Current.Webhooks.GetValueOrDefault(serverId) ?? Current.DefaultWebhook;
    }

    /// <summary>True when this server has a webhook of its own.</summary>
    public bool HasOwnWebhook(string serverId)
    {
        lock (_gate)
            return Current.Webhooks.ContainsKey(serverId);
    }

    /// <summary>True when there's a webhook for every server without its own.</summary>
    public bool HasDefaultWebhook
    {
        get
        {
            lock (_gate)
                return Current.DefaultWebhook != null;
        }
    }

    /// <summary>
    /// Sets a webhook for one server, or for all without their own if the server is null. Null or empty
    /// removes it. Returns false for anything that isn't a Discord webhook, because this makes
    /// Quasar post to the address, and it mustn't be able to reach anything else.
    /// </summary>
    public bool SetWebhook(string? serverId, string? url)
    {
        bool remove = string.IsNullOrWhiteSpace(url);

        if (!remove && !IsDiscordWebhook(url!))
            return false;

        lock (_gate)
        {
            if (serverId == null)
                Current.DefaultWebhook = remove ? null : url!.Trim();
            else if (remove)
                Current.Webhooks.Remove(serverId);
            else
                Current.Webhooks[serverId] = url!.Trim();

            Save();
        }

        return true;
    }

    internal static bool IsDiscordWebhook(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host is "discord.com" or "ptb.discord.com" or "canary.discord.com" or "discordapp.com"
        && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);

    /// <summary>Changes when <see cref="Triggers"/> does, so the collector knows to tell the servers.</summary>
    public long TriggersVersion
    {
        get
        {
            lock (_gate)
                return _triggersVersion;
        }
    }

    private Saved Current => _saved ??= Load();

    private static int Limit(int days) => Math.Clamp(days, 1, MaxRetentionDays);

    private static TriggerSettings Limit(TriggerSettings settings) => new()
    {
        Enabled = settings.Enabled,
        SimBelow = Math.Clamp(Finite(settings.SimBelow), 0.1d, 1d),
        FrameP95AboveMs = Math.Max(Finite(settings.FrameP95AboveMs), 0d),
        Seconds = Math.Clamp(settings.Seconds, 1, MaxTriggerSeconds),
        SpikeMs = Math.Max(Finite(settings.SpikeMs), 0d),
        RunSeconds = Math.Clamp(settings.RunSeconds, 0, MaxTriggeredRunSeconds),
        CooldownSeconds = Math.Clamp(settings.CooldownSeconds, MinCooldownSeconds, MaxCooldownSeconds),
        JoinGraceSeconds = Math.Clamp(settings.JoinGraceSeconds, 0, MaxJoinGraceSeconds),
        TimeClusters = settings.TimeClusters,
        TellAdmins = settings.TellAdmins,
        PlayerMine = settings.PlayerMine,
        PlayerTest = settings.PlayerTest,
        PlayerTestSeconds = Math.Clamp(settings.PlayerTestSeconds, 1, MaxTriggeredRunSeconds),
        PlayerTestCooldownSeconds = Math.Clamp(settings.PlayerTestCooldownSeconds, MinCooldownSeconds, MaxCooldownSeconds),
        PlayerTestLowestSim = Math.Clamp(Finite(settings.PlayerTestLowestSim), 0d, 1d),
    };

    // A number that isn't one survives Clamp, and then can't be saved or sent to the server.
    private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 0d : value;

    private Saved Load()
    {
        if (!File.Exists(Path))
            return new Saved();

        string text;

        try
        {
            text = ReadWithRetries();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file may well be fine, so nothing is saved over it, and it's read again at the next start.
            _holdSaves = true;
            _problem = "Monitor could not read its settings, so it's using defaults until it restarts. They won't be saved, so the file stays as it was.";
            return new Saved { RetentionDays = MaxRetentionDays };
        }

        try
        {
            Saved? saved = JsonSerializer.Deserialize<Saved>(text);

            if (saved == null || saved.RetentionDays < 1)
                return Damaged();

            saved.RetentionDays = Limit(saved.RetentionDays);
            saved.Triggers = Limit(saved.Triggers ?? new TriggerSettings());
            saved.Webhooks = (saved.Webhooks ?? []).Where(entry => IsDiscordWebhook(entry.Value)).ToDictionary(entry => entry.Key, entry => entry.Value);

            if (saved.DefaultWebhook != null && !IsDiscordWebhook(saved.DefaultWebhook))
                saved.DefaultWebhook = null;

            return saved;
        }
        catch (JsonException)
        {
            return Damaged();
        }
    }

    // Something else can have the file open for a moment, and that isn't damage.
    private string ReadWithRetries()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(Path);
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                Thread.Sleep(ReadRetryMs);
            }
        }
    }

    // Kept for an admin to look at, under a name of its own so a second time can't replace the first. Falling back
    // to a shorter retention would delete history for good, so this takes the longest.
    private Saved Damaged()
    {
        string kept = Path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            File.Copy(Path, kept, overwrite: false);
            _problem = "Monitor could not read its settings, so they're back to defaults. The old file is kept as " + System.IO.Path.GetFileName(kept) + ".";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Saving now would destroy the only copy.
            _holdSaves = true;
            _problem = "Monitor could not read its settings and could not keep a copy, so they won't be saved over.";
        }

        return new Saved { RetentionDays = MaxRetentionDays };
    }

    /// <summary>What went wrong reading or saving the settings, once, for the log.</summary>
    public string? TakeProblem()
    {
        lock (_gate)
        {
            // Reading them is what finds a problem, so this has to happen first.
            _ = Current;

            string? problem = _problem;
            _problem = null;
            return problem;
        }
    }

    private void Save()
    {
        if (_holdSaves)
        {
            _problem = "Monitor's settings changed but weren't saved, because the file on disk couldn't be read. Restart once that's mended.";
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            StateFiles.Write(Path, JsonSerializer.Serialize(Current));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // It still applies until Quasar restarts.
            _problem = "Monitor could not save its settings, so a change will be lost at the next restart: " + exception.GetType().Name + ".";
        }
    }
}
