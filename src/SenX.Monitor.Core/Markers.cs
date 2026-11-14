using System.Runtime.InteropServices;
using System.Threading;

namespace SenX.Monitor.Core;

/// <summary>What a marker says happened. Numbers match the reader's.</summary>
public enum MarkerKind : uint
{
    /// <summary>A world save ended. The record's value is how long its frame took, in ms.</summary>
    Saved = 1,
    SaveFailed = 2,

    /// <summary>Lag was noticed. The record's value says which report holds the evidence.</summary>
    LagTripped = 3,

    /// <summary>A run began timing every call. The record's value is how many seconds it's set for.</summary>
    RunStarted = 4,

    /// <summary>A run started from chat finished. The record's value says which, so Quasar can collect it.</summary>
    RunFinished = 5,

    /// <summary>Background detail changed. The record's value is the new <see cref="DetailMode"/>.</summary>
    DetailChanged = 6,

    /// <summary>One long frame just after a character spawned. The record's value is how long it took, in ms.</summary>
    JoinHitch = 7,
}

/// <summary>A moment worth a line on the charts.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MarkerRecord
{
    public long UnitsSinceStart;
    public uint Kind;
    public uint Value;
}

/// <summary>
/// Moments the charts should show, such as world saves. The game can announce these from any thread
/// and the ring has one writer, so a moment is only counted here, and the game thread writes it on its
/// next frame. Restarts and plugin or mod changes aren't here, because Quasar works those out itself.
/// </summary>
public static unsafe class Markers
{
    /// <summary>About a day of five-minute saves, far longer than Quasar is ever away for.</summary>
    public const int RingRows = 1 << 8;

    private static RecordRing? s_rows;
    private static int s_saved;
    private static int s_saveFailed;
    private static long s_lastSave;
    private static long s_lastSpawn;
    private static int s_detail;

    public static RecordRing Rows => s_rows!;

    internal static void Initialize()
    {
        if (s_rows == null)
            s_rows = new RecordRing(sizeof(MarkerRecord), RingRows);
    }

    /// <summary>Says a world save ended. From any thread.</summary>
    public static void NoteSave(bool succeeded)
    {
        if (succeeded)
            Interlocked.Increment(ref s_saved);
        else
            Interlocked.Increment(ref s_saveFailed);
    }

    /// <summary>Writes what has been noted. Once a frame, on the game thread. Allocates nothing.</summary>
    public static void Flush()
    {
        if (Volatile.Read(ref s_saved) != 0 && Interlocked.Exchange(ref s_saved, 0) != 0)
            WroteSave(MarkerKind.Saved);

        if (Volatile.Read(ref s_saveFailed) != 0 && Interlocked.Exchange(ref s_saveFailed, 0) != 0)
            WroteSave(MarkerKind.SaveFailed);

        if (Volatile.Read(ref s_detail) != 0)
            Write(MarkerKind.DetailChanged, (uint)(Interlocked.Exchange(ref s_detail, 0) - 1));
    }

    /// <summary>Says lag was noticed. Game thread only, because this writes straight to the ring.</summary>
    public static void AddLag(uint report) => Write(MarkerKind.LagTripped, report);

    /// <summary>Says a player's character just spawned. From any thread.</summary>
    public static void NoteSpawn() => Volatile.Write(ref s_lastSpawn, Clock.Now());

    /// <summary>
    /// Says a character just spawned, which counts only when a person plays it. Mods such as MES spawn NPCs
    /// often, and each would otherwise open a window that hides real spikes. An NPC identity, or one with no
    /// player, has no Steam id, and the game's bots are players on the server's own Steam id with a serial
    /// above zero. From any thread.
    /// </summary>
    public static void NoteCharacterSpawn(bool npc, ulong steamId, int serialId)
    {
        if (IsPlayer(npc, steamId, serialId))
            NoteSpawn();
    }

    internal static bool IsPlayer(bool npc, ulong steamId, int serialId) => !npc && steamId != 0UL && serialId == 0;

    /// <summary>Says a long frame came just after a spawn. Game thread only.</summary>
    internal static void AddJoinHitch(double ms) => Write(MarkerKind.JoinHitch, ms >= uint.MaxValue ? uint.MaxValue : (uint)ms);

    /// <summary>True when a character spawned less than this many seconds ago. Zero seconds is never.</summary>
    internal static bool SpawnedWithin(long now, int seconds)
    {
        long spawned = Volatile.Read(ref s_lastSpawn);
        return seconds > 0 && spawned != 0L && now - spawned < seconds * Clock.Frequency;
    }

    internal static void ForgetSpawn() => Volatile.Write(ref s_lastSpawn, 0L);

    /// <summary>Says a chat run finished. Game thread only.</summary>
    public static void AddRunFinished(uint run) => Write(MarkerKind.RunFinished, run);

    // A run makes frames slower, so a chart needs to say why.
    internal static void AddRunStarted(int seconds) => Write(MarkerKind.RunStarted, (uint)seconds);

    // A fault can change the mode from a worker thread, so this waits for the next frame like a save does.
    internal static void NoteDetailChanged(DetailMode mode) => Volatile.Write(ref s_detail, (int)mode + 1);

    /// <summary>True when a save ended just now, or is about to be written.</summary>
    internal static bool SaveJustEnded(long now, int seconds) =>
        Volatile.Read(ref s_saved) != 0
        || Volatile.Read(ref s_saveFailed) != 0
        || (s_lastSave != 0L && now - s_lastSave < seconds * Clock.Frequency);

    private static void WroteSave(MarkerKind kind)
    {
        s_lastSave = Clock.Now();

        // Saves run inside a frame, and it's the next frame that writes this, so that frame is the save's.
        Write(kind, FrameEngine.LastFrameUnits / 10000U);
    }

    private static void Write(MarkerKind kind, uint value)
    {
        var record = (MarkerRecord*)s_rows!.Reserve();
        record->UnitsSinceStart = FrameEngine.UnitsSinceStart(Clock.Now());
        record->Kind = (uint)kind;
        record->Value = value;
        s_rows.Commit();
    }
}
