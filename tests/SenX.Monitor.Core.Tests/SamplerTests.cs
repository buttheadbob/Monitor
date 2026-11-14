using System;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public class SamplerTests
{
    private static long[] Ids(int count, int seed)
    {
        var random = new Random(seed);
        var ids = new long[count];
        var buffer = new byte[8];

        for (int i = 0; i < count; i++)
        {
            random.NextBytes(buffer);
            ids[i] = BitConverter.ToInt64(buffer, 0);
        }

        return ids;
    }

    [Fact]
    public void Shift_zero_times_every_call()
    {
        foreach (long id in Ids(10_000, 1))
            Assert.True(Sampler.Hit(id, 3, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    public void Hit_rate_matches_the_shift(int shift)
    {
        long[] ids = Ids(4_000, 2);
        long hits = 0;
        const int frames = 2_000;

        for (int frame = 0; frame < frames; frame++)
        {
            Sampler.NextFrame();
            foreach (long id in ids)
            {
                if (Sampler.Hit(id, 7, shift))
                    hits++;
            }
        }

        double expected = 1.0 / (1 << shift);
        double actual = (double)hits / (frames * ids.Length);
        Assert.InRange(actual, expected * 0.9, expected * 1.1);
    }

    [Fact]
    public void Negative_ids_are_not_favoured()
    {
        // SE entity ids are 64 bit and often negative as a signed long.
        long[] negative = Ids(4_000, 3);
        for (int i = 0; i < negative.Length; i++)
            negative[i] |= long.MinValue;

        long hits = 0;
        const int frames = 1_000;
        for (int frame = 0; frame < frames; frame++)
        {
            Sampler.NextFrame();
            foreach (long id in negative)
            {
                if (Sampler.Hit(id, 7, 4))
                    hits++;
            }
        }

        double actual = (double)hits / (frames * negative.Length);
        Assert.InRange(actual, (1.0 / 16) * 0.9, (1.0 / 16) * 1.1);
    }

    [Theory]
    [InlineData(10, 3)]
    [InlineData(100, 3)]
    [InlineData(10, 4)]
    [InlineData(100, 6)]
    public void Periodic_updates_are_not_starved(int period, int shift)
    {
        // Frame rotation left 49 to 75 percent of these entities never sampled.
        long[] ids = Ids(2_000, 4);
        var sampled = new bool[ids.Length];
        int updates = (1 << shift) * 12;

        for (int frame = 0; frame < updates * period; frame++)
        {
            Sampler.NextFrame();
            for (int i = 0; i < ids.Length; i++)
            {
                if (frame % period == i % period && Sampler.Hit(ids[i], 9, shift))
                    sampled[i] = true;
            }
        }

        int missed = 0;
        foreach (bool hit in sampled)
        {
            if (!hit)
                missed++;
        }

        // (1 - 1/N)^(12N) is about e^-12, so nearly every entity should have been seen.
        Assert.True(missed <= ids.Length / 100, $"{missed} of {ids.Length} entities were never sampled.");
    }

    [Fact]
    public void Nested_scopes_are_sampled_independently()
    {
        long[] ids = Ids(4_000, 5);
        long both = 0;
        const int frames = 2_000;

        for (int frame = 0; frame < frames; frame++)
        {
            Sampler.NextFrame();
            foreach (long id in ids)
            {
                if (Sampler.Hit(id, 1, 2) && Sampler.Hit(id, 2, 2))
                    both++;
            }
        }

        double actual = (double)both / (frames * ids.Length);
        Assert.InRange(actual, (1.0 / 16) * 0.9, (1.0 / 16) * 1.1);
    }
}
