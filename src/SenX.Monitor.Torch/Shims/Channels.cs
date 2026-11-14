using System.Runtime.CompilerServices;

// What the shared services take from System.Threading.Channels: a bounded queue with one reader that drops
// its oldest entry when full. await foreach only needs the right shape, so no extra assembly is involved.
namespace System.Threading.Channels;

internal enum BoundedChannelFullMode
{
    DropOldest,
}

internal sealed class BoundedChannelOptions(int capacity)
{
    public int Capacity { get; } = capacity;

    public BoundedChannelFullMode FullMode { get; set; }

    public bool SingleReader { get; set; }

    public bool SingleWriter { get; set; }
}

internal static class Channel
{
    public static Channel<T> CreateBounded<T>(BoundedChannelOptions options) => new(options.Capacity);
}

internal sealed class Channel<T>(int capacity)
{
    private readonly Queue<T> _items = new();
    private readonly SemaphoreSlim _ready = new(0);

    public ChannelWriter<T> Writer => new(this);

    public ChannelReader<T> Reader => new(this);

    internal bool Add(T item)
    {
        lock (_items)
        {
            if (_items.Count >= capacity)
                _items.Dequeue();
            else
                _ready.Release();

            _items.Enqueue(item);
        }

        return true;
    }

    internal async Task<T> TakeAsync(CancellationToken token)
    {
        await _ready.WaitAsync(token).ConfigureAwait(false);

        lock (_items)
            return _items.Dequeue();
    }
}

internal readonly struct ChannelWriter<T>(Channel<T> channel)
{
    public bool TryWrite(T item) => channel.Add(item);
}

internal readonly struct ChannelReader<T>(Channel<T> channel)
{
    public ChannelItems<T> ReadAllAsync(CancellationToken token = default) => new(channel, token);
}

/// <summary>Runs until the token is cancelled, which ends the loop with an exception, as the real one does.</summary>
internal readonly struct ChannelItems<T>(Channel<T> channel, CancellationToken token)
{
    public ChannelItems<T> ConfigureAwait(bool continueOnCapturedContext) => this;

    public ChannelEnumerator<T> GetAsyncEnumerator() => new(channel, token);
}

internal sealed class ChannelEnumerator<T>(Channel<T> channel, CancellationToken token)
{
    public T Current { get; private set; } = default!;

    public ConfiguredTaskAwaitable<bool> MoveNextAsync() => Next().ConfigureAwait(false);

    private async Task<bool> Next()
    {
        Current = await channel.TakeAsync(token).ConfigureAwait(false);
        return true;
    }
}
