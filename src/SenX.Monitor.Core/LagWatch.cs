using System;

namespace SenX.Monitor.Core;

/// <summary>Why lag was noticed. Numbers match what's reported.</summary>
public enum LagReason : uint
{
    None = 0,

    /// <summary>Sim speed stayed low.</summary>
    SimSpeed = 1,

    /// <summary>Frames stayed slow, even if sim speed hadn't fallen yet.</summary>
    FrameTime = 2,

    /// <summary>One frame took far too long.</summary>
    Spike = 3,
}

/// <summary>What counts as lag. Replaced whole, so a frame never sees half of a change.</summary>
public sealed class LagLimits
{
    public bool Enabled { get; set; } = true;

    /// <summary>Sim speed under this counts as a slow second.</summary>
    public double SimBelow { get; set; } = 0.9d;

    /// <summary>A frame p95 over this counts as a slow second. Zero is off.</summary>
    public double FrameP95AboveMs { get; set; }

    /// <summary>How many slow seconds in a row it takes.</summary>
    public int Seconds { get; set; } = 5;

    /// <summary>One frame over this counts at once. Zero is off.</summary>
    public double SpikeMs { get; set; } = 1000d;

    /// <summary>The least time between two trips, so lag can't keep setting off runs.</summary>
    public int CooldownSeconds { get; set; } = 900;

    /// <summary>How long after a character spawns a spike counts as a join hitch, which is recorded but sets off nothing. Zero is off.</summary>
    public int JoinGraceSeconds { get; set; } = 15;
}

/// <summary>
/// Notices lag by itself, so its cause can be caught while nobody is watching. Checks each second's
/// row at the end of a frame, on the game thread, and allocates nothing. Running out of budget can't
/// stop it, because it only reads a row the frame engine has already written.
/// </summary>
public static unsafe class LagWatch
{
    // Saves stop the game by design, and they have their own marker.
    private const int SaveGraceSeconds = 2;

    private static volatile LagLimits s_limits = new();
    private static int s_slowSeconds;
    private static long s_nextTrip;

    /// <summary>Told when lag is noticed, at the end of a frame on the game thread.</summary>
    public static Action<LagReason, double>? Tripped;

    /// <summary>Told when a spike came just after a character spawned, with how long it took in ms. Nothing else happens.</summary>
    public static Action<double>? JoinHitch;

    public static LagLimits Limits
    {
        get => s_limits;
        set => s_limits = value;
    }

    /// <summary>Called once a second with the row just written, unless the server is paused.</summary>
    internal static void OnSecond(RollupRecord* row, long now)
    {
        LagLimits limits = s_limits;

        // The first seconds after loading aren't lag, and a run's own timing mustn't look like it.
        if (Tripped == null || !limits.Enabled || !DetailController.Ready || !Capture.Idle)
        {
            s_slowSeconds = 0;
            return;
        }

        double sim = row->SimSpeed;
        double p95 = row->FrameP95 / 10000d;
        double worst = row->FrameMax / 10000d;
        bool lowSim = sim < limits.SimBelow;
        bool slowFrames = limits.FrameP95AboveMs > 0d && p95 > limits.FrameP95AboveMs;

        s_slowSeconds = lowSim || slowFrames ? s_slowSeconds + 1 : 0;
        bool spike = limits.SpikeMs > 0d && worst > limits.SpikeMs && !Markers.SaveJustEnded(now, SaveGraceSeconds);

        // A character's first spawn after a start makes the game read every voxel map file on this thread, for the
        // asteroids around it, which takes seconds. That's recorded as a join hitch, and sets off no alert or run.
        if (spike && Markers.SpawnedWithin(now, limits.JoinGraceSeconds))
        {
            Markers.AddJoinHitch(worst);
            JoinHitch?.Invoke(worst);
            return;
        }

        if (now < s_nextTrip)
            return;

        if (s_slowSeconds >= limits.Seconds)
            Trip(limits, now, lowSim ? LagReason.SimSpeed : LagReason.FrameTime, lowSim ? sim : p95);
        else if (spike)
            Trip(limits, now, LagReason.Spike, worst);
    }

    private static void Trip(LagLimits limits, long now, LagReason reason, double value)
    {
        s_slowSeconds = 0;
        s_nextTrip = now + (limits.CooldownSeconds * Clock.Frequency);
        Tripped?.Invoke(reason, value);
    }

    internal static void ResetForTests()
    {
        s_limits = new LagLimits();
        s_slowSeconds = 0;
        s_nextTrip = 0L;
        Tripped = null;
        JoinHitch = null;
        Markers.ForgetSpawn();
    }
}
