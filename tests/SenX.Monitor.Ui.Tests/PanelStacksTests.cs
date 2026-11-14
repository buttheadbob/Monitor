using SenX.Monitor.Contracts;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class PanelStacksTests
{
    private static List<List<string>> Split(params (string Title, int Rows, string? Group)[] items) =>
        PanelStacks.Split(items, item => item.Rows, item => item.Group).Select(stack => stack.Select(item => item.Title).ToList()).ToList();

    [Fact]
    public void Mods_sit_above_entity_types_in_one_stack()
    {
        // The lists from the run that overlapped in the live test.
        List<List<string>> stacks = Split(("Session components", 17, null), ("Game logic", 2, null), ("Mods and plugins", 7, "mods-entities"), ("Entity types", 3, "mods-entities"));

        List<string> stack = Assert.Single(stacks, s => s.Contains("Mods and plugins"));
        Assert.Equal(stack.IndexOf("Mods and plugins") + 1, stack.IndexOf("Entity types"));
    }

    [Fact]
    public void There_are_always_two_stacks_and_each_list_is_in_one()
    {
        List<List<string>> stacks = Split(("A", 5, null), ("B", 5, null), ("C", 5, null));

        Assert.Equal(PanelStacks.Count, stacks.Count);
        Assert.Equal(["A", "B", "C"], stacks.SelectMany(stack => stack).Order());
    }

    [Fact]
    public void Each_next_list_goes_on_the_shorter_stack_and_keeps_its_order()
    {
        List<List<string>> stacks = Split(("Tall", 20, null), ("Short 1", 2, null), ("Short 2", 2, null), ("Short 3", 2, null));

        Assert.Equal(["Tall"], stacks[0]);
        Assert.Equal(["Short 1", "Short 2", "Short 3"], stacks[1]);
    }

    [Fact]
    public void No_lists_means_two_empty_stacks()
    {
        Assert.All(Split(), stack => Assert.Empty(stack));
    }
}
