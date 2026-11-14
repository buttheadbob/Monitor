using System;
using System.IO;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar;
using SenX.Monitor.Magnetar.Patching;
using Xunit;

namespace SenX.Monitor.Magnetar.Tests;

public sealed unsafe class WorkerTests
{
    // A failed mod download ends the game before a world loads, and shutting down mustn't report a problem of its own.
    [Fact]
    public void Shutting_down_before_a_world_loaded_is_quiet()
    {
        TextWriter console = Console.Out;
        using var written = new StringWriter();
        Console.SetOut(written);

        try
        {
            var worker = new Worker();
            worker.Prepare();
            worker.Shutdown();
        }
        finally
        {
            Console.SetOut(console);
        }

        Assert.True(!written.ToString().Contains("ERROR"), written.ToString());
    }

    [Fact]
    public void Giving_up_refuses_new_work_and_answers_a_run_in_progress()
    {
        var worker = new Worker();
        worker.Prepare();

        try
        {
            CaptureResult? delivered = new();
            bool answered = false;

            Assert.Equal(CaptureStart.Started, StartRun(result =>
            {
                answered = true;
                delivered = result;
            }));

            worker.GiveUp();

            Assert.True(answered);
            Assert.Null(delivered);
            Assert.True(Worker.Closed);
            Assert.Equal(CaptureStart.NotReady, Capture.TryStart(1, null, _ => { }));
            Assert.Contains("shut itself down", DrillDown.Start(new DrillStartRequest { Seconds = 1 }).Message, StringComparison.Ordinal);
            Assert.Contains("shut itself down", PatchAudit.Start(new AuditStartRequest { Seconds = 1 }).Message, StringComparison.Ordinal);
        }
        finally
        {
            worker.Prepare();
            Capture.Abandon();
        }
    }

    private static CaptureStart StartRun(Action<CaptureResult?> done)
    {
        FrameEngine.Start();
        DetailController.Configure(new[] { Site.EntityBefore });

        // A run won't start on a paused server, and it needs a simulated frame to know it isn't one.
        TreeCounters.Calls[Scope.Components] = 1L;
        FrameEngine.OnFrameEnd(Clock.Now());

        return Capture.TryStart(1, null, done);
    }
}
