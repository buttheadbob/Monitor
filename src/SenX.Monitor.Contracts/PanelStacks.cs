using System;
using System.Collections.Generic;

namespace SenX.Monitor.Contracts;

/// <summary>
/// Splits a page's lists into two stacks of panels, so a wide page shows two columns and a narrow one shows them one
/// above the other. Lists keep their order inside a stack. Lists that share a group always go together, one above the
/// other, such as mods above entity types. Each next list or group goes on the shorter stack, judged by rows.
/// </summary>
public static class PanelStacks
{
    /// <summary>The two stacks Quasar and Torch show, left first.</summary>
    public const int Count = 2;

    /// <summary>Roughly what a panel's heading and table head take, in rows.</summary>
    private const int HeadingRows = 3;

    public static List<List<T>> Split<T>(IReadOnlyList<T> items, Func<T, int> rows, Func<T, string?> group)
    {
        var stacks = new List<List<T>>(Count);
        var heights = new int[Count];

        for (int i = 0; i < Count; i++)
            stacks.Add([]);

        for (int at = 0; at < items.Count;)
        {
            // A group is the run of neighbouring items that share it.
            string? name = group(items[at]);
            int end = at + 1;

            while (name != null && end < items.Count && group(items[end]) == name)
                end++;

            int height = 0;
            for (int i = at; i < end; i++)
                height += rows(items[i]) + HeadingRows;

            int shorter = heights[1] < heights[0] ? 1 : 0;

            for (int i = at; i < end; i++)
                stacks[shorter].Add(items[i]);

            heights[shorter] += height;
            at = end;
        }

        return stacks;
    }
}
