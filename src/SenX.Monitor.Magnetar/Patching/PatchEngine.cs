using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SenX.Monitor.Contracts;
using SenX.Monitor.Core;

namespace SenX.Monitor.Magnetar.Patching;

internal enum SiteState
{
    /// <summary>Found as expected, in a method that matches the known fingerprint.</summary>
    Ok,

    /// <summary>Found, but the method changed. Still safe, though the tree shape may be stale.</summary>
    Drifted,

    /// <summary>Not found, or not patchable. Skipped, and everything else keeps running.</summary>
    Unavailable,

    /// <summary>In the game's code, but gone from a method another plugin transpiles, so it can't be timed here.</summary>
    TakenOver,

    /// <summary>Still there, but Monitor left it alone because its shape wasn't one that could be rewritten safely.</summary>
    Refused,
}

/// <summary>What happened to one call target.</summary>
internal sealed class SiteResult
{
    public SiteResult(string caller, CallTarget target, SiteState state, int found, int patched, string note, MethodBase? method = null, IReadOnlyList<string>? takenBy = null)
    {
        Method = method;
        TakenBy = takenBy ?? Array.Empty<string>();
        Caller = caller;
        Target = target;
        State = state;
        Found = found;
        Patched = patched;
        Note = note;
    }

    public string Caller { get; }

    public CallTarget Target { get; }

    public SiteState State { get; }

    public int Found { get; }

    public int Patched { get; }

    public string Note { get; }

    /// <summary>The method the site is in.</summary>
    public MethodBase? Method { get; }

    /// <summary>Who changed a site that's <see cref="SiteState.TakenOver"/>.</summary>
    public IReadOnlyList<string> TakenBy { get; }
}

/// <summary>
/// Applies the table. Every step is contained: a method that is missing, changed or refuses to
/// patch costs its own scopes and nothing else. A monitor must never stop a server.
/// </summary>
internal static partial class PatchEngine
{
    private static int s_pluginsHooked;

    private static readonly Dictionary<MethodBase, CallerSpec> s_active = new();
    private static readonly Dictionary<CallTarget, int> s_rewritten = new();
    private static readonly List<SiteResult> s_evaluated = new();
    private static readonly object s_countsGate = new();
    private static readonly Dictionary<CallTarget, int> s_matched = new();
    private static readonly HashSet<MethodBase> s_hooked = new();

    /// <summary>
    /// Each site as it stands now. A method is rebuilt whenever anyone patches it, and that recounts its sites, so
    /// this holds whichever order plugins loaded in. Worked out when asked, so keep it off hot paths.
    /// </summary>
    public static IReadOnlyList<SiteResult> Results
    {
        get
        {
            List<SiteResult> evaluated;
            Dictionary<CallTarget, int> counts;
            Dictionary<CallTarget, int> matched;
            HashSet<MethodBase> hooked;

            lock (s_countsGate)
            {
                evaluated = s_evaluated.ToList();
                counts = new Dictionary<CallTarget, int>(s_rewritten);
                matched = new Dictionary<CallTarget, int>(s_matched);
                hooked = new HashSet<MethodBase>(s_hooked);
            }

            // Outside the lock. Patching holds Harmony's own lock and then comes here through the transpiler,
            // so asking Harmony who patched what while holding this one could leave the two waiting on each other.
            return evaluated.Select(result =>
            {
                counts.TryGetValue(result.Target, out int patched);
                matched.TryGetValue(result.Target, out int found);

                bool isHooked = result.Method != null && hooked.Contains(result.Method);
                bool blame = isHooked && found < result.Target.Expected && patched < result.Target.Expected;

                return Live(result, patched, found, isHooked, blame ? OthersChanging(result.Method) : Array.Empty<string>());
            }).ToList();
        }
    }

    public static double LoadMilliseconds { get; private set; }

    public static int PatchedMethods { get; private set; }

    /// <summary>Checks the table against the current game without changing anything.</summary>
    public static List<SiteResult> Evaluate(IEnumerable<CallerSpec> table)
    {
        var results = new List<SiteResult>();

        foreach (CallerSpec caller in table)
        {
            bool matches = FingerprintMatches(caller, out string note);

            foreach (CallTarget target in caller.Targets)
            {
                int found = CountSites(caller, target);
                results.Add(new SiteResult(caller.Id, target, Classify(found, target.Expected, matches), found, 0, note, caller.Method));
            }
        }

        return results;
    }

    private static void RecordResults(IEnumerable<SiteResult> evaluated)
    {
        lock (s_countsGate)
            s_evaluated.AddRange(evaluated);
    }

    /// <summary>
    /// A site as it stands, given how the table read the game's own code. Sites only count as there if they were
    /// really rewritten. Nothing's blamed on anyone unless Monitor's own patch is on the method and the call
    /// has really gone from what it was given, because a plugin transpiling a method for its own reasons took nothing.
    /// </summary>
    internal static SiteResult Live(SiteResult evaluated, int patched, int matched, bool hooked, IReadOnlyList<string> others)
    {
        int expected = evaluated.Target.Expected;
        SiteState state = evaluated.State;

        if (patched < expected && state != SiteState.Unavailable)
        {
            if (!hooked)
                state = SiteState.Unavailable;
            else if (matched >= expected)
                state = SiteState.Refused;
            else
                state = others.Count > 0 ? SiteState.TakenOver : SiteState.Unavailable;
        }

        return new SiteResult(evaluated.Caller, evaluated.Target, state, evaluated.Found, patched, evaluated.Note, evaluated.Method, others);
    }

    /// <summary>
    /// Keeps what a rebuild of a method found. Each one starts from the method's original code, so its count
    /// replaces the last. Added up, a method rebuilt twice would read as twice the sites it has.
    /// </summary>
    internal static void NoteRewritten(CallerSpec caller, Dictionary<CallTarget, int> counts, Dictionary<CallTarget, int> matched)
    {
        lock (s_countsGate)
        {
            if (caller.Method != null)
                s_hooked.Add(caller.Method);

            foreach (CallTarget target in caller.Targets)
            {
                s_rewritten[target] = counts.TryGetValue(target, out int count) ? count : 0;
                s_matched[target] = matched.TryGetValue(target, out int found) ? found : 0;
            }
        }
    }

    private static void Forget(CallerSpec caller)
    {
        lock (s_countsGate)
        {
            if (caller.Method != null)
                s_hooked.Remove(caller.Method);

            foreach (CallTarget target in caller.Targets)
            {
                s_rewritten.Remove(target);
                s_matched.Remove(target);
            }
        }
    }

    private static void ForgetAll()
    {
        lock (s_countsGate)
        {
            s_hooked.Clear();
            s_rewritten.Clear();
            s_matched.Clear();
        }
    }

    // Who has a transpiler on this method. One of them changed the site, but only trying each in turn could say which.
    private static IReadOnlyList<string> OthersChanging(MethodBase? method)
    {
        if (method == null)
            return Array.Empty<string>();

        try
        {
            return PatchList.Rows(method).Where(row => row.Kind == PatchKinds.Transpiler).Select(row => row.Owner).Distinct().ToList();
        }
        catch (Exception exception)
        {
            // This can be reached from inside the game's frame, and a name isn't worth losing a run over.
            MonitorLog.Warning("Monitor could not read which plugins patched " + method.Name + ": " + exception.Message);
            return Array.Empty<string>();
        }
    }

    /// <summary>Why some of what's measured here reads low, or null when nothing's been taken over.</summary>
    public static string? TakenOverNote() => TakenOverNote(Results);

    internal static string? TakenOverNote(IReadOnlyList<SiteResult> results)
    {
        List<SiteResult> lost = results.Where(result => result.State == SiteState.TakenOver).ToList();

        if (lost.Count == 0)
            return null;

        int sites = lost.Sum(result => result.Target.Expected - result.Patched);
        string owners = string.Join(", ", lost.SelectMany(result => result.TakenBy).Distinct());

        return FormattableString.Invariant($"{sites} call sites can't be timed, because they're in methods another plugin rewrote ({owners}), so what runs there reads low or empty");
    }

    /// <summary>True when both cluster hooks are fully in place on code matching the recorded fingerprints.</summary>
    internal static bool ClusterSitesVerified(IEnumerable<SiteResult> results)
    {
        List<SiteResult> sites = results.Where(result => result.Target.Scope == Site.PhysicsCluster).ToList();

        return sites.Count == 2 && sites.All(result => result.State == SiteState.Ok && result.Patched == result.Target.Expected);
    }

    /// <summary>The detail sites that were really patched. Nothing else can ever open.</summary>
    public static int[] PatchedSites()
    {
        lock (s_countsGate)
        {
            return s_evaluated
                .Where(result => result.Target.Kind == CallSiteKind.Substitute && s_rewritten.TryGetValue(result.Target, out int patched) && patched > 0)
                .Select(result => result.Target.Scope)
                .Concat(s_pluginsHooked > 0 ? new[] { Site.PluginUpdate } : Array.Empty<int>())
                .Distinct()
                .ToArray();
        }
    }

    private static int CountSites(CallerSpec caller, CallTarget target)
    {
        if (caller.Method == null)
            return 0;

        return ReadIl(caller.Method).Count(step => CallMatch.Matches(step.OpCode, step.Operand, target));
    }

    private static bool FingerprintMatches(CallerSpec caller, out string note)
    {
        note = string.Empty;

        if (caller.Method == null)
        {
            note = "method not found";
            return false;
        }

        if (!Fingerprints.Known.TryGetValue(caller.Id, out string? expected))
        {
            note = "no fingerprint on record";
            return false;
        }

        string actual = IlFingerprint.Compute(caller.Method);
        if (actual == expected)
            return true;

        note = $"fingerprint {actual}, expected {expected}";
        return false;
    }

    private static SiteState Classify(int found, int expected, bool matches)
    {
        if (found == 0 || found != expected)
            return SiteState.Unavailable;

        return matches ? SiteState.Ok : SiteState.Drifted;
    }
}
