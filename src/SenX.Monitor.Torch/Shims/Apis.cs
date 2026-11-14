using System.Collections.Concurrent;

// Newer library calls the shared services make, kept to the ones they use. Each behaves as the real one
// does for the way it's called there.
namespace System
{
    internal static class CoreShims
    {
        extension(Math)
        {
            public static double Clamp(double value, double lowest, double highest) => value < lowest ? lowest : value > highest ? highest : value;

            public static int Clamp(int value, int lowest, int highest) => value < lowest ? lowest : value > highest ? highest : value;
        }

        extension(Array)
        {
            public static void Fill<T>(T[] array, T value)
            {
                for (int i = 0; i < array.Length; i++)
                    array[i] = value;
            }

            public static void Clear(Array array) => Array.Clear(array, 0, array.Length);
        }

        extension(System.Net.HttpStatusCode)
        {
            public static System.Net.HttpStatusCode TooManyRequests => (System.Net.HttpStatusCode)429;
        }
    }
}

namespace System.Threading
{
    internal sealed class PeriodicTimer(TimeSpan period) : IDisposable
    {
        public async Task<bool> WaitForNextTickAsync(CancellationToken token = default)
        {
            await Task.Delay(period, token).ConfigureAwait(false);
            return true;
        }

        public void Dispose()
        {
        }
    }
}

namespace System.Threading.Tasks
{
    internal static class TaskShims
    {
        public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (token.Register(() => cancelled.TrySetCanceled(token)))
            {
                await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            return await task.ConfigureAwait(false);
        }
    }
}

// The game's library has its own versions of these under the System namespaces, so these sit in
// Monitor's namespace, which the compiler looks in first. That also keeps the pages free of the game's DLLs.
namespace SenX.Monitor
{
    internal static class CollectionShims
    {
        public static bool Contains(this string text, string value, StringComparison comparison) => text.IndexOf(value, comparison) >= 0;

        public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> values, TKey key) where TKey : notnull =>
            values.TryGetValue(key, out TValue? value) ? value : default;

        public static TValue? GetValueOrDefault<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> values, TKey key) where TKey : notnull =>
            values.TryGetValue(key, out TValue? value) ? value : default;

        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }
}

namespace System.Linq
{
    internal static class LinqShims
    {
        public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> values) => values.OrderBy(value => value);

        public static IEnumerable<T[]> Chunk<T>(this IEnumerable<T> values, int size)
        {
            var chunk = new List<T>(size);

            foreach (T value in values)
            {
                chunk.Add(value);

                if (chunk.Count == size)
                {
                    yield return chunk.ToArray();
                    chunk.Clear();
                }
            }

            if (chunk.Count > 0)
                yield return chunk.ToArray();
        }

        public static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> values, Func<T, TKey> key)
        {
            var seen = new HashSet<TKey>();

            foreach (T value in values)
            {
                if (seen.Add(key(value)))
                    yield return value;
            }
        }
    }
}
