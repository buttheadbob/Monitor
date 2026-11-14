using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>One instruction, in no patching library's terms, so the table works on Harmony and on Torch.</summary>
internal readonly struct IlStep
{
    public IlStep(OpCode opCode, object? operand = null)
    {
        OpCode = opCode;
        Operand = operand;
    }

    public OpCode OpCode { get; }

    public object? Operand { get; }
}

internal enum CallSiteKind
{
    /// <summary>Always-on tree scope. The call stays, with a clock read before it and a record after.</summary>
    Bracket,

    /// <summary>The frame itself. Like a bracket, but the exit commits the frame.</summary>
    FrameEnd,

    /// <summary>Gated detail site. The call is swapped for a hook, one instruction for one.</summary>
    Substitute,

    /// <summary>Swapped like a detail site, for a hook that always counts. There's no gate, so it's never opened or closed.</summary>
    Counter,

    /// <summary>
    /// The call stays exactly as it is, and a hook runs just before it. What's added loads the hook's
    /// arguments and the hook takes them, so the stack ends as it started. It needs no access to the call.
    /// </summary>
    Tally,
}

/// <summary>One call inside a caller that Monitor wants to measure.</summary>
internal sealed class CallTarget
{
    public CallTarget(string type, string method, int parameters, int expected, CallSiteKind kind, int scope, MethodInfo? hook, string? secondParameter = null, IlStep[]? loads = null)
    {
        SecondParameter = secondParameter;
        Loads = loads;
        Type = type;
        Method = method;
        Parameters = parameters;
        Expected = expected;
        Kind = kind;
        Scope = scope;
        Hook = hook;
    }

    public string Type { get; }

    public string Method { get; }

    public int Parameters { get; }

    /// <summary>Call sites expected in the caller. A different count means the game changed here.</summary>
    public int Expected { get; }

    public CallSiteKind Kind { get; }

    public int Scope { get; }

    public MethodInfo? Hook { get; }

    /// <summary>The type name of the second parameter, to tell overloads with the same count apart.</summary>
    public string? SecondParameter { get; }

    /// <summary>For a tally, what loads the hook's arguments.</summary>
    public IlStep[]? Loads { get; }

    public string Name => Type + "::" + Method + (SecondParameter == null ? string.Empty : "(" + SecondParameter + ")");
}

/// <summary>A game method to rewrite, and the calls inside it to measure.</summary>
internal sealed class CallerSpec
{
    public CallerSpec(string id, MethodBase? method, IReadOnlyList<CallTarget> targets)
    {
        Id = id;
        Method = method;
        Targets = targets;
    }

    /// <summary>Stable identity, used for fingerprints and reporting.</summary>
    public string Id { get; }

    /// <summary>Null when the method could not be found in this game build.</summary>
    public MethodBase? Method { get; }

    public IReadOnlyList<CallTarget> Targets { get; }
}
