using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Cube;
using SenX.Monitor.Core;
using VRage.Game;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>
/// Finds block classes by part of their name, for <c>--block</c>. Definitions say which classes can
/// exist, so this never walks the world. Game thread, once per command.
/// </summary>
internal static class BlockClasses
{
    /// <summary>The sorted class keys of every block class whose name contains the text.</summary>
    public static long[] Matching(string text)
    {
        var keys = new HashSet<long>();

        foreach (MyDefinitionBase definition in MyDefinitionManager.Static.GetAllDefinitions())
        {
            if (!(definition is MyCubeBlockDefinition block))
                continue;

            Type? type = MyCubeBlockFactory.GetProducedType(block.Id.TypeId);

            if (type != null && type.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                keys.Add(EntityKeys.TypeOf(EntityKeys.Pack(0, type.TypeHandle.Value.ToInt64())));
        }

        var sorted = new long[keys.Count];
        keys.CopyTo(sorted);
        Array.Sort(sorted);
        return sorted;
    }
}
