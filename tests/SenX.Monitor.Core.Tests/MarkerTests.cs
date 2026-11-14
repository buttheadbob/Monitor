using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class MarkerTests
{
    private readonly byte[] _buffer = new byte[HistoryWriter.MaxBytes];

    public MarkerTests()
    {
        TestSetup.Reset();
        FrameEngine.Initialize();
        FrameEngine.Start();

        // Anything an earlier test left noted.
        Markers.Flush();
    }

    private HistoryBatch Pull(long fromMarkers)
    {
        fixed (byte* output = _buffer)
        {
            int length = HistoryWriter.Write(FrameEngine.Rollups.Head, Census.Rows.Head, 0L, output, _buffer.Length, Sweep.Rows.Head, fromMarkers);
            HistoryBatch? batch = HistoryReader.Read(_buffer.Take(length).ToArray());

            Assert.NotNull(batch);
            return batch!;
        }
    }

    [Fact]
    public void A_record_is_whole_columns_with_no_padding()
    {
        Assert.Equal(16, sizeof(MarkerRecord));
        Assert.Equal(0, (int)Marshal.OffsetOf<MarkerRecord>(nameof(MarkerRecord.UnitsSinceStart)));
        Assert.Equal(8, (int)Marshal.OffsetOf<MarkerRecord>(nameof(MarkerRecord.Kind)));
    }

    [Fact]
    public void Kinds_match_the_readers()
    {
        Assert.Equal((uint)MarkerKind.Saved, (uint)HistoryMarkerKind.Saved);
        Assert.Equal((uint)MarkerKind.SaveFailed, (uint)HistoryMarkerKind.SaveFailed);
        Assert.Equal((uint)MarkerKind.LagTripped, (uint)HistoryMarkerKind.LagTripped);
        Assert.Equal((uint)MarkerKind.RunStarted, (uint)HistoryMarkerKind.RunStarted);
        Assert.Equal((uint)MarkerKind.RunFinished, (uint)HistoryMarkerKind.RunFinished);
        Assert.Equal((uint)MarkerKind.DetailChanged, (uint)HistoryMarkerKind.DetailChanged);
    }

    [Fact]
    public void A_save_reaches_the_reader_with_its_kind_and_time()
    {
        long head = Markers.Rows.Head;
        long before = FrameEngine.UnitsSinceStart(Clock.Now());

        Markers.NoteSave(succeeded: true);
        Markers.Flush();

        long after = FrameEngine.UnitsSinceStart(Clock.Now());
        HistoryMarker marker = Assert.Single(Pull(head).Markers);

        Assert.Equal(head, marker.Sequence);
        Assert.Equal(HistoryMarkerKind.Saved, marker.Kind);
        Assert.InRange(marker.UnitsSinceStart, before, after);
    }

    [Fact]
    public void A_failed_save_is_told_apart()
    {
        long head = Markers.Rows.Head;

        Markers.NoteSave(succeeded: false);
        Markers.Flush();

        Assert.Equal(HistoryMarkerKind.SaveFailed, Assert.Single(Pull(head).Markers).Kind);
    }

    [Fact]
    public void Nothing_is_written_until_the_frame_flushes()
    {
        long head = Markers.Rows.Head;

        Markers.NoteSave(succeeded: true);
        Assert.Equal(head, Markers.Rows.Head);

        Markers.Flush();
        Assert.Equal(head + 1L, Markers.Rows.Head);
    }

    [Fact]
    public void A_frame_with_nothing_noted_writes_nothing()
    {
        long head = Markers.Rows.Head;

        Markers.Flush();
        Markers.Flush();

        Assert.Equal(head, Markers.Rows.Head);
    }

    [Fact]
    public void A_save_noted_on_another_thread_is_written_by_the_game_thread()
    {
        long head = Markers.Rows.Head;

        var thread = new Thread(() => Markers.NoteSave(succeeded: true));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(head, Markers.Rows.Head);

        Markers.Flush();
        Assert.Equal(head + 1L, Markers.Rows.Head);
    }

    [Fact]
    public void Saves_between_frames_make_one_line()
    {
        long head = Markers.Rows.Head;

        Markers.NoteSave(succeeded: true);
        Markers.NoteSave(succeeded: true);
        Markers.Flush();

        Assert.Equal(head + 1L, Markers.Rows.Head);
    }

    [Fact]
    public void A_save_and_a_failure_in_one_frame_are_both_kept()
    {
        long head = Markers.Rows.Head;

        Markers.NoteSave(succeeded: true);
        Markers.NoteSave(succeeded: false);
        Markers.Flush();

        Assert.Equal([HistoryMarkerKind.Saved, HistoryMarkerKind.SaveFailed], Pull(head).Markers.Select(marker => marker.Kind));
    }

    [Fact]
    public void A_pull_carries_on_from_the_last()
    {
        long head = Markers.Rows.Head;

        Markers.NoteSave(succeeded: true);
        Markers.Flush();

        HistoryBatch first = Pull(head);

        Markers.NoteSave(succeeded: false);
        Markers.Flush();

        HistoryBatch second = Pull(first.NextMarkers);

        Assert.Equal(head + 2L, second.NextMarkers);
        Assert.Equal(HistoryMarkerKind.SaveFailed, Assert.Single(second.Markers).Kind);
    }

    [Fact]
    public void Markers_older_than_the_ring_are_skipped_without_a_fault()
    {
        long start = Markers.Rows.Head;

        for (int i = 0; i < Markers.RingRows + 10; i++)
        {
            Markers.NoteSave(succeeded: true);
            Markers.Flush();
        }

        HistoryBatch batch = Pull(start);

        Assert.True(batch.MarkerFirst > start);
        Assert.Equal(batch.MarkerHead, batch.NextMarkers);
    }

    [Fact]
    public void A_full_batch_leaves_markers_for_the_next_pull()
    {
        long head = Markers.Rows.Head;

        Markers.NoteSave(succeeded: true);
        Markers.Flush();

        fixed (byte* output = _buffer)
        {
            int length = HistoryWriter.Write(FrameEngine.Rollups.Head, Census.Rows.Head, 0L, output, HistoryWriter.HeaderBytes, Sweep.Rows.Head, head);
            HistoryBatch? batch = HistoryReader.Read(_buffer.Take(length).ToArray());

            Assert.NotNull(batch);
            Assert.Empty(batch!.Markers);
            Assert.Equal(head, batch.NextMarkers);
            Assert.True(batch.MarkerHead > batch.NextMarkers);
        }
    }

    [Fact]
    public void A_lag_trip_carries_which_report_holds_its_evidence()
    {
        long head = Markers.Rows.Head;

        Markers.AddLag(42U);

        HistoryMarker marker = Assert.Single(Pull(head).Markers);
        Assert.Equal(HistoryMarkerKind.LagTripped, marker.Kind);
        Assert.Equal(42U, marker.Value);
    }

    [Fact]
    public void A_save_says_how_long_its_frame_took()
    {
        long head = Markers.Rows.Head;

        TreeCounters.Calls[Scope.Components] = 1L;
        FrameEngine.OnFrameEnd(Clock.Now() - (Clock.Frequency / 2L));

        Markers.NoteSave(succeeded: true);
        Markers.Flush();

        Assert.InRange(Assert.Single(Pull(head).Markers).Value, 400U, 2000U);
    }

    [Fact]
    public void A_change_of_detail_is_written_on_the_next_frame()
    {
        DetailMode other = DetailController.Mode == DetailMode.Off ? DetailMode.Grouped : DetailMode.Off;
        long head = Markers.Rows.Head;

        DetailController.SetMode(other);
        Assert.Equal(head, Markers.Rows.Head);

        Markers.Flush();

        HistoryMarker marker = Assert.Single(Pull(head).Markers);
        Assert.Equal(HistoryMarkerKind.DetailChanged, marker.Kind);
        Assert.Equal((uint)other, marker.Value);
    }

    [Fact]
    public void Setting_the_same_detail_marks_nothing()
    {
        long head = Markers.Rows.Head;

        DetailController.SetMode(DetailController.Mode);
        Markers.Flush();

        Assert.Equal(head, Markers.Rows.Head);
    }

    [Fact]
    public void A_run_marks_when_it_starts_and_for_how_long()
    {
        DetailController.Configure(new[] { Site.EntityBefore });
        long head = Markers.Rows.Head;
        var record = default(FrameRecord);

        Capture.Inline = true;
        Assert.Equal(CaptureStart.Started, Capture.TryStart(7, null, _ => { }));

        for (int frame = 0; frame < 16 && Capture.State != CaptureState.Measuring; frame++)
            Capture.OnFrame(&record, true, false);

        HistoryMarker marker = Assert.Single(Pull(head).Markers);
        Assert.Equal(HistoryMarkerKind.RunStarted, marker.Kind);
        Assert.Equal(7U, marker.Value);
    }

    [Fact]
    public void A_finished_chat_run_says_which_it_was()
    {
        long head = Markers.Rows.Head;

        Markers.AddRunFinished(9U);

        HistoryMarker marker = Assert.Single(Pull(head).Markers);
        Assert.Equal(HistoryMarkerKind.RunFinished, marker.Kind);
        Assert.Equal(9U, marker.Value);
    }

    [Fact]
    public void Noting_and_flushing_allocate_nothing()
    {
        long bytes = Allocation.Measure(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                Markers.NoteSave(i % 2 == 0);
                Markers.Flush();
            }
        });

        Assert.Equal(0L, bytes);
    }
}
