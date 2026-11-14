using SenX.Monitor.Contracts;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class RecentNamesTests
{
    [Fact]
    public void A_noted_name_can_be_found()
    {
        var names = new RecentNames(4);

        names.Note(10L, "Big Ship");

        Assert.Equal("Big Ship", names.Find(10L));
    }

    [Fact]
    public void Nothing_is_found_for_a_key_never_noted()
    {
        Assert.Null(new RecentNames(4).Find(10L));
    }

    [Fact]
    public void The_oldest_goes_first_once_it_is_full()
    {
        var names = new RecentNames(2);

        names.Note(1L, "one");
        names.Note(2L, "two");
        names.Note(3L, "three");

        Assert.Null(names.Find(1L));
        Assert.Equal("two", names.Find(2L));
        Assert.Equal("three", names.Find(3L));
    }

    [Fact]
    public void Noting_a_key_again_changes_its_name_and_takes_no_extra_room()
    {
        var names = new RecentNames(2);

        names.Note(1L, "one");
        names.Note(1L, "renamed");
        names.Note(2L, "two");

        Assert.Equal("renamed", names.Find(1L));
        Assert.Equal("two", names.Find(2L));
    }

    [Fact]
    public void It_keeps_working_after_going_round_many_times()
    {
        var names = new RecentNames(3);

        for (long key = 1L; key <= 100L; key++)
            names.Note(key, key.ToString());

        Assert.Null(names.Find(97L));
        Assert.Equal("98", names.Find(98L));
        Assert.Equal("100", names.Find(100L));
    }
}
