using Microsoft.Extensions.Logging;

// What the shared services take from Microsoft.Extensions.Hosting, which net48 doesn't have. Shipping the
// real one would add assemblies that Torch's fixed binding redirects don't cover.
namespace Microsoft.Extensions.Hosting;

/// <summary>Says when the host is closing, so waits can end.</summary>
public interface IHostApplicationLifetime
{
    CancellationToken ApplicationStopping { get; }
}

/// <summary>Work that runs for as long as the plugin does, off the game thread.</summary>
public abstract class BackgroundService : IDisposable
{
    private static readonly TimeSpan s_restartAfter = TimeSpan.FromMinutes(1);

    private CancellationTokenSource? _stopping;
    private Task? _running;

    protected abstract Task ExecuteAsync(CancellationToken stoppingToken);

    public void Start()
    {
        if (_running != null)
            return;

        _stopping = new CancellationTokenSource();
        CancellationToken token = _stopping.Token;
        _running = Task.Run(() => RunAsync(token));
    }

    // The real host stops the whole application when one of these fails. Here that's Torch, so it says
    // so in the log and tries again, and history shows a gap rather than quietly ending for good.
    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ExecuteAsync(token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                MonitorLogSink.Write?.Invoke(GetType().Name + " failed, and starts again in a minute.", exception);
            }

            try
            {
                await Task.Delay(s_restartAfter, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Asks the work to stop and doesn't wait for it, because this runs while Torch is closing.</summary>
    public void Stop()
    {
        _stopping?.Cancel();
        _running = null;
    }

    public virtual void Dispose() => Stop();
}
