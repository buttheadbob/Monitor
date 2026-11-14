using System;
using System.Linq.Expressions;
using System.Reflection;

namespace SenX.Monitor.Core;

/// <summary>
/// Identifies which method a delegate calls without touching <see cref="Delegate.Method"/>, which
/// allocates. Most grid callbacks share one target type, so type alone can't name them, but all
/// delegates built from one method share a code pointer. Names are resolved later, off the hot path.
/// </summary>
public static class DelegateIdentity
{
    private static Func<Delegate, IntPtr>? s_method;
    private static Func<Delegate, IntPtr>? s_methodAux;

    /// <summary>True when this runtime exposes the fields. If false, callers fall back to target type.</summary>
    public static bool Available => s_method != null && s_methodAux != null;

    internal static void Initialize()
    {
        if (Available)
            return;

        s_method = BindField("_methodPtr");
        s_methodAux = BindField("_methodPtrAux");
    }

    /// <summary>
    /// A stable key for the method behind a delegate. Open static delegates keep the target in the
    /// aux field and a shuffle thunk in the main one, so aux wins when it is set.
    /// </summary>
    public static long Key(Delegate callback)
    {
        long aux = s_methodAux!(callback).ToInt64();
        return aux != 0L ? aux : s_method!(callback).ToInt64();
    }

    private static Func<Delegate, IntPtr>? BindField(string name)
    {
        FieldInfo? field = typeof(Delegate).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || field.FieldType != typeof(IntPtr))
            return null;

        ParameterExpression target = Expression.Parameter(typeof(Delegate), "target");
        return Expression.Lambda<Func<Delegate, IntPtr>>(Expression.Field(target, field), target).Compile();
    }
}
