using System.Reflection;
using System.Reflection.Emit;

namespace SenX.Monitor.Magnetar.Patching;

/// <summary>Which calls a table entry means, and whether a hook can stand in for one. Both rewriters use these.</summary>
internal static class CallMatch
{
    public static bool Matches(OpCode opCode, object? operand, CallTarget target)
    {
        if (opCode != OpCodes.Call && opCode != OpCodes.Callvirt)
            return false;

        return operand is MethodInfo method &&
               method.Name == target.Method &&
               method.DeclaringType?.FullName == target.Type &&
               method.GetParameters().Length == target.Parameters &&
               (target.SecondParameter == null || method.GetParameters()[1].ParameterType.Name == target.SecondParameter);
    }

    /// <summary>True when the hook takes exactly what the call leaves on the stack and returns the same.</summary>
    public static bool Fits(MethodInfo hook, MethodInfo call)
    {
        if (hook.ReturnType != call.ReturnType)
            return false;

        ParameterInfo[] hookParameters = hook.GetParameters();
        ParameterInfo[] callParameters = call.GetParameters();
        int receiver = call.IsStatic ? 0 : 1;

        if (hookParameters.Length != callParameters.Length + receiver)
            return false;

        if (receiver == 1 && !hookParameters[0].ParameterType.IsAssignableFrom(call.DeclaringType))
            return false;

        for (int i = 0; i < callParameters.Length; i++)
        {
            if (hookParameters[i + receiver].ParameterType != callParameters[i].ParameterType)
                return false;
        }

        return true;
    }
}
