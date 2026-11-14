using System.Collections.Generic;

namespace SenX.Monitor.Contracts;

/// <summary>
/// The latest names noted for things that have left the game, so a report can still say what they were.
/// It holds a fixed number and the oldest goes first, so it can't grow however much a server removes.
/// </summary>
public sealed class RecentNames
{
    private readonly object _gate = new();
    private readonly Dictionary<long, string> _names = new();
    private readonly long[] _order;
    private int _next;

    public RecentNames(int capacity) => _order = new long[capacity];

    public void Note(long key, string name)
    {
        lock (_gate)
        {
            if (!_names.ContainsKey(key))
            {
                if (_names.Count == _order.Length)
                    _names.Remove(_order[_next]);

                _order[_next] = key;
                _next = (_next + 1) % _order.Length;
            }

            _names[key] = name;
        }
    }

    public string? Find(long key)
    {
        lock (_gate)
            return _names.TryGetValue(key, out string? name) ? name : null;
    }
}
