using System.Collections.Generic;

namespace SenX.Monitor.Contracts;

/// <summary>One part of the frame, and what it sits inside. Zero means it's at the top.</summary>
public readonly struct TreeNode
{
    public TreeNode(int id, int parent, string name)
    {
        Id = id;
        Parent = parent;
        Name = name;
    }

    public int Id { get; }

    public int Parent { get; }

    public string Name { get; }
}

/// <summary>
/// The shape of the tree, for Quasar, which doesn't reference the worker's core. Ids and order match
/// the worker's scope table, and a test checks that.
/// </summary>
public static class TreeShape
{
    public const int Frame = 1;

    /// <summary>The game thread waiting in the scheduler for tasks on worker threads.</summary>
    public const int TaskWait = 13;

    /// <summary>Physics stepping, where the game thread waits for the physics engine's own threads.</summary>
    public const int PhysicsStep = 23;

    /// <summary>The parallel entity updates, where the game thread hands out work and waits for workers.</summary>
    public const int BeforeParallel = 28;

    public const int AfterParallel = 34;

    /// <summary>
    /// Which threads a part's time is on. Every part is game thread time, because that's what the tree measures,
    /// but in a few the game thread spends some of it waiting for other threads.
    /// </summary>
    public static string ThreadOf(int id) => id switch
    {
        TaskWait or BeforeParallel or AfterParallel => "Main, waits on workers",
        PhysicsStep => "Main, waits on physics threads",
        _ => "Main",
    };

    public static readonly TreeNode[] All =
    {
        new(1, 0, "Frame"),
        new(2, 1, "Update"),
        new(3, 2, "Invoke queue"),
        new(4, 2, "Network"),
        new(5, 4, "Transport"),
        new(6, 4, "Game service"),
        new(7, 4, "Monitor"),
        new(8, 4, "Read"),
        new(9, 4, "Report objects"),
        new(10, 2, "Plugins"),
        new(11, 2, "Session"),
        new(12, 11, "Client states"),
        new(13, 11, "Task wait"),
        new(14, 11, "Parallel callbacks"),
        new(15, 11, "Replication before"),
        new(16, 11, "Components"),
        new(17, 11, "GPS"),
        new(18, 11, "Multiplayer tick"),
        new(19, 18, "Replication send"),
        new(20, 11, "Block limits"),
        new(21, 16, "Physics"),
        new(22, 21, "Raycasts"),
        new(23, 21, "Step"),
        new(24, 21, "Rigid bodies"),
        new(25, 21, "Characters"),
        new(26, 21, "Cluster space"),
        new(27, 16, "Entities before"),
        new(28, 27, "Parallel"),
        new(29, 27, "Invoke later"),
        new(30, 27, "Serial"),
        new(31, 27, "Every 10"),
        new(32, 27, "Every 100"),
        new(33, 16, "Entities after"),
        new(34, 33, "Parallel"),
        new(35, 33, "Invoke later"),
        new(36, 33, "Serial"),
        new(37, 33, "Every 10"),
        new(38, 33, "Every 100"),
        new(39, 33, "Cleanup"),
        new(40, 16, "Entities simulate"),
        new(41, 16, "Entities once"),
        new(42, 0, "Monitor"),
    };

    /// <summary>What sits directly inside a part.</summary>
    public static IEnumerable<TreeNode> ChildrenOf(int id)
    {
        foreach (TreeNode node in All)
        {
            if (node.Parent == id)
                yield return node;
        }
    }
}
