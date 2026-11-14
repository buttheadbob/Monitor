# Phase 2: the tree as a product

The worker now keeps its own history and hands it to Quasar on request. Quasar's collector comes in Phase 4, so nothing charts this yet.

## Gate

The original gate was a 24-hour soak on a live server. That needs a server running for a day, so it's replaced with the same test run offline.

**Offline soak.** `SoakTests` pushes a full day through the real code in about a second: 5.2 million frames, 2,880 census passes and 2,880 history pulls, read back by the real Quasar-side reader.

| Check | Result |
|---|---|
| Every second received exactly once | 86,400 of 86,400, no gaps, no repeats |
| Worker allocation across the day, .NET 10 on Windows and Linux | 0 bytes, apart from one base64 string per pull |
| The same on .NET Framework 4.8 | At most 64 KB a day, see below |
| Memory growth after the first 6 hours | Under 256 KB over the next 18, on all platforms |

The original gate asked for no gen2 collections from Monitor. Counting them here would measure the reader and the test host, which aren't in the game process on a real server. Counting what the worker allocates is exact: if it allocates nothing, the GC has nothing to do on its account. The one exception, the base64 string, is short-lived and stays under the large object heap.

Memory is compared after the first stretch, because a process grows a little once at startup. A leak keeps climbing, and the limit catches even one 24-byte object a second, which would add 1.5 MB.

**One thing is unexplained.** On .NET Framework only, in about one run in four, the worker thread's allocation counter moves by up to 15 KB, once, at a set time into the run. No GC runs in that window, and tests of blocking and background GCs didn't reproduce it. The frame path has no time-based code, and per-frame work would show as megabytes, so this isn't a repeating cost. The limit of 64 KB still fails anything that repeats per pass or per pull.

## Pause handling

A server paused by `PauseGameWhenEmpty` can sit for hours. It now writes no rollup rows while paused, so an idle night can't push real history out of the one-hour ring. The first row afterwards is marked `AfterGap`, so charts show a gap and don't draw a line across it. No census runs while paused either.

## World census

Every 30 seconds, on the game thread, in slices of at most 30 µs per frame. Grids carry their own block and PCU totals, so a pass walks grids and not blocks.

| Row, every pass, 8 hours kept | Latest only |
|---|---|
| Entities, grids, blocks, PCU | Players: identity, faction, ping |
| Characters, floating objects, voxels | Factions: grids, blocks, PCU, members online |
| Players online, faction count | |
| Mean and worst ping | |
| What the pass itself cost | |

`!monitor status` shows the latest row.

Ping comes from the game, because Monitor can't measure it. The server figure is the mean of player pings, in tenths of a millisecond, so it can show 21.3 like the old gauge. A player with no ping yet is left out of the mean.

If the census fails, it stops and logs why. Everything else carries on.

## History pull

`history.pull` asks for everything since a point:

```json
{ "FromRollup": 0, "FromCensus": 0, "FromDetail": 0 }
```

The answer is a base64 JSON string, which Quasar's `SendAsync<HistoryPullRequest, byte[]>` reads straight into bytes. `HistoryReader.Read` in Contracts decodes it, and the batch says where to pull from next.

- **Off the game thread.** It only reads Monitor's rings, so a pull can't stall the game.
- **Nothing lost.** Quasar can be away for an hour. If it's away longer, the batch's first sequence shows the gap.
- **Limited size.** A batch is at most 16 KB, so it stays under the large object heap through every JSON layer. A backlog comes in pieces.
- **Restarts show.** Each batch carries the server's start time. When that changes, sequences have restarted.
- **Live view.** The same call at 1 Hz gives one new row each time.

Timings go first in a batch. Census rows and players follow if there's room, and otherwise come with the next pull.

## Changes from the plan

- **The gate is offline,** as above.
- **Concealment wasn't in the census at this point,** and has been since Phase 7, which reads the flag it sets. See [Phase7.md](Phase7.md). The Blocks vs Conceal chart planned to read that plugin's published stats, and it doesn't publish any. Guessing from entity flags would be a wrong number on a chart, so the column is left out until Concealment publishes a count through `PluginStats`.
- **Names aren't sent.** Rows hold ids, and Quasar looks up names in Phase 4, as planned.
- **The tree doesn't collapse on a slow clock.** Even at 1 µs a read, about fifty times what was measured here, the tree costs about 80 µs a frame, which isn't worth an extra branch in every scope. `!monitor status` reports a slow clock.

## Not proven yet

No game session has run this. The walker that reads real grids, players and pings compiles against the game, but only a live server can exercise it. The census logic around it is tested with a stand-in world.
