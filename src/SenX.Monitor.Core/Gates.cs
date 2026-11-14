using System.Runtime.CompilerServices;

namespace SenX.Monitor.Core;

/// <summary>
/// One byte per detail site. <see cref="Closed"/> means the site costs a byte read and a branch.
/// Any other value is the sample shift k for that site, so a site times 1 in 2^k calls and k=0
/// times every call. Written by the controller once a second, read by every hook.
/// </summary>
public static unsafe class Gates
{
    /// <summary>Number of addressable sites and scopes.</summary>
    public const int Capacity = 1024;

    public const byte Closed = 0xFF;

    public const int MaxShift = 10;

    /// <summary>
    /// The gate table. A plain static rather than readonly with an initializer: there is no static
    /// constructor, so hot code never carries a class-init check. Set before any hook is patched in.
    /// </summary>
    public static byte* Ptr;

    internal static void Initialize()
    {
        if (Ptr != null)
            return;

        byte* table = Native.Allocate<byte>(Capacity);
        for (int i = 0; i < Capacity; i++)
            table[i] = Closed;

        Ptr = table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Read(int site) => Ptr[site];

    public static void Open(int site, int shift) =>
        Ptr[site] = (byte)(shift < 0 ? 0 : shift > MaxShift ? MaxShift : shift);

    public static void Close(int site) => Ptr[site] = Closed;

    public static void CloseAll()
    {
        for (int i = 0; i < Capacity; i++)
            Ptr[i] = Closed;
    }
}
