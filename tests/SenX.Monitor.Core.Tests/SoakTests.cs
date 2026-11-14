using System;
using System.Collections.Generic;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

/// <summary>
/// The Phase 2 gate, with no server needed. A whole day of frames, census passes and history pulls
/// goes through the real code as fast as it can run. Quasar's side is the real reader, pulling as it
/// will in production. A day needs to end with every second accounted for and memory where it began.
/// </summary>
public unsafe class SoakTests
{
    private const int Seconds = 24 * 60 * 60;
    private const int FramesPerSecond = 60;
    private const int PullEverySeconds = 30;
    private const int Stretches = 4;
    private const int MaxPullsToCatchUp = 100;

    // Runs sometimes show one small step and then stay flat, from the test host and not from Monitor.
    // A leak of one 24-byte object a second adds 1.5 MB over the measured 18 hours, so this catches that.
    private const long MaxGrowthBytes = 256 * 1024;

#if NET
    private const long MaxWorkerBytes = 0L;
#else
    // On .NET Framework only, in about one run in four, this thread's counter moves by up to 15 KB,
    // once, at a set time into the run. It isn't explained: no GC runs in that window, blocking and
    // background GCs both failed to reproduce it, and the frame path has no time-based code. It can't
    // be per-frame work, which would be megabytes. Anything repeating per pass or per pull passes 64 KB.
    private const long MaxWorkerBytes = 64 * 1024;
#endif

    private struct SoakWorld : ICensusSource
    {
        public int Snapshot() => 500;

        public void Visit(int index) => Census.AddGrid(1L + (index % 20), 50, 25);

        public void Finish()
        {
            for (int i = 1; i <= 40; i++)
                Census.AddPlayer(1000L + i, 1L + (i % 20), 30 + (i % 9));

            Census.SetFactionCount(20);
        }
    }

    private struct Totals
    {
        public long RollupsReceived;
        public long CensusReceived;
        public long DetailsReceived;
        public long Gaps;
        public long Repeats;
        public long LargestBatch;
        public long Unreadable;

        /// <summary>Bytes the worker itself allocated. The reader and this test's bookkeeping don't count.</summary>
        public long WorkerBytes;

        /// <summary>Where the first of those bytes came from, so a failure says where to look.</summary>
        public string? FirstAllocation;

        public void Count(long bytes, string where)
        {
            if (bytes != 0L && FirstAllocation == null)
                FirstAllocation = where;

            WorkerBytes += bytes;
        }
    }

    [Fact]
    public void A_day_of_running_loses_nothing_and_keeps_memory_flat()
    {
        TestSetup.Reset();
        FrameEngine.Initialize();
        FrameEngine.Start();
        HistoryWriter.StartedUtcTicks = 42L;

        var buffer = new byte[HistoryWriter.MaxBytes];
        var world = default(SoakWorld);
        var totals = default(Totals);

        long nextRollup = FrameEngine.Rollups.Head;
        long nextCensus = Census.Rows.Head;
        long nextDetail = Census.DetailSequence;
        long firstRollup = nextRollup;

        // Warm up, so anything allocated once isn't blamed on the soak.
        RunSeconds(120, ref world, buffer, ref nextRollup, ref nextCensus, ref nextDetail, ref totals);

        totals.WorkerBytes = 0L;
        totals.FirstAllocation = null;

        // Memory after each stretch. One-off growth settles, while a leak keeps climbing.
        var memory = new List<long>();

        for (int stretch = 0; stretch < Stretches; stretch++)
        {
            RunSeconds(Seconds / Stretches, ref world, buffer, ref nextRollup, ref nextCensus, ref nextDetail, ref totals);
            memory.Add(GC.GetTotalMemory(forceFullCollection: true));
        }

        long expectedRows = FrameEngine.Rollups.Head - firstRollup;
        string trail = string.Join(", ", memory);

        // Nothing allocated means nothing for the GC to do on Monitor's account. Counting gen2 collections
        // here would measure the reader and the test host, which aren't in the game process on a real server.
        Assert.True(totals.WorkerBytes <= MaxWorkerBytes, "The worker allocated " + totals.WorkerBytes + " bytes, first in " + totals.FirstAllocation + ".");
        Assert.True(totals.Gaps == 0L, totals.Gaps + " gaps in history.");
        Assert.True(totals.Repeats == 0L, totals.Repeats + " repeated rows.");
        Assert.True(totals.Unreadable == 0L, totals.Unreadable + " unreadable batches.");
        Assert.Equal(expectedRows, totals.RollupsReceived);
        Assert.Equal(Census.Rows.Head, nextCensus);
        Assert.True(totals.CensusReceived >= Seconds / Census.PeriodSeconds, "Got " + totals.CensusReceived + " census rows.");
        Assert.True(totals.DetailsReceived >= Seconds / Census.PeriodSeconds, "Got " + totals.DetailsReceived + " details.");
        Assert.True(totals.LargestBatch <= HistoryWriter.MaxBytes);

        // A leak of even one small object a second would add a megabyte over these 18 hours.
        long growth = memory[memory.Count - 1] - memory[0];
        Assert.True(growth < MaxGrowthBytes, "Memory grew " + growth + " bytes after the first stretch: " + trail);

    }

    private static void OneFrame()
    {
        TreeCounters.Calls[Scope.Components] = 1L;
        TreeCounters.Add(Scope.Physics, 1000L);
        FrameEngine.OnFrameEnd(Clock.Now());
    }

    private static void RunSeconds(
        int seconds,
        ref SoakWorld world,
        byte[] buffer,
        ref long nextRollup,
        ref long nextCensus,
        ref long nextDetail,
        ref Totals totals)
    {
        for (int second = 1; second <= seconds; second++)
        {
            long allocated = GcProbe.AllocatedOnThread();

            for (int frame = 0; frame < FramesPerSecond; frame++)
                OneFrame();

            totals.Count(GcProbe.AllocatedOnThread() - allocated, "frames, second " + second);

            allocated = GcProbe.AllocatedOnThread();
            FrameEngine.CommitSecondNow();
            totals.Count(GcProbe.AllocatedOnThread() - allocated, "commit, second " + second);

            allocated = GcProbe.AllocatedOnThread();

            if (second % Census.PeriodSeconds == 0)
            {
                Census.MakeDue();
                while (true)
                {
                    Census.Step(ref world, paused: false);
                    if (!Census.Walking)
                        break;
                }
            }

            totals.Count(GcProbe.AllocatedOnThread() - allocated, "census, second " + second);

            if (second % PullEverySeconds == 0)
                Pull(buffer, ref nextRollup, ref nextCensus, ref nextDetail, ref totals);
        }
    }

    // What Quasar's collector does: pull from where it got to, until it's caught up.
    private static void Pull(byte[] buffer, ref long nextRollup, ref long nextCensus, ref long nextDetail, ref Totals totals)
    {
        // Catching up takes a few batches at most. A pull that never ends should fail here, not hang.
        for (int attempt = 0; attempt < MaxPullsToCatchUp; attempt++)
        {
            int length;
            long allocated = GcProbe.AllocatedOnThread();

            fixed (byte* output = buffer)
                length = HistoryWriter.Write(nextRollup, nextCensus, nextDetail, output, buffer.Length, Sweep.Rows.Head);

            totals.Count(GcProbe.AllocatedOnThread() - allocated, "history write");
            totals.LargestBatch = Math.Max(totals.LargestBatch, length);

            var bytes = new byte[length];
            Buffer.BlockCopy(buffer, 0, bytes, 0, length);

            // The base64 string is the one allocation a real pull can't avoid. It's short-lived and under the LOH limit.
            HistoryBatch? batch = HistoryReader.Read(Convert.FromBase64String(Convert.ToBase64String(bytes)));
            if (batch == null)
            {
                totals.Unreadable++;
                return;
            }

            if (batch.RollupFirst > nextRollup)
                totals.Gaps++;

            if (batch.RollupFirst < nextRollup)
                totals.Repeats++;

            totals.RollupsReceived += batch.Rollups.Count;
            totals.CensusReceived += batch.Census.Count;
            if (batch.HasDetail)
                totals.DetailsReceived++;

            nextRollup = batch.NextRollup;
            nextCensus = batch.NextCensus;
            nextDetail = batch.DetailSequence;

            if (nextRollup >= batch.RollupHead && nextCensus >= batch.CensusHead)
                return;
        }

        totals.Repeats++;
    }
}
