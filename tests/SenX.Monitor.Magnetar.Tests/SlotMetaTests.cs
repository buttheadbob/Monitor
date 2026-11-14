using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox.Game.Entities;
using SenX.Monitor.Magnetar.Hooks;
using Xunit;

namespace SenX.Monitor.Magnetar.Tests;

public sealed class SlotMetaTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Type s_managerType = typeof(MyCubeGrid).Assembly.GetType("Sandbox.Game.Entities.Cube.MyCubeGridOwnershipManager", throwOnError: true)!;

    private static MyCubeGrid GridOwnedBy(List<long>? owners, bool stale)
    {
        var grid = (MyCubeGrid)Blank(typeof(MyCubeGrid));

        if (owners == null)
            return grid;

        object manager = Blank(s_managerType);
        s_managerType.GetField("BigOwners", Any)!.SetValue(manager, owners);
        // The field, because the game's setter does more than store it.
        s_managerType.GetField("m_needRecalculateOwners", Any)!.SetValue(manager, stale);
        typeof(MyCubeGrid).GetField("m_ownershipManager", Any)!.SetValue(grid, manager);
        return grid;
    }

    private static bool Stale(MyCubeGrid grid) =>
        (bool)s_managerType.GetField("m_needRecalculateOwners", Any)!.GetValue(typeof(MyCubeGrid).GetField("m_ownershipManager", Any)!.GetValue(grid))!;

    // Built without running the game's constructors, which need a running game.
#if NETFRAMEWORK
    private static object Blank(Type type) => System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
#else
    private static object Blank(Type type) => RuntimeHelpers.GetUninitializedObject(type);
#endif

    [Fact]
    public void The_owner_comes_from_the_games_last_count()
    {
        Assert.Equal(42L, SlotMeta.FirstOwner(GridOwnedBy(new List<long> { 42L, 7L }, stale: false)));
    }

    [Fact]
    public void A_stale_count_is_read_as_it_is_and_left_for_the_game_to_redo()
    {
        MyCubeGrid grid = GridOwnedBy(new List<long> { 42L }, stale: true);

        Assert.Equal(42L, SlotMeta.FirstOwner(grid));
        Assert.True(Stale(grid));
    }

    [Fact]
    public void Anything_going_wrong_is_no_owner_and_never_throws()
    {
        // With nothing owned the grid's parent is looked up, and outside a game there's no hierarchy to ask.
        Assert.Equal(0L, SlotMeta.FirstOwner(GridOwnedBy(new List<long>(), stale: false)));
        Assert.Equal(0L, SlotMeta.FirstOwner(GridOwnedBy(null, stale: false)));
    }
}
