using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using SenX.Monitor.Core;

namespace SenX.Monitor.Magnetar.Hooks;

/// <summary>
/// Records time against a type, a delegate or a method rather than an entity. Keys pack the site into the low
/// bits, so one component type gets separate rows for Before and After. A key's meaning is noted
/// once, the first time it is seen on a thread, so the normal path touches no managed collection.
/// </summary>
internal static class KeyedRecord
{
    public static void AddType(int site, Type type, long rawTicks, int shift)
    {
        long value = type.TypeHandle.Value.ToInt64();
        if (Recorder.AddKey(site, SiteKeys.Pack(value, site), rawTicks, shift))
            KeyNames.Note(value, type);
    }

    public static void AddDelegate(int site, Action callback, long rawTicks, int shift)
    {
        long value = DelegateIdentity.Key(callback);
        if (Recorder.AddKey(site, SiteKeys.Pack(value, site), rawTicks, shift))
            KeyNames.NoteDelegate(value, callback);
    }

    public static void AddMethod(int site, MethodInfo method, long rawTicks, int shift)
    {
        long value = method.MethodHandle.Value.ToInt64();
        if (Recorder.AddKey(site, SiteKeys.Pack(value, site), rawTicks, shift))
            KeyNames.Note(value, method);
    }
}

/// <summary>
/// What keys mean, resolved off the hot path. Holds a bounded number of references, and only to types
/// and methods, because a delegate would keep whatever it's bound to alive, which can be a whole grid.
/// </summary>
internal static class KeyNames
{
    private const int Limit = 8192;

    private static readonly object s_gate = new();
    private static readonly Dictionary<long, object> s_keys = new();

    public static int Count
    {
        get
        {
            lock (s_gate)
                return s_keys.Count;
        }
    }

    public static void Note(long value, object meaning)
    {
        lock (s_gate)
        {
            if (s_keys.Count < Limit && !s_keys.ContainsKey(value))
                s_keys[value] = meaning;
        }
    }

    // Reading a delegate's method allocates, so it only happens the first time this method is seen.
    public static void NoteDelegate(long value, Delegate callback)
    {
        lock (s_gate)
        {
            if (s_keys.Count < Limit && !s_keys.ContainsKey(value))
                s_keys[value] = callback.Method;
        }
    }

    public static bool Knows(long value)
    {
        lock (s_gate)
            return s_keys.ContainsKey(value);
    }

    /// <summary>Everything noted so far, for finding methods to look inside. Report path only.</summary>
    public static List<object> Meanings()
    {
        lock (s_gate)
            return new List<object>(s_keys.Values);
    }

    /// <summary>The type behind a key, which says what mod or plugin owns the code. Report path only.</summary>
    public static Type? TypeOf(long value)
    {
        object? meaning;
        lock (s_gate)
            s_keys.TryGetValue(value, out meaning);

        switch (meaning)
        {
            case MethodBase method:
                return method.DeclaringType;
            default:
                return meaning as Type;
        }
    }

    /// <summary>A readable name for a key. Runs on the report path only.</summary>
    public static string Describe(long value)
    {
        object? meaning;
        lock (s_gate)
            s_keys.TryGetValue(value, out meaning);

        switch (meaning)
        {
            case Type type:
                return type.FullName ?? type.Name;
            case MethodBase method:
                return (method.DeclaringType?.Name ?? "?") + "." + method.Name;
            default:
                return value.ToString("x", CultureInfo.InvariantCulture);
        }
    }
}
