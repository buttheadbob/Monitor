using SenX.Monitor.Contracts;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class SampleRateTests
{
    private static readonly DateTime s_start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int seconds) => s_start.AddSeconds(seconds);

    [Fact]
    public void A_calm_server_keeps_one_every_two_minutes()
    {
        var rate = new SampleRate();

        Assert.True(rate.Keep(At(0)));
        Assert.False(rate.Keep(At(30)));
        Assert.False(rate.Keep(At(90)));
        Assert.True(rate.Keep(At(120)));
    }

    [Fact]
    public void Every_row_of_one_total_is_kept_together()
    {
        var rate = new SampleRate();

        Assert.True(rate.Keep(At(0)));
        Assert.True(rate.Keep(At(0)));
    }

    [Fact]
    public void Lag_keeps_one_a_minute()
    {
        var rate = new SampleRate();
        rate.Keep(At(0));

        rate.NoteSimSpeed(At(10), 0.5d);

        Assert.False(rate.Keep(At(30)));
        Assert.True(rate.Keep(At(60)));
        Assert.True(rate.Keep(At(120)));
    }

    [Fact]
    public void It_stays_at_one_a_minute_until_five_calm_minutes_have_passed()
    {
        var rate = new SampleRate();
        rate.Keep(At(0));
        rate.NoteSimSpeed(At(60), 0.5d);

        Assert.True(rate.Keep(At(240)));
        Assert.True(rate.Keep(At(300)));
        Assert.False(rate.Keep(At(360)));
        Assert.True(rate.Keep(At(420)));
    }

    [Fact]
    public void A_total_a_moment_early_doesnt_slip_a_whole_total()
    {
        var rate = new SampleRate();
        rate.Keep(At(0));

        Assert.True(rate.Keep(At(119)));
    }

    [Fact]
    public void A_total_stays_skipped_if_lag_starts_partway_through_it()
    {
        var rate = new SampleRate();
        rate.Keep(At(0));

        Assert.False(rate.Keep(At(70)));

        rate.NoteSimSpeed(At(70), 0.5d);

        Assert.False(rate.Keep(At(70)));
    }

    [Fact]
    public void The_rate_carries_on_from_where_it_was()
    {
        var first = new SampleRate();
        first.Keep(At(0));
        first.Keep(At(120));
        first.NoteSimSpeed(At(130), 0.5d);

        var second = new SampleRate();
        second.Restore(first.Last, first.Before, first.LagUntil);

        Assert.True(second.Keep(At(120)));
        Assert.Equal(At(0), second.Before);
        Assert.False(second.Keep(At(150)));
        Assert.True(second.Keep(At(180)));
    }

    [Fact]
    public void Each_kept_total_knows_the_one_before()
    {
        var rate = new SampleRate();
        rate.Keep(At(0));
        rate.Keep(At(120));

        Assert.Equal(At(0), rate.Before);
    }

    [Fact]
    public void A_total_stands_for_the_time_up_to_the_next()
    {
        Dictionary<DateTime, double> weights = SampleRate.Weights([At(0), At(60), At(120), At(240)]);

        Assert.Equal(60d, weights[At(0)]);
        Assert.Equal(60d, weights[At(60)]);
        Assert.Equal(120d, weights[At(120)]);
        Assert.Equal(120d, weights[At(240)]);
    }

    [Fact]
    public void Time_the_server_was_away_doesnt_count()
    {
        Dictionary<DateTime, double> weights = SampleRate.Weights([At(0), At(3600)]);

        Assert.Equal(120d, weights[At(0)]);
    }

    [Fact]
    public void One_total_counts_for_a_normal_step()
    {
        Assert.Equal(120d, SampleRate.Weights([At(0), At(0)])[At(0)]);
    }

    [Fact]
    public void Exactly_the_line_isnt_lag()
    {
        var rate = new SampleRate();
        rate.Keep(At(0));

        rate.NoteSimSpeed(At(10), 0.7d);

        Assert.False(rate.Keep(At(60)));
    }
}
