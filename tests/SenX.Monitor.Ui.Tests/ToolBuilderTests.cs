using SenX.Monitor.Api;
using SenX.Monitor.Contracts;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class ToolBuilderTests
{
    private static MonitorRow Row(string name, long? id, double ms) => new(name, id, ms, 0d, 0d, 0d, 1d);

    private static MonitorReply Reply(params MonitorRow[] rows) => new()
    {
        State = MonitorReplyState.Finished,
        Lists = [new MonitorListReply { List = MonitorList.Grids, Available = true, Rows = rows }],
    };

    [Fact]
    public void Comparing_joins_rows_on_their_ids()
    {
        MonitorComparison comparison = ToolBuilder.Compare(
            Reply(Row("Ship", 1L, 1d), Row("Ship", 2L, 5d)),
            Reply(Row("Ship", 2L, 7d)));

        IReadOnlyList<MonitorComparedRow> rows = Assert.Single(comparison.Lists).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal(2L, rows[0].Id);
        Assert.Equal(2d, rows[0].ChangeMs);
        Assert.Null(rows[1].AfterMs);
    }

    [Fact]
    public void Comparing_joins_rows_with_no_id_on_their_names()
    {
        MonitorComparison comparison = ToolBuilder.Compare(Reply(Row("A mod", null, 1d)), Reply(Row("A mod", null, 3d)));

        MonitorComparedRow row = Assert.Single(Assert.Single(comparison.Lists).Rows);

        Assert.Null(row.Id);
        Assert.Equal(2d, row.ChangeMs);
    }

    [Fact]
    public void Time_left_is_never_negative()
    {
        DateTime now = DateTime.UtcNow;

        Assert.Equal(TimeSpan.FromSeconds(9), ToolBuilder.RetryAfter(now.AddSeconds(9), now));
        Assert.True(ToolBuilder.RetryAfter(now.AddSeconds(-9), now) > TimeSpan.Zero);
        Assert.True(ToolBuilder.RetryAfter(default, now) > TimeSpan.Zero);
    }

    [Fact]
    public void A_result_from_someone_elses_drill_down_isnt_passed_off_as_yours()
    {
        var result = new DrillResultResponse { State = RunState.Finished, Number = 2L, Method = "Theirs" };

        Assert.Equal(MonitorReplyState.Finished, ToolBuilder.ToInside(result, 2L).State);
        Assert.Equal(MonitorReplyState.Failed, ToolBuilder.ToInside(result, 1L).State);
    }

    [Fact]
    public void A_result_from_someone_elses_audit_isnt_passed_off_as_yours()
    {
        var result = new AuditResultResponse { State = RunState.Finished, Number = 2L };

        Assert.Equal(MonitorReplyState.Finished, ToolBuilder.ToAudit(result, 2L).State);
        Assert.Equal(MonitorReplyState.Failed, ToolBuilder.ToAudit(result, 1L).State);
    }

    [Fact]
    public async Task Pace_lets_the_first_through_and_makes_the_next_wait()
    {
        var pace = new Pace(TimeSpan.FromSeconds(30));

        Assert.True(pace.WaitAsync(CancellationToken.None).IsCompleted);

        using var cancel = new CancellationTokenSource();
        Task next = pace.WaitAsync(cancel.Token);

        Assert.False(next.IsCompleted);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }
}
