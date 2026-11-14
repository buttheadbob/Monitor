using System;
using System.Threading;
using System.Threading.Tasks;

namespace SenX.Monitor.Contracts;

/// <summary>Spaces out something that costs the game a frame. Asking again soon just waits its turn.</summary>
public sealed class Pace
{
    private readonly object _gate = new();
    private readonly TimeSpan _gap;
    private DateTime _next;

    public Pace(TimeSpan gap) => _gap = gap;

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait;

        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            DateTime turn = _next > now ? _next : now;

            _next = turn + _gap;
            wait = turn - now;
        }

        return wait > TimeSpan.Zero ? Task.Delay(wait, cancellationToken) : Task.CompletedTask;
    }
}
