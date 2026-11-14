using System.Runtime.CompilerServices;

namespace SenX.Monitor.Core;

/// <summary>
/// Decides which calls are timed at a site too busy to time fully.
/// <para>
/// The test hashes the id with a salt that changes every frame, so each update is an independent
/// 1 in 2^k chance. Never rotate on the frame number instead: entities updating every 10 or 100
/// frames alias against a power of two, and most of them would never be sampled at all.
/// </para>
/// </summary>
public static class Sampler
{
    private const ulong HashMultiplier = 0x9E3779B97F4A7C15UL;
    private const ulong ScopeMultiplier = 0xC2B2AE3D27D4EB4FUL;

    private static ulong s_frameSalt;
    private static ulong s_sequence;

    /// <summary>The current frame salt. Racy reads from workers are harmless.</summary>
    public static ulong FrameSalt => s_frameSalt;

    /// <summary>
    /// True when this call should be timed. <paramref name="scope"/> decorrelates nested scopes,
    /// so an outer scope does not always pay for its inner one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Hit(long id, int scope, int shift)
    {
        ulong hash = ((ulong)id ^ s_frameSalt ^ ((ulong)scope * ScopeMultiplier)) * HashMultiplier;

        // Two shifts because C# masks the count: a single shift by 64 would be a shift by 0.
        return ((hash >> 1) >> (63 - shift)) == 0UL;
    }

    /// <summary>Advances the salt. Call once per frame from the game thread (splitmix64).</summary>
    public static void NextFrame()
    {
        ulong z = s_sequence += HashMultiplier;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        s_frameSalt = z ^ (z >> 31);
    }
}
