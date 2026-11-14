using System;
using System.Diagnostics;
using System.Threading;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public unsafe class RecordingTests
{
    private const int Site = 11;

    public RecordingTests() => TestSetup.Reset();

    private static long RawFor(long units) => (long)(units * (double)Stopwatch.Frequency / Clock.UnitsPerSecond);

    [Fact]
    public void Table_returns_the_same_slot_for_the_same_key()
    {
        OpenTable<EntitySlot> table = OpenTable<EntitySlot>.Allocate(64);

        EntitySlot* first = table.Claim(1234L, out bool created);
        Assert.True(created);

        EntitySlot* second = table.Claim(1234L, out created);
        Assert.False(created);
        Assert.True(first == second);
    }

    [Fact]
    public void Table_refuses_rather_than_grows_when_full()
    {
        OpenTable<KeySlot> table = OpenTable<KeySlot>.Allocate(16);
        int stored = 0;

        for (long key = 1; key <= 64; key++)
        {
            if (table.Claim(key, out _) != null)
                stored++;
        }

        Assert.Equal(16, stored);
        Assert.True(table.Claim(1000L, out _) == null);
    }

    [Fact]
    public void Table_rejects_a_bad_capacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenTable<KeySlot>.Allocate(100));

    [Fact]
    public void Entity_records_scale_by_the_sample_shift()
    {
        long raw = RawFor(1000);

        EntitySlot* created = Recorder.AddEntity(Site, Phase.Before, 42L, raw, 3);
        Assert.True(created != null);
        Assert.True(Recorder.AddEntity(Site, Phase.After, 42L, raw, 3) == null);

        ThreadBlock* block = ThreadBlocks.Current();
        long expected = raw << 3;

        Assert.Equal(expected, created->PhaseTicks[Phase.Before]);
        Assert.Equal(expected, created->PhaseTicks[Phase.After]);
        Assert.Equal(2u, created->Samples);
        Assert.Equal(expected * 2, block->SiteTicks[Site]);
        Assert.Equal(2L, block->SiteSamples[Site]);
    }

    [Fact]
    public void Entity_max_keeps_the_unscaled_worst_call()
    {
        Recorder.AddEntity(Site, Phase.Before, 7L, RawFor(100), 4);
        Recorder.AddEntity(Site, Phase.Before, 7L, RawFor(900), 4);
        Recorder.AddEntity(Site, Phase.Before, 7L, RawFor(300), 4);

        ThreadBlock* block = ThreadBlocks.Current();
        EntitySlot* slot = block->Entities.Claim(7L, out _);
        Assert.InRange(slot->MaxUnits, 899u, 901u);
    }

    [Fact]
    public void Overflow_keeps_totals_right_and_loses_only_the_row()
    {
        ThreadBlock* block = ThreadBlocks.Current();
        long raw = RawFor(10);

        for (long id = 1; id <= TestSetup.MainSlots * 2L; id++)
            Recorder.AddEntity(Site, Phase.Before, id, raw, 0);

        long expected = raw * TestSetup.MainSlots * 2L;
        Assert.Equal(expected, block->SiteTicks[Site]);
        Assert.Equal(TestSetup.MainSlots * 2L, block->SiteSamples[Site]);
        Assert.True(block->OverflowTicks[Site] > 0L);
    }

    [Fact]
    public void Slow_calls_reach_the_ring_and_fast_calls_do_not()
    {
        Recorder.Frame = 555UL;

        Recorder.AddEntity(Site, Phase.Before, 1L, RawFor(10), 2);
        Recorder.AddEntity(Site, Phase.Before, 2L, RawFor(Recorder.SlowThresholdUnits + 5), 2);

        ThreadBlock* block = ThreadBlocks.Current();
        Assert.Equal(1u, block->SlowHead);

        SlowEntry entry = block->Slow[0];
        Assert.Equal(555UL, entry.Frame);
        Assert.Equal(2L, entry.Id);
        Assert.Equal(Site, entry.Site);

        // The ring holds what the call really took, never the scaled estimate.
        Assert.True(entry.Units < Recorder.SlowThresholdUnits * 2);
    }

    [Fact]
    public void Keys_aggregate_separately_from_entities()
    {
        long raw = RawFor(50);

        Assert.True(Recorder.AddKey(Site, 900L, raw, 1));
        Assert.False(Recorder.AddKey(Site, 900L, raw, 1));

        ThreadBlock* block = ThreadBlocks.Current();
        KeySlot* slot = block->Keys.Claim(900L, out _);

        Assert.Equal((raw << 1) * 2, slot->ScaledTicks);
        Assert.Equal(2u, slot->Samples);
        Assert.Equal(Site, slot->Scope);
    }

    [Fact]
    public void The_main_thread_keeps_block_zero_across_periods()
    {
        Assert.Equal(0, ThreadBlocks.Current()->Index);

        ThreadBlocks.ClearAll();
        ThreadBlocks.BeginPeriod();

        Assert.Equal(0, ThreadBlocks.Current()->Index);
    }

    [Fact]
    public void Worker_threads_each_get_their_own_block()
    {
        var indexes = new int[TestSetup.WorkerBlocks];
        var threads = new Thread[indexes.Length];
        using var ready = new CountdownEvent(indexes.Length);

        for (int i = 0; i < threads.Length; i++)
        {
            int slot = i;
            threads[i] = new Thread(() =>
            {
                indexes[slot] = ThreadBlocks.Current()->Index;
                ready.Signal();
            });
            threads[i].Start();
        }

        ready.Wait();
        foreach (Thread thread in threads)
            thread.Join();

        Array.Sort(indexes);
        for (int i = 0; i < indexes.Length; i++)
            Assert.Equal(i + 1, indexes[i]);
    }

    [Fact]
    public void Too_many_threads_become_a_counter_not_a_crash()
    {
        long before = ThreadBlocks.Unattached;
        int threads = TestSetup.WorkerBlocks + 3;
        var workers = new Thread[threads];

        for (int i = 0; i < threads; i++)
        {
            long id = i + 1;
            workers[i] = new Thread(() => Recorder.AddEntity(Site, Phase.Before, id, 100L, 0));
            workers[i].Start();
            workers[i].Join();
        }

        Assert.Equal(before + 3, ThreadBlocks.Unattached);
    }

    [Fact]
    public void A_new_period_rebinds_threads_that_held_stale_blocks()
    {
        var bound = new ManualResetEventSlim();
        var proceed = new ManualResetEventSlim();
        int first = -1;
        int second = -1;

        var worker = new Thread(() =>
        {
            first = ThreadBlocks.Current()->Index;
            bound.Set();
            proceed.Wait();
            second = ThreadBlocks.Current()->Index;
        });

        worker.Start();
        bound.Wait();

        ThreadBlocks.ClearAll();
        ThreadBlocks.BeginPeriod();

        proceed.Set();
        worker.Join();

        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void Concurrent_writers_lose_nothing()
    {
        const int perThread = 200_000;
        int threads = TestSetup.WorkerBlocks;
        long raw = RawFor(10);
        var workers = new Thread[threads];
        using var start = new ManualResetEventSlim();

        for (int i = 0; i < threads; i++)
        {
            long baseId = (i + 1) * 1000L;
            workers[i] = new Thread(() =>
            {
                start.Wait();
                for (int n = 0; n < perThread; n++)
                    Recorder.AddEntity(Site, Phase.Before, baseId + n % 50, raw, 0);
            });
            workers[i].Start();
        }

        start.Set();
        foreach (Thread worker in workers)
            worker.Join();

        long samples = 0L;
        long ticks = 0L;
        for (int i = 1; i <= threads; i++)
        {
            samples += ThreadBlocks.At(i)->SiteSamples[Site];
            ticks += ThreadBlocks.At(i)->SiteTicks[Site];
        }

        Assert.Equal((long)perThread * threads, samples);
        Assert.Equal(raw * perThread * threads, ticks);
    }
}
