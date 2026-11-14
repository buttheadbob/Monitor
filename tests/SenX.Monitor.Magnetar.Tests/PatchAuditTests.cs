using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;
using SenX.Monitor.Magnetar.Patching;
using Xunit;

namespace SenX.Monitor.Magnetar.Tests;

// Stands in for a plugin and the game method it patches.
public static class AuditTarget
{
    public static int Calls;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Work() => Calls++;

    public static void PluginPrefix() => Spin(2);

    public static void PluginPostfix() => Spin(4);

    private static void Spin(int milliseconds)
    {
        long until = Stopwatch.GetTimestamp() + (milliseconds * Stopwatch.Frequency / 1000L);

        while (Stopwatch.GetTimestamp() < until)
        {
        }
    }
}

public sealed class PatchAuditTests : IDisposable
{
    private const string PluginId = "some.plugin";

    private static readonly MethodInfo s_work = AccessTools.Method(typeof(AuditTarget), nameof(AuditTarget.Work));

    private readonly Harmony _plugin = new(PluginId);

    public PatchAuditTests()
    {
        MonitorCore.Initialize();
        FrameEngine.Start();

        _plugin.Patch(
            s_work,
            prefix: new HarmonyMethod(typeof(AuditTarget), nameof(AuditTarget.PluginPrefix)),
            postfix: new HarmonyMethod(typeof(AuditTarget), nameof(AuditTarget.PluginPostfix)));
    }

    public void Dispose()
    {
        PatchAudit.RemoveAll();
        SenX.Monitor.Magnetar.Patching.OwnPatches.Remove(_plugin);
    }

    private static string Name => PatchList.Describe(s_work);

    private static AuditResultResponse Result() => JsonConvert.DeserializeObject<AuditResultResponse>(PatchAudit.ResultJson())!;

    // Bounded, so a fault fails the test and can't hang the run.
    private static void Finish(Action duringMeasuring)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(30);

        while (Result().State == RunState.Running && DateTime.UtcNow < giveUp)
        {
            PatchAudit.Step();
            duringMeasuring();
        }

        Assert.Equal(RunState.Finished, Result().State);
    }

    [Fact]
    public void Other_plugins_patches_are_listed()
    {
        PatchedMethodRow row = PatchList.Page(0).Methods.Single(method => method.Method == Name);

        Assert.Contains(row.Patches, patch => patch.Kind == PatchKinds.Prefix && patch.Owner == PluginId);
        Assert.Contains(row.Patches, patch => patch.Kind == PatchKinds.Postfix && patch.Owner == PluginId);
    }

    [Fact]
    public void A_page_says_how_many_there_are()
    {
        Assert.True(PatchList.Page(0).Total >= 1);
    }

    [Fact]
    public void An_audit_refuses_a_method_nobody_patched()
    {
        AuditStartResponse start = PatchAudit.Start(new AuditStartRequest { Methods = ["Not.A.Method()"], Seconds = 1 });

        Assert.False(start.Started);
        Assert.NotNull(start.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void An_audit_has_limits(int seconds)
    {
        Assert.False(PatchAudit.Start(new AuditStartRequest { Methods = [Name], Seconds = seconds }).Started);
    }

    [Fact]
    public void One_audit_at_a_time()
    {
        Assert.True(PatchAudit.Start(new AuditStartRequest { Methods = [Name], Seconds = 1 }).Started);
        Assert.False(PatchAudit.Start(new AuditStartRequest { Methods = [Name], Seconds = 1 }).Started);
    }

    [Fact]
    public void An_audit_splits_a_call_into_three()
    {
        Assert.True(PatchAudit.Start(new AuditStartRequest { Methods = [Name], Seconds = 1 }).Started);

        Finish(AuditTarget.Work);

        AuditRow row = Assert.Single(Result().Rows);
        Assert.True(row.CallsPerFrame > 0d);
        Assert.True(row.PostfixMs > row.PrefixMs, $"The postfix takes twice as long as the prefix, but they read {row.PostfixMs} and {row.PrefixMs} ms, with {row.OriginalMs} for the original at {row.CallsPerFrame} calls.");
        Assert.True(row.PrefixMs > row.OriginalMs, "The original does almost nothing.");
        Assert.Contains("SenX.Monitor.Magnetar.Tests", row.Plugins);
    }

    [Fact]
    public void An_audit_leaves_nothing_behind()
    {
        Assert.True(PatchAudit.Start(new AuditStartRequest { Methods = [Name], Seconds = 1 }).Started);

        Finish(AuditTarget.Work);

        Assert.DoesNotContain(Harmony.GetPatchInfo(s_work)!.Owners, owner => owner == PatchAudit.HarmonyId);
        Assert.Contains(PluginId, Harmony.GetPatchInfo(s_work)!.Owners);
    }

    [Fact]
    public void Shutting_down_mid_audit_leaves_nothing_behind()
    {
        Assert.True(PatchAudit.Start(new AuditStartRequest { Methods = [Name], Seconds = 60 }).Started);

        for (int i = 0; i < 100; i++)
            PatchAudit.Step();

        PatchAudit.RemoveAll();

        Assert.DoesNotContain(Harmony.GetPatchInfo(s_work)!.Owners, owner => owner == PatchAudit.HarmonyId);
    }

    [Fact]
    public void The_game_method_still_runs_during_an_audit()
    {
        int before = AuditTarget.Calls;

        Assert.True(PatchAudit.Start(new AuditStartRequest { Methods = [Name], Seconds = 1 }).Started);

        for (int i = 0; i < 100; i++)
            PatchAudit.Step();

        AuditTarget.Work();

        Assert.Equal(before + 1, AuditTarget.Calls);
    }

    [Fact]
    public void A_transpiled_method_is_flagged()
    {
        var transpiler = new HarmonyMethod(typeof(PatchAuditTests), nameof(Unchanged));
        _plugin.Patch(s_work, transpiler: transpiler);

        Assert.True(PatchAudit.Start(new AuditStartRequest { Methods = [Name], Seconds = 1 }).Started);

        Finish(AuditTarget.Work);

        Assert.True(Assert.Single(Result().Rows).Transpiled);
    }

    public static System.Collections.Generic.IEnumerable<CodeInstruction> Unchanged(System.Collections.Generic.IEnumerable<CodeInstruction> instructions) => instructions;
}
