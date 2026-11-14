# Phase 3: detail data

The worker now totals what it measures for the "what is costing time" charts, names the mods and plugins behind it, and lets Quasar start runs.

**Tested on a test server.** Its tests pass, and it has run on a test server with no players. It hasn't run on a real server yet.

## Gate

Tests pass, including the group ceiling, and rollups read correctly on a quiet server. Neither has happened yet.

## Background totals

Every 30 seconds, while detail is on:

1. Gates close at a frame end, and two frames let stragglers finish.
2. The low priority thread totals every thread's tables, clears them, and writes the top 20 of each list to a ring. A list is grids, block types, definitions, owners, entity types, session components, game logic, callbacks or scripts.
3. Detail carries on. The tree never stops.

Detail misses a few frames in 1,800. Clearing also means the tables can't slowly fill up on a server that runs for weeks.

**Scaling.** In Grouped mode each site is measured for part of the time, so each update phase is scaled up by how many frames it was really measured for. Without this, a group measured a quarter of the time would read a quarter as high, and a grid's phases couldn't be added together.

**Lag.** At low sim speed a cycle of groups can outlast 30 seconds, so a sweep waits until every group has had a turn. Otherwise a whole group would drop out of the charts just when they matter.

**Runs.** A measuring run and a sweep never overlap. A run waits a few frames for a sweep to finish, and sweeps don't start during a run.

The rows travel in `history.pull` with a new `FromGroups` position, and a batch has a new section for them. `!monitor top <list>` shows the latest in chat. They're background numbers, so a lag-finding command is still what gives accurate ones.

## Composite game logic

An entity with several game logic components wraps them in `MyCompositeGameLogicComponent`, which used to show as one row. Each part is now timed on its own, and the wrapper isn't timed, so nothing counts twice. If those patches don't apply, the wrapper is timed as before.

## Mods and plugins

`!monitor mods` runs like the other lag-finding commands, and lists session components, game logic and callbacks by whose code they are: a mod through the game's `TypeToModMap`, a plugin through its assembly, or the game. `diagnose` includes it too. Background totals keep the same list over time since Phase 6, see [Phase6.md](Phase6.md).

**Limit:** some game callbacks can run game logic inside them, so the Game row can count that time twice. Mod and plugin rows aren't affected.

## Runs for Quasar

| Operation | Does |
|---|---|
| `run.start` | Starts a run, on the game thread. Takes seconds, whether to time clusters, and an optional grid |
| `run.result` | Says whether it's running, or returns the rows |

Runs are rare and their rows carry names, so these are plain JSON. The rows cover every list, including clusters, top 20 each. The worker keeps only the latest 8 results, so a result Quasar never collects costs nothing more than that.

## Commands

| Command | Shows |
|---|---|
| `!monitor mods` | Runs, then lists the costliest mods and plugins |
| `!monitor top <list>` | The latest background total. Lists are `grids`, `blocktypes`, `definitions`, `owners`, `entitytypes`, `session`, `logic`, `callbacks` and `scripts` |

## Changes from the plan

- **Tables are cleared each sweep,** instead of keeping rising counters and subtracting. The sums are exact, and the tables can't fill up.
- **Per-plugin `IPlugin.Update` rows weren't built here.** Magnetar calls plugins through its own loader, which isn't in the game DLLs, so those call sites can't be fingerprinted offline. Phase 6 added them.
- **Runs use JSON,** not the packed format, because they're rare and need names.

## Tests to run

Written and compiling, but not run: `SweepTests` covers totals, scaling, waiting for every group, clearing, pause, run and sweep taking turns, the trip to Quasar, and allocation.

These have no tests yet, and need a live game or a stand-in:

- `RunService`: `run.start` and `run.result`
- `!monitor top` and `!monitor mods`, and `CodeOwner`'s mod and plugin names
- The wrapper check in `LogicHooks`, and the seven composite call sites under real Harmony

Before relying on this phase, run the whole suite on both runtimes and on Linux, plus `Verify --apply`, `--commands` and `--physics`.
