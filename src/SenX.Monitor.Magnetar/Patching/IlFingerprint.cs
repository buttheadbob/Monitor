using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>
/// A hash of a method's original IL. It runs on original instructions, so another plugin's patches
/// can't change it, and it includes the members a method calls, so swapping one call for another
/// does change it. Names carry no assembly identity and floating point goes in as its bits, so .NET
/// Framework and .NET 10 agree. They print 1f/60f differently, so a number can't go in as text.
/// </summary>
internal static partial class IlFingerprint
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    // What every instruction's operand adds, once a reader has dealt with labels and locals.
    private static ulong MixMember(ulong hash, object? operand)
    {
        switch (operand)
        {
            case null:
                return hash;
            case string text:
                return Mix(hash, text);
            case MethodBase method:
                return Mix(hash, Describe(method));
            case FieldInfo field:
                return Mix(hash, Describe(field.DeclaringType) + "::" + field.Name);
            case Type type:
                return Mix(hash, Describe(type));
            case float number:
                return Mix(hash, BitConverter.ToInt32(BitConverter.GetBytes(number), 0).ToString(CultureInfo.InvariantCulture));
            case double number:
                return Mix(hash, BitConverter.DoubleToInt64Bits(number).ToString(CultureInfo.InvariantCulture));
            case IFormattable number:
                return Mix(hash, number.ToString(null, CultureInfo.InvariantCulture));
            default:
                return Mix(hash, operand.GetType().Name);
        }
    }

    /// <summary>A stable description of a method: declaring type, name and parameter types.</summary>
    public static string Describe(MethodBase method)
    {
        var text = new StringBuilder();
        text.Append(Describe(method.DeclaringType)).Append("::").Append(method.Name).Append('(');

        ParameterInfo[] parameters = method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
                text.Append(',');
            text.Append(Describe(parameters[i].ParameterType));
        }

        return text.Append(')').ToString();
    }

    private static string Describe(Type? type)
    {
        if (type == null)
            return "?";

        if (type.HasElementType)
            return Describe(type.GetElementType()) + (type.IsByRef ? "&" : type.IsPointer ? "*" : "[]");

        if (type.IsGenericParameter)
            return type.Name;

        if (!type.IsGenericType)
            return type.FullName ?? type.Name;

        // Type.FullName would embed assembly names here, and those differ between runtimes.
        var text = new StringBuilder();
        Type definition = type.GetGenericTypeDefinition();
        text.Append(definition.FullName ?? definition.Name).Append('<');

        Type[] arguments = type.GetGenericArguments();
        for (int i = 0; i < arguments.Length; i++)
        {
            if (i > 0)
                text.Append(',');
            text.Append(Describe(arguments[i]));
        }

        return text.Append('>').ToString();
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= Prime;
        }

        return hash;
    }

    private static ulong Mix(ulong hash, string text)
    {
        foreach (char c in text)
        {
            hash ^= (byte)c;
            hash *= Prime;
            hash ^= (byte)(c >> 8);
            hash *= Prime;
        }

        return hash;
    }
}
