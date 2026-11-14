using System;
using BenchmarkDotNet.Attributes;
using SenX.Monitor.Core;

namespace SenX.Monitor.Benchmarks;

/// <summary>Cost of each hot-path primitive on its own, measured with the real default table sizes.</summary>
public unsafe class PrimitiveBenchmarks
{
    private const int Site = 21;
    private const int LoadedEntities = 100_000;

    private long[] _ids = Array.Empty<long>();
    private int _next;
    private Action _callback = () => { };
    private LogHistogram _histogram = null!;
    private RecordRing _ring = null!;

    [GlobalSetup]
    public void Setup()
    {
        MonitorCore.Initialize();
        ThreadBlocks.Initialize(DetailLayout.Default(32));
        ThreadBlocks.BindMainThread();

        // Random ids in random order, so table lookups miss the cache as they would on a real
        // world. The table holds as many entities as a large server would, not a toy number.
        var random = new Random(12345);
        var buffer = new byte[8];
        _ids = new long[LoadedEntities];

        for (int i = 0; i < _ids.Length; i++)
        {
            random.NextBytes(buffer);
            _ids[i] = BitConverter.ToInt64(buffer, 0) | 1L;
            Recorder.AddEntity(Site, Phase.Before, _ids[i], 10L, 0);
        }

        for (int i = _ids.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (_ids[i], _ids[j]) = (_ids[j], _ids[i]);
        }

        _histogram = new LogHistogram();
        _ring = new RecordRing(160, 1 << 14);
    }

    private long NextId()
    {
        int index = _next + 1;
        if (index == _ids.Length)
            index = 0;

        _next = index;
        return _ids[index];
    }

    [Benchmark(Description = "Clock read")]
    public long ClockRead() => Clock.Now();

    [Benchmark(Description = "Clock read plus unit conversion")]
    public long ClockToUnits() => Clock.ToUnits(Clock.Now());

    [Benchmark(Description = "DateTime.UtcNow, for comparison")]
    public DateTime UtcNow() => DateTime.UtcNow;

    [Benchmark(Description = "Gate read")]
    public byte GateRead() => Gates.Read(Site);

    [Benchmark(Description = "Sampler test")]
    public bool SamplerHit() => Sampler.Hit(NextId(), Site, 3);

    [Benchmark(Description = "Thread block lookup (TLS)")]
    public long ThreadBlockLookup() => (long)ThreadBlocks.Current();

    [Benchmark(Description = "Tree counter add")]
    public void TreeAdd() => TreeCounters.Add(Site, 100L);

    [Benchmark(Description = "Record entity, loaded table, random order")]
    public void RecordEntity() => Recorder.AddEntity(Site, Phase.Before, NextId(), 100L, 3);

    [Benchmark(Description = "Record key")]
    public void RecordKey() => Recorder.AddKey(Site, 77L, 100L, 3);

    [Benchmark(Description = "Delegate key")]
    public long DelegateKey() => DelegateIdentity.Key(_callback);

    [Benchmark(Description = "Histogram record")]
    public void HistogramRecord() => _histogram.Record(166_666L);

    [Benchmark(Description = "Ring reserve and commit")]
    public void RingWrite()
    {
        _ring.Reserve();
        _ring.Commit();
    }

    [Benchmark(Description = "Sampler advance")]
    public void SamplerAdvance() => Sampler.NextFrame();
}
