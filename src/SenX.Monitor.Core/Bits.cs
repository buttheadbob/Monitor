using System.Runtime.CompilerServices;

namespace SenX.Monitor.Core;

/// <summary>Integer helpers. netstandard2.0 has no BitOperations.</summary>
public static class Bits
{
    private static readonly byte[] DeBruijn64 =
    {
        63, 0, 58, 1, 59, 47, 53, 2, 60, 39, 48, 27, 54, 33, 42, 3,
        61, 51, 37, 40, 49, 18, 28, 20, 55, 30, 34, 11, 43, 14, 22, 4,
        62, 57, 46, 52, 38, 26, 32, 41, 50, 36, 17, 19, 29, 10, 13, 21,
        56, 45, 25, 31, 35, 16, 9, 12, 44, 24, 15, 8, 23, 7, 6, 5,
    };

    /// <summary>Floor of log2. Returns 0 for 0 and 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Log2(ulong value)
    {
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        value |= value >> 32;
        return DeBruijn64[((value - (value >> 1)) * 0x07EDD5E59A4E28C2UL) >> 58];
    }

    public static bool IsPowerOfTwo(long value) => value > 0 && (value & (value - 1)) == 0;
}
