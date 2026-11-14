using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public sealed class CommandWordsTests
{
    [Fact]
    public void Words_Torch_found_properly_are_left_alone()
    {
        Assert.Equal(new[] { "--secs=3", "--top=5" }, CaptureArgs.WordsAfter("monitor grids", new[] { "--secs=3", "--top=5" }));
        Assert.Equal(new[] { "off" }, CaptureArgs.WordsAfter("monitor everything", new[] { "off" }));
        Assert.Empty(CaptureArgs.WordsAfter("monitor status", new string[0]));
    }

    [Fact]
    public void A_word_found_inside_the_root_gets_the_command_back_off_it()
    {
        Assert.Equal(new[] { "on" }, CaptureArgs.WordsAfter("monitor everything", new[] { "onitor", "everything", "on" }));
    }

    [Fact]
    public void A_word_found_in_the_command_name_loses_only_that()
    {
        Assert.Equal(new[] { "test" }, CaptureArgs.WordsAfter("monitor test", new[] { "test", "test" }));
        Assert.Equal(new[] { "ill" }, CaptureArgs.WordsAfter("monitor drill", new[] { "ill", "ill" }));
    }

    [Fact]
    public void A_name_that_repeats_the_command_keeps_its_own_words()
    {
        Assert.Equal(new[] { "tor", "test" }, CaptureArgs.WordsAfter("monitor test", new[] { "tor", "test", "tor", "test" }));
    }

    [Fact]
    public void A_word_merely_starting_like_the_command_is_kept()
    {
        Assert.Equal(new[] { "on" }, CaptureArgs.WordsAfter("monitor drill", new[] { "on" }));
        Assert.Equal(new[] { "testing", "rig" }, CaptureArgs.WordsAfter("monitor test", new[] { "testing", "rig" }));
    }
}
