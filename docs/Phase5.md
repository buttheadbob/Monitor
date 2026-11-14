# Phase 5: Incidents and polish

Monitor now notices lag by itself and keeps the evidence, learns what's normal for each server, compares stretches of history, shows what other plugins patch and what that costs, and gives other Quasar plugins a public API.

**Tested on a test server.** Its tests pass, and it has run on a test server with no players. It hasn't run on a real server yet, and the patch audit, which rebuilds methods on a running server, is the part most worth watching there.

## Noticing lag

Each second, the worker checks the row it just wrote. That costs a few comparisons and allocates nothing, so it can't add to lag or go dark during it.

| Trips on | Default |
|---|---|
| Sim speed under a limit | 0.90, for 5 seconds in a row |
| Frame p95 over a limit, for the same seconds | Off |
| One frame over a limit | 1000 ms, except right after a save, or within 15 seconds of a character spawning |

- **After a trip,** nothing else can trip for 15 minutes, so lag can't keep setting off runs.
- **It stays quiet** for the first 30 seconds after the game is ready, while paused, and during any run, because a run's own timing isn't lag.
- **Sustained lag gets a run.** It lasts 5 seconds, because timing every call adds cost when the server is already struggling. Setting it to 0 gathers evidence without a run.
- **A spike gets no run,** because it's over before one could start. Its evidence is the frames and calls around it.
- **A spike just after a character spawns is a join hitch, not lag.** The first character in the world after a start makes the game read every voxel map file on the game thread, for the asteroids around it (`MyCompositeShapes` loading each storage through `MyStorageBase.LoadFromFile`). In container tests that held one frame for 3-10 s, and the trip came at most 11 s after the spawn. It's recorded as a "Join hitch" marker with its length, and sets off no alert or run and uses no cooldown. `JoinGraceSeconds` sets the window, 15 by default, up to 120, and 0 turns it off. Low sim speed and frame time still count during it.
- **Clusters aren't timed by default.** It changes physics scheduling with nobody watching, so it's a setting.

Limits are on the Incidents tab, needing `CanEditConfigs`, and apply to every server. Quasar sends them when a server starts and when they change. A worker with no Quasar uses the defaults. `!monitor trigger on|off` turns it off for one server until its next start.

## Evidence

A trip sends a marker through history straight away, and the worker keeps the evidence until it's collected, up to its last 8 reports.

- **Worst frames,** the 20 slowest of the 30 seconds or so before the trip, with where each frame's time went and any garbage collection. These come from before the run, because timing every call makes frames slower.
- **Slowest single calls,** with their thread and what they were. The slow call ring existed from Phase 1, and nothing read it until now. This also adds a table to every run's results.
- **The run,** as any other run's results.

## Incidents

Quasar turns a trip into an incident, on the new Incidents tab.

- **It opens at the trip.** The page shows a notice while one is open.
- **It closes after 60 healthy seconds in a row,** timed from the first of them, so a brief recovery in the middle of lag doesn't split it in two.
- **Lag that outlasts the cooldown trips again,** and that evidence joins the same incident, up to 32 trips. Past that, a trip adds nothing to the incident and its run isn't collected, which takes at least 32 minutes of unbroken lag.
- **A server restart closes it.**
- **It records** how long it lasted, the lowest sim speed and the worst frame, what looked unusual against normal when it began, and a chart of the frame from 5 minutes either side.

Incidents are kept for the retention setting, and an open one survives Quasar restarting.

## Baselines

Monitor learns what's normal for each server, for each hour of the day, because a full server at peak isn't a problem and the same load at 4am might be.

- **Normal is the middle value of that hour's minutes,** so past lag can't drag it up.
- **It takes 90 minutes of that hour, across two days,** before it says anything. Until then it's still learning.
- **A part is unusual** when the last 5 minutes are a quarter over that hour's usual high, and at least 1 ms over normal. So a tiny part doubling doesn't count.

The Overview tab says what's unusual, and each incident records it. It's worked out in the background and reused for an hour, so asking never waits for files.

## Window compare

The Compare tab sets two stretches of history side by side: sim speed, frame time, available time, the parts of the frame, and the top of each list by what changed most. Picking an event fills in the stretches either side of it. These are background numbers, so they're for trends. A run settles a cause.

## Patch audit

The Patches tab lists every game method another plugin patched, by plugin and by method. Monitor's own patches aren't listed. That only reads Harmony's records, and costs nothing until it's opened.

An audit measures up to 8 chosen methods for up to 120 seconds, and needs `CanControlServers`. Monitor adds its own prefix and postfix either side of everyone else's, which splits each call into plugin prefixes, the original and plugin postfixes.

- **Adding them makes Harmony rebuild the method,** which takes a moment on the game thread. So they go in and come out one at a time, half a second apart, and only when someone asks. Monitor shutting down removes any left in.
- **A method with several plugins** shows their time together, because Monitor can't tell one plugin's prefix from another's.
- **A transpiler's changes** are inside the original, so they can't be split out. Those methods are flagged.

## Entities page

- **An ms/f column,** from the latest background total. A dash means the grid wasn't in that total's top 20.
- **A Profile button** on grids, needing `CanControlServers`. It times every call on that grid for 10 seconds, and the result goes on the Runs tab.

## Command

`--block=TYPE` limits a run to blocks whose class name contains that text, on any lag-finding command. Your old `grids --block` did the same. All of a run's lists are limited, not only grids, and a name matching no block class is refused, so a typo can't quietly report nothing.

## Public API

Other Quasar plugins can read Monitor's data and ask for runs. See [PublicApi.md](PublicApi.md).

## Changes from the plan

- **The flight ring isn't frozen.** The window is still in it when the run ends, so the worst frames are read then. Sending every frame would have needed a new batch format and many pulls for what 20 frames show.
- **Spikes are a third trigger,** and don't start a run.
- **Triggered runs are 5 seconds.** A short run at the worst moment was a better trade than a long one.
- **Baselines use the middle value, not an average,** so they don't need incidents cut out of them.
- **Plugins are limited to a run a minute,** counted from the end of the last one, and a lag trip counts. They don't share the 15 minute cooldown, because that would make the API useless, and an API run doesn't delay a real trip.
- **History rows gained ids, and run rows gained calls per frame,** so both give the same reply through the API.
- **File reads moved outside `HistoryStore`'s lock,** so a long range can't hold up collection or another page.
- **Charts can show a fixed stretch of the past,** which incidents use.

## Not built

- **Blocks vs Conceal,** built in Phase 7 from a flag Concealment already sets. See [Phase7.md](Phase7.md).
- **The Profiler's `custom`.**

Everything else this list held at the time is in [Phase6.md](Phase6.md).

## Tests to run

Written and compiling, but not run:

- `LagWatchTests`, `SlowestCallsTests`, `MarkerTests`, and the block filter, slow call and option tests added to `CaptureTests`, in `tests/SenX.Monitor.Core.Tests`.
- `PatchAuditTests` in `tests/SenX.Monitor.Magnetar.Tests`, through real Harmony.
- `IncidentTests`, `BaselineTests`, `HistoryWindowsTests`, `PublicApiTests`, and the additions to `MonitorCollectorTests`, in `tests/SenX.Monitor.Ui.Tests`.

`IncidentService` and `ServerInventory` have no tests, because they need the game.

In the preview host, lag comes every couple of minutes, so an incident should open, gather a run and close. Check the Incidents, Compare and Patches tabs in light and dark.

These need a real server:

- A lag trip gathers its evidence, and worst frames name the right parts.
- Slow calls have real names.
- An audit on a busy method, watching the frame time while shims go in and come out.
- The ms/f column and Profile button on Quasar's Entities page, including on a cluster.
- `samples/ApiConsumer` through a real host, with Monitor installed, disabled and removed.

## When another plugin changes the same calls

Monitor times calls where they're made, so if another plugin's transpiler rewrites one of those calls first, there's nothing left for Monitor to find. Monitor runs last on purpose, because that's what keeps other plugins' own checks working, so it adapts rather than competing.

A method is rebuilt whenever anyone patches it, and each rebuild recounts its sites, so what's reported is what's really in place, in whichever order plugins loaded. A site that's in the game's code but has gone from what Monitor's patch was handed, in a method another plugin transpiles, reads as **taken over**. Every transpiler on that method is named, because one of them did it and only trying each in turn could say which. If Monitor's own patch isn't on the method, or the call is still there and Monitor left it alone, nobody's blamed:

- `!monitor health` lists each one.
- The log says so once, after the 30 second warm-up.
- The Patches tab lists them on Quasar and Torch.
- Every run report carries a warning, because costs from those calls read low or empty. So does copied report text, and the API's `MonitorRunDetails.LostSites`.

The game isn't affected, and the tree and everything else keep working.

This reports what the last rebuild of a method did. If another plugin's rebuild fails partway, on Torch the method can end up with nothing patched while the count still reads as it was. A plugin that removes Monitor's additions after Monitor has run isn't seen either, because Monitor runs last and has nothing after it to check.

**Cluster Node does this today.** It rewrites 14 entity update calls in `MyParallelEntityUpdateOrchestrator` to charge its load balancer, so on a server with it installed, those calls can't be timed by Monitor. Monitor doesn't reach into Cluster Node's wrappers, because they're private and change often, and a hook into them would fail just as quietly. A proper way to share that timing would be a change on Cluster Node's side, once it has settled.
