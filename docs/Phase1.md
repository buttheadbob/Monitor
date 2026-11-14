# Phase 1: the worker in the game

Real load isn't needed to finish this phase. Monitor reports its own cost all the time, so numbers under real load arrive from normal use and don't block development.

## Gate

1. **Offline proof.** Done.
2. **Quiet-server smoke test.** Any Magnetar server, any world, no players, a few minutes.

## Offline proof

| Check | Result |
|---|---|
| All targets found in the game DLLs | 72 of 72 sites across 43 methods, at this phase. Now 84 across 54 |
| Real Harmony patch and compile, then clean removal | All 72, in about 650–800 ms |
| Core tests on .NET 10 and .NET Framework 4.8, including Phase 2 | All, also in shuffled orders |
| Core tests on Linux (.NET 10 in Docker) | All |
| Rewriter tests with real Harmony on both runtimes | 22 of 22 |
| Chat commands through the real SDK dispatcher, with full runs | All passed |
| Cluster timing on real Havok, with the game's real `MyPhysics` code and Monitor's real patches | All passed, see below |
| Deliberate breaks in run logic, timing and cluster stepping | All caught |

The physics check runs with no server: the game's own stepping code, Monitor's patches and hooks, and the real `Havok.dll`, with worlds of real rigid bodies. Results:

| Check | Result |
|---|---|
| Physics with hooks closed, against unpatched | 0.000000 m difference |
| Physics with clusters taking turns, against unpatched | 0.000000 m difference |
| Clusters of 700, 300, 100 and 10 boxes | Ranked in that order: about 3.4, 1.5, 0.5 and 0.13 ms |
| Time spent stepping alone | 0.833 ms/f, against a budget of 0.833 ms/f |
| Optimizer state after a timed step | Restored exactly, 20 of 20, including steps that would have tripped it |
| Another plugin patching any of the four methods | Refused, and the plugin is named |
| Game code differing from the recorded fingerprint, or a hook missing | Refused |

An earlier probe compared schedules in the same way. Going fully sequential, as the Torch Profiler does, took 57% longer per frame, against 10% with one world stepping alone per frame. Both gave identical physics.

The physics check runs on Windows only, so on Linux the turn logic is tested but Havok itself is not. These offline checks have no game session. They cover routing, options, mode changes, report layout and physics stepping, but not live game data. Entity callbacks during a step, grid, player and faction names, `--this`, `--gps`, `--takeme` and numbers from real load are only proven by the smoke test.

Those runs used the game DLLs of one Dedicated Server install. If a server uses a different game build, the check below shows it:

```bash
tools/SenX.Monitor.Verify/bin/Release/net48/SenX.Monitor.Verify.exe "PATH/TO/DedicatedServer64"
```

A `Drifted` line means that build differs from the recorded fingerprints. It's still safe to run, but clusters won't be timed until the two physics methods are checked and `--write-lock` regenerates the fingerprints. Add `--commands` for the command check, or `--physics` for the Havok check.

## Smoke test

1. Build into Quasar's Plugins folder:

   ```powershell
   ./tools/Package-Local.ps1 -QuasarInstall <QuasarRoot>/QuasarApp -Destination <QuasarRoot>/Quasar/Plugins/senx.monitor
   ```

2. Restart Quasar, then enable **Monitor** at `/settings/ui-plugins`. Enabling takes effect on the next restart.
3. Start a server. Quasar deploys the worker into its Magnetar `Local` folder. `PauseGameWhenEmpty` must be off, or an empty server won't simulate.
4. After a few minutes, read the Magnetar log. `!monitor status` is written there once a minute.

It passes if:

- The server starts and stays up.
- `!monitor health` shows all sites patched, with no errors.
- `!monitor status` shows a sane tree: children never sum to more than their parent.
- `!monitor sites` shows all four groups measured.
- `!monitor diagnose` replies after 10 seconds, and `!monitor status` shows the mode back at Grouped afterwards.

To remove it, disable it in the same page and restart Quasar.

## How detail works

Detail is the per-call timing below the tree. The tree itself always runs.

- **Background: grouped.** One related group is measured at a time, for cheap numbers all the time. Groups are measured at different moments, so compare them by share of the frame. If a group would cost too much, it is sampled on its next turn, and `!monitor sites` shows that.
- **Finding lag: automatic.** A lag-finding command switches detail to Everything by itself, times every call for its run, then puts back the mode that was set before. Every number in the reply comes from the same frames.
- **Full time: admin's choice.** `!monitor everything on` holds it on until `!monitor everything off`. The setting lasts until the server restarts.

During a run, background group turns stop, and a mode change takes effect when the run ends.

## Commands

Lag-finding commands run for 10 seconds unless told otherwise. One runs at a time, and the reply follows when it finishes. They need Admin.

| Command | Lists |
|---|---|
| `!monitor diagnose` | The frame, then the top of every list below, including clusters |
| `!monitor physics` | Physics stages, then the costliest clusters |
| `!monitor grids` | Costliest grids |
| `!monitor blocktypes` | Block classes |
| `!monitor blocks` | Block definitions |
| `!monitor players` | Owners |
| `!monitor factions` | Factions |
| `!monitor scripts` | Programmable blocks |
| `!monitor session` | Session components |
| `!monitor entities` | Entity types, with all blocks as one row |
| `!monitor logic` | Game logic components |
| `!monitor callbacks` | Grid and deferred callbacks |
| `!monitor rpcs` | RPCs received and the biggest sent, added in Phase 7 |
| `!monitor mods` | Mods and plugins, by whose code it is |

Replies are laid out for whoever asked: game chat for a player online, and a Discord layout for anyone else, because bots run commands as the console. See [Phase7.md](Phase7.md).

Limiting a run works on `grids`, `blocktypes`, `blocks`, `players`, `factions` and `scripts`. Other lists aren't tied to a grid or an owner, so they refuse those options. See [Phase6.md](Phase6.md), which also has the commands for players.

Options:

| Option | Does |
|---|---|
| `--secs=N` | How long to measure, 1 to 600 |
| `--top=N` | How many rows, 1 to 50 |
| `--grid=ID` or `--entity=ID` | Only this grid and its blocks |
| `--this` | Only the grid you're controlling |
| `--player=NAME` | Only this owner, by name, identity id or `sid/STEAMID` |
| `--faction=TAG` | Only this faction's members, by tag, name or faction id |
| `--noblocks` | Grids' own updates, leaving out their blocks |
| `--block=TYPE` | Only blocks whose class name contains this, such as `--block=Drill` |
| `--details` | Adds owners and factions to grid rows |
| `--gps` | Sends you a GPS marker per grid, with `!monitor grids` |

After `!monitor physics`, these work on the clusters it listed, without measuring again:

| Option | Does |
|---|---|
| `--inspect=N` | Lists the grids in cluster N, by faction and owner |
| `--takeme=N` | Moves your character to cluster N and marks its grids with GPS. You can't be seated |
| `--takeme=done` | Removes those markers |

These answer at once from history, because the tree is always on. Each takes `--secs=N`, up to 3600.

| Command | Shows |
|---|---|
| `!monitor sim` | Best, worst and average sim speed |
| `!monitor frames` | Where frame time went |
| `!monitor net` | What's crossing the network and who for, added in Phase 7 |

Everything else:

| Command | Shows |
|---|---|
| `!monitor health` | What Monitor patched, how long it took, and anything drifted or skipped |
| `!monitor patches` | What other plugins patch, changed in Phase 6 |
| `!monitor top <list>` | The latest background total for a list, including `mods`, with no run |
| `!monitor drill <name>` | Times everything a method calls, added in Phase 6. See [Phase6.md](Phase6.md) |
| `!monitor status` | Sim speed, frame percentiles, GC, Monitor's own cost, top of the tree |
| `!monitor sites` | Background numbers per group and site: calls, main and worker time, share of the frame |
| `!monitor cleangps` | Removes the GPS markers Monitor sent you |
| `!monitor everything on` or `off` | Admin. Holds every call timed full time, or goes back to grouped |
| `!monitor detail grouped`, `everything` or `off` | Admin. Sets background detail directly. Off stops background timing, and lag-finding commands still work |

## How clusters are timed

All clusters normally step together on one job queue, so nothing can be attributed to any single one. During `physics` and `diagnose`, a few clusters step alone first, using the game's own `StepSingleWorld`. The rest then step in parallel as usual, skipping those through the game's own `IsClusterActive` check. Each cluster still steps exactly once per frame.

- **Spread out, not a burst.** Turns rotate through the clusters and spend at most 5% of a frame on average, about 0.83 ms/f, however many clusters there are. A heavy cluster uses up the budget, so frames after it stay normal.
- **What a time means.** A cluster's time is with every physics thread to itself. That ranks clusters fairly, but the times add up to more than normal physics, where clusters overlap.
- **Sleeping clusters stay asleep.** Turns use the same activity check as the game's parallel path. The Profiler's sequential path didn't have that check.
- **The game's optimizer isn't misled.** It times each physics step and can turn off collision checks on grids if steps look slow. A step that ran slower because Monitor timed it is taken back out.
- **It refuses when it can't be sure.** Skipping relies on the parallel path being the game's own, or a cluster could step twice. So cluster timing stays off if these methods differ from the verified build, or if another plugin could change what they do. A plugin that only watches them is fine: that's a void prefix or a postfix, and Quasar's agent times `StepWorldsInternal` that way on every managed server. The rest of the run carries on, and the reply says why.

Grid updates aren't affected, so `grids` and the other lists don't time clusters. Everything held on doesn't either, because stepping alone changes how physics is scheduled.

## How the numbers are kept honest

- **Clock overhead is removed.** Monitor measures its own clock on each server and takes that out of every call's time, so its own cost doesn't read as game time.
- **No time is lost to rounding.** Running sums keep raw clock ticks and convert when read, so Linux's 1 ns ticks aren't rounded to 100 ns on every call.
- **Main thread and workers stay apart.** Worker time is summed across threads, so it can exceed frame time. Main thread time is what holds up a frame.
- **Each figure has its frame context.** Share of frame uses how long frames took during that same measurement.
- **Nested time isn't counted twice.** Scripts run inside their block's update, so they're left out of grid and block totals and listed separately. Callbacks and game logic have their own lists for the same reason.
- **Runs show their own cost.** Each reply gives what timing added, and warns if a table filled up or threads went uncounted.
- **Runs start clean.** Tables are cleared first, and the run's gates close before anything is totalled, so a total covers exactly those frames.

Detail starts after a 30 second warm-up.

## Known gaps in this build

These are on purpose, and come in later phases:

- The Profiler's `custom` isn't built. `drill` answers the same question without the author's help, see [Phase6.md](Phase6.md).
- `--tics=N` is gone from `physics`. Use `--secs=N`, because clusters are timed across the whole run.
- Reporting is plain text. Charts arrive in Phase 4.
- Time inside `MyCompositeGameLogicComponent` shows as that one type, not each inner component.
- `Plugins` in the tree is one total. `mods` shows each plugin's own Update time, added in Phase 6.
- There's no autosave scope, so saves appear as Session self time. Each save's marker says how long its frame took.
- No census, background group rollups or automatic triggers yet. Phases 2, 3 and 5 added these.
- The `everything` setting isn't saved, so a restart goes back to grouped.

## Changes from the plan

- **Detail runs by group in the background, and everything on demand.** This replaced budget-driven focus and sampling, which were more complex and gave estimates. Sampling remains only as the guard for a background group that would cost too much. Lag-finding commands never sample.
- **Lag-finding commands arrived in this phase,** not Phase 3, along with the totals by grid, type, definition and owner they need. Background totals for charts are still Phase 3.
- **Tree scopes bracket the call rather than substitute it.** A bracket is stack-neutral and needs no helper per target. Labels and exception blocks move with the call, and prefixes stay attached. Detail sites still substitute.
- **Fingerprints are generated C#, not a JSON file,** so the worker needs no parser.
