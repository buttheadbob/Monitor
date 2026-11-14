using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class SlowestCallsTests
{
    public SlowestCallsTests() => TestSetup.Reset();

    private static void Slow(long id, int milliseconds) =>
        Recorder.AddEntity(Site.EntityBefore, Phase.Before, id, Clock.Frequency * milliseconds / 1000L, 0);

    [Fact]
    public void The_slowest_comes_first()
    {
        Slow(1L, 2);
        Slow(2L, 9);
        Slow(3L, 5);

        var calls = new SlowestCalls();
        calls.Collect(0UL);

        Assert.Equal(3, calls.Count);
        Assert.Equal(2L, calls[0].Id);
        Assert.Equal(3L, calls[1].Id);
        Assert.Equal(1L, calls[2].Id);
    }

    [Fact]
    public void Calls_keep_their_site_and_thread()
    {
        Slow(1L, 2);

        var calls = new SlowestCalls();
        calls.Collect(0UL);

        Assert.Equal(Site.EntityBefore, calls[0].Site);
        Assert.True(calls[0].Main);
    }

    [Fact]
    public void A_fast_call_isnt_kept()
    {
        Recorder.AddEntity(Site.EntityBefore, Phase.Before, 1L, 1L, 0);

        var calls = new SlowestCalls();
        calls.Collect(0UL);

        Assert.Equal(0, calls.Count);
    }

    [Fact]
    public void Calls_from_before_the_frame_asked_for_are_left_out()
    {
        Recorder.Frame = 10UL;
        Slow(1L, 2);
        Recorder.Frame = 20UL;
        Slow(2L, 2);

        var calls = new SlowestCalls();
        calls.Collect(15UL);

        Assert.Equal(1, calls.Count);
        Assert.Equal(2L, calls[0].Id);
    }

    [Fact]
    public void It_never_holds_more_than_it_can()
    {
        for (long id = 1L; id <= 16L; id++)
            Slow(id, 2);

        var calls = new SlowestCalls();

        for (int i = 0; i < 6; i++)
            calls.Collect(0UL);

        Assert.Equal(SlowestCalls.Capacity, calls.Count);
    }

    [Fact]
    public void Clearing_empties_it()
    {
        Slow(1L, 2);

        var calls = new SlowestCalls();
        calls.Collect(0UL);
        calls.Clear();

        Assert.Equal(0, calls.Count);
    }

    [Theory]
    [InlineData(Site.GridCallback, true)]
    [InlineData(Site.SessionBefore, true)]
    [InlineData(Site.ParallelBefore, true)]
    [InlineData(Site.EntityBefore, false)]
    [InlineData(Site.Script, false)]
    public void Keyed_sites_are_known(int site, bool keyed)
    {
        Assert.Equal(keyed, SlowestCalls.IsKeyed(site));
    }

    [Fact]
    public void Collecting_allocates_nothing()
    {
        for (long id = 1L; id <= 16L; id++)
            Slow(id, 2);

        var calls = new SlowestCalls();

        long bytes = Allocation.Measure(() =>
        {
            calls.Clear();
            calls.Collect(0UL);
        });

        Assert.Equal(0L, bytes);
    }
}
