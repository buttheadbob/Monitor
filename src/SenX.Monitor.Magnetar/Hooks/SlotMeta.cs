using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.CompilerServices;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Interfaces;
using Sandbox.ModAPI;
using SenX.Monitor.Core;
using VRage.Game.Entity;

namespace SenX.Monitor.Magnetar.Hooks;

/// <summary>
/// Fills in who a slot belongs to. This runs once per entity per period, when its slot is created,
/// and the numeric ids it stores are all the worker keeps. Game state can be odd here, such as a
/// block mid-close, so a fault stops detail recording rather than reaching the game.
/// </summary>
internal static unsafe class SlotMeta
{
    private static int s_faults;

    public static int Faults => s_faults;

    private static Exception? s_fault;

    /// <summary>The first thing that went wrong, once, for the log. This runs on workers, so it can't log for itself.</summary>
    public static Exception? TakeFault() => Interlocked.Exchange(ref s_fault, null);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Fill(EntitySlot* slot, MyEntity entity)
    {
        try
        {
            long type = entity.GetType().TypeHandle.Value.ToInt64();

            if (entity is MyCubeBlock block)
            {
                MyCubeGrid? parent = block.CubeGrid;
                long owner = block.OwnerId;

                slot->GridId = parent?.EntityId ?? 0L;
                slot->OwnerId = owner != 0L || parent == null ? owner : FirstOwner(parent);
                slot->DefinitionKey = EntityKeys.Pack(block.BlockDefinition?.Id.SubtypeId.GetHashCode() ?? 0, type);
                return;
            }

            if (entity is MyCubeGrid grid)
            {
                slot->GridId = grid.EntityId;
                slot->OwnerId = FirstOwner(grid);
            }

            slot->DefinitionKey = EntityKeys.Pack(0, type);
        }
        catch (Exception exception)
        {
            s_faults++;
            Interlocked.CompareExchange(ref s_fault, exception, null);
            DetailController.SetMode(DetailMode.Off);
        }
    }

    /// <summary>
    /// The grid's first owner, from the game's last count. <see cref="MyCubeGrid.BigOwners"/> would count again when the game
    /// has marked it stale, which rewrites the lists from whichever thread first times the grid. Like the game, a subgrid that
    /// owns nothing takes the owner of the grid it hangs from, unless only a connector or landing gear holds it there.
    /// </summary>
    internal static long FirstOwner(MyCubeGrid grid)
    {
        try
        {
            for (int depth = 0; depth < MostParents; depth++)
            {
                List<long>? owners = grid.m_ownershipManager?.BigOwners;

                if (owners != null && owners.Count > 0)
                    return owners[0];

                MyEntity? link = MyGridPhysicalHierarchy.Static.GetEntityConnectingToParent(grid);

                if (link == null || link is IMyLandingGear || link is IMyShipConnector || !(MyGridPhysicalHierarchy.Static.GetParent(grid) is { } parent))
                    return 0L;

                grid = parent;
            }
        }
        catch (Exception)
        {
            // The game can change these on another thread. A missed owner is harmless, so it never turns detail off.
        }

        return 0L;
    }

    private const int MostParents = 8;
}
