using System;
using System.Threading;

namespace SenX.Monitor.Core;

/// <summary>
/// One low priority thread for work that walks a lot of memory, so the game thread never does.
/// It sleeps between jobs and costs nothing while idle. One job at a time: callers take turns.
/// </summary>
public static class Background
{
    private static readonly AutoResetEvent s_wake = new(false);

    private static Thread? s_thread;
    private static Action? s_work;
    private static Exception? s_fault;
    private static int s_done = 1;

    /// <summary>Runs jobs on the calling thread, so tests control when they happen.</summary>
    internal static bool Inline { get; set; }

    /// <summary>True when the last job has finished.</summary>
    public static bool Done => Volatile.Read(ref s_done) == 1;

    /// <summary>What stopped the last job, if anything.</summary>
    public static Exception? Fault => s_fault;

    public static void Start(Action work)
    {
        s_fault = null;
        Volatile.Write(ref s_done, 0);

        if (Inline)
        {
            Run(work);
            return;
        }

        if (s_thread == null)
        {
            s_thread = new Thread(Loop)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "Monitor background",
            };

            s_thread.Start();
        }

        Volatile.Write(ref s_work, work);
        s_wake.Set();
    }

    private static void Loop()
    {
        while (true)
        {
            s_wake.WaitOne();

            Action? work = Interlocked.Exchange(ref s_work, null);
            if (work != null)
                Run(work);
        }
    }

    private static void Run(Action work)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            s_fault = exception;
        }

        Volatile.Write(ref s_done, 1);
    }

    internal static void ResetForTests()
    {
        Inline = false;
        s_fault = null;
        Volatile.Write(ref s_done, 1);
    }
}
