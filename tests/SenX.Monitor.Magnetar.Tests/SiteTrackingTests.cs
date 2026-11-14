using System;
using System.Collections.Generic;
using SenX.Monitor.Magnetar.Patching;
using Xunit;

namespace SenX.Monitor.Magnetar.Tests;

public sealed class SiteTrackingTests
{
    private const string Owner = "other.plugin";

    private static readonly IReadOnlyList<string> s_nobody = Array.Empty<string>();
    private static readonly IReadOnlyList<string> s_someone = new[] { Owner };

    private static CallTarget Target(int expected) => new("Game.Type", "Update", 0, expected, CallSiteKind.Substitute, 5, null);

    private static SiteResult Evaluated(SiteState state, int expected = 2) => new("Caller", Target(expected), state, expected, 0, string.Empty);

    [Fact]
    public void A_fully_rewritten_site_stays_as_the_game_read()
    {
        Assert.Equal(SiteState.Ok, PatchEngine.Live(Evaluated(SiteState.Ok), 2, 2, true, s_nobody).State);
        Assert.Equal(SiteState.Drifted, PatchEngine.Live(Evaluated(SiteState.Drifted), 2, 2, true, s_nobody).State);
    }

    [Fact]
    public void A_call_gone_from_a_method_someone_else_transpiles_is_taken_over()
    {
        SiteResult result = PatchEngine.Live(Evaluated(SiteState.Ok), 0, 0, true, s_someone);

        Assert.Equal(SiteState.TakenOver, result.State);
        Assert.Equal(Owner, Assert.Single(result.TakenBy));
    }

    [Fact]
    public void Losing_only_some_of_a_method_counts_too()
    {
        Assert.Equal(SiteState.TakenOver, PatchEngine.Live(Evaluated(SiteState.Ok), 1, 1, true, s_someone).State);
    }

    [Fact]
    public void A_call_still_there_that_Monitor_left_alone_is_nobody_elses_doing()
    {
        Assert.Equal(SiteState.Refused, PatchEngine.Live(Evaluated(SiteState.Ok), 0, 2, true, s_nobody).State);
        Assert.Equal(SiteState.Refused, PatchEngine.Live(Evaluated(SiteState.Ok), 1, 2, true, s_nobody).State);
    }

    [Fact]
    public void Without_Monitors_own_patch_on_the_method_nobody_is_blamed()
    {
        // After a failed patch or a shutdown, a plugin with a transpiler there has taken nothing.
        Assert.Equal(SiteState.Unavailable, PatchEngine.Live(Evaluated(SiteState.Ok), 0, 0, false, s_someone).State);
    }

    [Fact]
    public void A_site_missing_with_nobody_to_blame_is_unavailable()
    {
        Assert.Equal(SiteState.Unavailable, PatchEngine.Live(Evaluated(SiteState.Ok), 0, 0, true, s_nobody).State);
    }

    [Fact]
    public void A_site_the_game_doesnt_have_isnt_blamed_on_anyone()
    {
        Assert.Equal(SiteState.Unavailable, PatchEngine.Live(Evaluated(SiteState.Unavailable), 0, 0, true, s_someone).State);
    }

    [Fact]
    public void Another_plugin_transpiling_a_method_takes_nothing_it_left_alone()
    {
        Assert.Equal(SiteState.Ok, PatchEngine.Live(Evaluated(SiteState.Ok), 2, 2, true, s_someone).State);
    }

    [Fact]
    public void A_note_says_how_many_calls_and_who()
    {
        string? note = PatchEngine.TakenOverNote(new[]
        {
            PatchEngine.Live(Evaluated(SiteState.Ok), 0, 0, true, s_someone),
            PatchEngine.Live(Evaluated(SiteState.Ok, 1), 0, 0, true, s_someone),
            PatchEngine.Live(Evaluated(SiteState.Ok), 2, 2, true, s_nobody),
        });

        Assert.NotNull(note);
        Assert.StartsWith("3 call sites", note, StringComparison.Ordinal);
        Assert.Contains(Owner, note, StringComparison.Ordinal);
    }

    [Fact]
    public void With_nothing_taken_there_is_no_note()
    {
        Assert.Null(PatchEngine.TakenOverNote(new[] { PatchEngine.Live(Evaluated(SiteState.Ok), 2, 2, true, s_nobody) }));
    }
}
