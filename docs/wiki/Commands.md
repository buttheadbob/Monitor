# Commands

All commands start with `!monitor`. You can type them in the game chat, in the server console, or through a Discord bot that passes commands to the server.

The example replies on this page come from Monitor's own reply code. The names and numbers in them are made up.

## Who can use them

- Players can use `howto`, `mine` and `test`.
- Only admins can use the other commands.
- `mine` and `test` must come from a player in the game. The console and bots cannot use them.

## How replies look

Monitor sends one finished reply for each command. A long reply comes in several pages.

- **In the game,** each row shows its name, with the numbers on the next line. The game chat font cannot line up columns.
- **In Discord and in the console,** each row shows the numbers first, then the name. The numbers line up in columns.

The same reply in both layouts:

```text
Background grids
From 5430 s after start. A run gives accurate numbers.
Iron Hauler
   Main 1.84  Work 0.62  Peak 6.10  On M+W
Outpost Kappa
   Main 1.20  Work 0.03  Peak 3.40  On M
```

```text
### Background grids
*From 5430 s after start. A run gives accurate numbers.*
> `Main  Work  Peak   On` Name
> `1.84  0.62  6.10  M+W` Iron Hauler
> `1.20  0.03  3.40    M` Outpost Kappa
```

In Discord, `###`, `**` and the backticks become headings, bold text and code. Names that players typed are made safe: a grid called `@everyone` cannot ping anyone.

## The columns

| Column | Meaning |
|---|---|
| Main | Time on the game thread, in ms per frame. Only this time can slow a frame. |
| Frame | Main as a share of the average frame. |
| Work | Time on worker threads, in ms per frame, added up over all workers. It can be more than a whole frame. |
| Peak | The slowest single call, in ms. |
| On | Where the time went: `M` the game thread, `W` worker threads, `M+W` both, `-` no time recorded. See [Thread tracking](Thread-tracking). |
| Calls/f | Calls per frame. |

---

## Player commands

### `!monitor howto`

A short help text about Monitor and what its numbers mean. It costs nothing.

### `!monitor mine`

Each of your grids, from the latest background total. It costs the server nothing.

```text
!monitor mine
```

In the game:

```text
Your grids
Estimates from the latest background total. !monitor test measures one properly.
Drill Rig 7
   Main 0.05  Frame 0.4%  Work 0.91  Rank 3 of 1240  On M+W
Small Scout
   Main 0.02  Frame 0.1%  Work 0.00  Rank 211 of 1240  On M
All of them 0.07 ms/f on the main thread
Share of frame 0.5%, Workers 0.91 ms/f
```

- **Rank** is where the grid stands among all grids. "3 of 1240" means only 2 grids cost more.
- It shows only your own grids, and it never names other players.
- If none of your grids did any work in the last 30 seconds, the reply is "None of your grids were in the latest total. A grid that does nothing costs nothing."

### `!monitor test`

Times one of your grids, with every call timed. Examples:

| You type | Monitor tests |
|---|---|
| `!monitor test` | The grid you sit in. You must sit at its controls. |
| `!monitor test Drill Rig 7` | Your grid with that name. |

First it replies "Measuring 'Drill Rig 7' for 5 s." Then, 5 seconds later:

```text
'Drill Rig 7'
Measured 5.0 s, Server sim 0.99
This grid
   Main 0.06  Frame 0.4%  Work 0.94  Peak 1.32  On M+W
-- Block types --
MyShipDrill
   Main 0.01  Frame 0.1%  Work 0.71  Peak 0.40  On W
MyCargoContainer
   Main 0.03  Frame 0.2%  Work 0.12  Peak 0.22  On M+W
-- Blocks --
Large Drill
   Main 0.01  Frame 0.1%  Work 0.71  Peak 0.40  On W
Large Cargo Container
   Main 0.03  Frame 0.2%  Work 0.12  Peak 0.22  On M+W
-- Scripts --
'PB Drill Control' in 'Drill Rig 7'
   Main 0.02  Frame 0.1%  Work 0.00  Peak 0.31  On M
Main and Work are ms/f, Peak is ms. On is M for the game thread, W for workers, M+W for both, and - where no time was recorded.
```

A test costs the whole server, so it has limits:

| Limit | Default | Reply when it stops you |
|---|---|---|
| How long a test runs | 5 s | |
| Time between two tests by the same player | 10 minutes | "You can test again in N s." |
| Time between any two tests on the server | 1 minute after the last one ends | "Someone else just ran a test. Try again in N s." |
| Time after any other run | 1 minute | "A measurement just ran. Try again in N s." |
| Lowest sim speed | 0.80 | "The server is busy, and a test would add to that. Try again when it's settled." |
| Which grids | Only grids you own | "You can only test your own grids." |

Admins can change these limits or turn the command off. See [Settings](Settings#player-settings).

---

## Admin commands: instant answers

These commands answer at once from what Monitor has already measured. They do not start a run, and they cost almost nothing.

### `!monitor status`

The server right now. Examples: `!monitor status`.

From a Discord bot:

```text
### Status
`Sim speed` **0.98**
`Frames/s ` **60**
`Simulated` **59**
`Frame p50` **11.40 ms**
`Frame p95` **15.10 ms**
`Frame p99` **18.70 ms**
`Frame max` **33.20 ms**
**Last hour**
`Sim speed` `█████████▅▅██████▇██████` 0.99
`Physics  ` `▂▂▂▂▂▂▂▂▂▅▅▂▂▂▂▂▂▂▂▂▂▂▂▂` 20%
`Entities ` `▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃` 29%
**Where the frame went**
> ` ms/f` Part
> ` 0.40` Network
> ` 0.30` Plugins
> `10.20` Session
> ` 0.90`  Task wait
> ` 8.60`  Components
> ` 3.30`   Physics
> ` 1.20`   Entities before
> ` 3.70`   Entities after
> ` 0.50`  Multiplayer tick
**Memory**
`GC mode  ` **Server, SustainedLowLatency**
`Gen 0/1/2` **41 / 12 / 1**
`Allocated` **5120 KB/s**
**World**
`Players   ` **14**
`Factions  ` **9**
`Ping      ` **62.4 / 180 ms**
`Grids     ` **1240**
`Blocks    ` **412,880**
`PCU       ` **1,904,215**
`Characters` **16**
`Floating  ` **37**
`Voxels    ` **112**
**Monitor**
`Own cost          ` **0.01 ms/f**
`Detail timing     ` **0.081 ms/f**
`Detail mode       ` **Grouped**
`Clock pair        ` **21 ns**
`Overhead taken out` **11 ns/call**
`Timed call        ` **58 ns**
```

How to read it:

- **Frames/s** is how many frames ran in the last second. **Simulated** is how many of them moved the game forward.
- **Last hour** (Discord only) has one bar for each 2.5 minutes. The sim speed bar shows the worst second in that time, so a short dip still shows. The other bars show how much of the frame each part used.
- **Where the frame went** is the top of the frame tree. Indented parts are inside the part above them.
- **Ping** is the average and the worst player ping.
- **Own cost** is what Monitor itself costs each frame.

### `!monitor sim`

Best, worst and average sim speed. Add `--secs=N` for the time span, from 1 to 3600 seconds. The default is 10.

| You type | Covers |
|---|---|
| `!monitor sim` | The last 10 seconds |
| `!monitor sim --secs=300` | The last 5 minutes |
| `!monitor sim --secs=3600` | The last hour |

```text
Sim speed
Over 300 s, Best 1.00, Worst 0.71
Average 0.96, Worst frame 212.40 ms
```

### `!monitor frames`

Where frame time went, as a tree, and how much time is left in a frame. Takes `--secs=N`, up to 3600.

```text
!monitor frames --secs=60
```

```text
### Frame time
`Over           ` **60 s**
`Sim speed      ` **0.98**
`Left in a frame` **4.10 ms**
> ` ms/f` Part
> ` 0.12` Invoke queue
> ` 0.40` Network
> ` 0.30` Plugins
> `11.60` Session
> ` 0.90`  Task wait
> ` 0.20`  Parallel callbacks
> ` 9.80`  Components
> ` 3.30`   Physics
> ` 1.20`   Entities before
> ` 4.90`   Entities after
> ` 0.50`  Multiplayer tick
```

**Left in a frame** is 16.67 ms minus the average frame. When it falls near 0, the server starts to lag.

### `!monitor net`

What crosses the network, and for whom. Takes `--secs=N`, up to 3600.

```text
!monitor net --secs=60
```

```text
### Network
`Over     ` **60 s**
`Sending  ` **412.5 KB/s**
`Receiving` **38.2 KB/s**
`RPCs in  ` **96.0 /s**
**What's being sent**
`State sync ` **61%**
`Events     ` **22%**
`Replication` **14%**
`Other      ` **3%**
**Players**
> `Down   Up  RPC/s  Ping  Queue` Player
> `48.2  3.1      9    45    0.0` Player One
> `40.7  2.8      7    88    0.0` Player Two
> `36.9  4.0     12   190  212.5` Player Three
*Down and Up are KB/s, Ping is ms, Queue is KB.*
**Biggest events sent**
> `KB/s  Events/f` RPC
> `31.4       0.8` MyCubeGrid.OnGridChangedRPC
> `12.0       2.1` MyInventory.OnInventoryContentChanged
```

**Queue** is data waiting to be sent to that player. Here Player Three has 212.5 KB waiting. If it keeps growing, that player cannot receive data fast enough. This often explains rubberbanding while sim speed is 1.00.

### `!monitor top LIST`

The latest background total for one list. LIST is one of `grids`, `blocktypes`, `blocks`, `players`, `factions`, `entities`, `session`, `logic`, `callbacks`, `rpcs`, `rpcssent`, `scripts` or `mods`.

| You type | Shows |
|---|---|
| `!monitor top grids` | The costliest grids |
| `!monitor top mods` | The costliest mods and plugins |
| `!monitor top rpcssent` | The biggest RPCs the server sent, in KB/s and events per frame |

```text
### Background grids
*From 5430 s after start. A run gives accurate numbers.*
> `Main  Work  Peak   On` Name
> `1.84  0.62  6.10  M+W` Iron Hauler
> `1.20  0.03  3.40    M` Outpost Kappa
> `0.05  0.91  1.20  M+W` Drill Rig 7
*Main and Work are ms/f, Peak is ms. On is M for the game thread, W for workers, M+W for both, and - where no time was recorded.*
```

Background totals are cheap estimates for trends. To find the cause of lag, use a run.

### Other instant commands

| Command | What it shows |
|---|---|
| `!monitor health` | Monitor's own patches: how many methods, how long patching took, and how many call sites are ok, drifted, unavailable, taken over by another plugin, or left alone. |
| `!monitor patches` | Which other plugins patch the game, and which methods. |
| `!monitor sites` | For experts: call counts and sampling for each measuring point. |
| `!monitor cleangps` | Removes the GPS markers that Monitor sent you. The reply is, for example, "Removed 3 markers." |

---

## Admin commands: runs

A run times every call on the server for `--secs=N` seconds (10 by default). Runs give the most exact answers. They make the server a little slower while they last, and every reply says by how much.

When you start a run, Monitor replies at once, for example "Measuring grids for 10 s, with every call timed. Results follow when it's done." The result follows when the run ends.

### The facts at the top of every run

```text
### Grids
`Measured     ` **10.0 s**
`Frames       ` **582**
`Sim speed    ` **0.97**
`Frame avg    ` **14.20 ms**
`Frame worst  ` **41.30 ms**
`Timing cost  ` **0.42 ms/f**
`Main waiting ` **1.85 ms/f on workers**
`Parallel work` **6.10 ms/f across workers**
```

| Fact | Meaning |
|---|---|
| Measured | How long the run measured. |
| Frames | How many frames ran. |
| Sim speed | Average sim speed during the run. |
| Frame avg, Frame worst | The average and the slowest frame. |
| Timing cost | How much Monitor's timing added to each frame. The numbers in the reply include it. |
| Main waiting | How long the game thread waited for worker threads, per frame. This is the only way worker time slows a frame. It can read a little high, because it includes handing out the work. |
| Parallel work | Time on worker threads in the game's parallel updates, added up over all workers, per frame. It is not frame time. |

Warnings can follow the facts, for example "The server paused, so the run stopped early." or "Only 98.5% of measured time fitted in the tables, so some rows read low."

### Options

Write options after the command, for example `!monitor grids --secs=20 --top=5`.

| Option | What it does | Default and limits |
|---|---|---|
| `--secs=N` | How many seconds to measure. | 10. From 1 to 600. |
| `--top=N` | How many rows in each list. | 10. From 1 to 50. |
| `--grid=ID` | Only this grid and its blocks. `--entity=ID` is the same. | |
| `--this` | Only the grid you control now. You must sit at its controls. | |
| `--player=NAME` | Only blocks this player owns. NAME is a name, an identity ID, or `sid/STEAMID`. | |
| `--faction=TAG` | Only blocks owned by members of this faction. TAG is a tag, a name or a faction ID. | With `--player`, both must match. |
| `--block=TYPE` | Only blocks whose class name contains this text, for example `--block=Drill`. | Not with `--noblocks`. |
| `--noblocks` | Only the grids' own updates, without their blocks. | |
| `--details` | Each grid's owners and their factions. | `grids` and `diagnose` only. |
| `--gps` | Sends you GPS markers for the grids in the list. | `grids` only, and only from a player. |

Monitor refuses an option it does not know, or one that does not fit the command, so a typing mistake cannot measure the wrong thing. For example, `--details` on `blocks` gives "--details only works with grids and diagnose."

The filters (`--grid`, `--this`, `--player`, `--faction`, `--block`, `--noblocks`) do not work with `diagnose`, `physics`, `session`, `entities`, `logic`, `callbacks`, `rpcs` or `mods`, because those rows do not belong to one grid or owner.

### `!monitor diagnose`

Times everything, then shows the frame and the top of every list. This is the first command to use when the server lags. It takes `--secs`, `--top` and `--details`.

| You type | Does |
|---|---|
| `!monitor diagnose` | 10 seconds, top 10 of every list |
| `!monitor diagnose --top=3` | A shorter reply |
| `!monitor diagnose --secs=30 --details` | A longer run, with grid owners |

The start of a reply, from a Discord bot (with `--top=2`; the full reply goes on to scripts, session components, RPCs, mods and clusters):

```text
### Diagnosis
`Measured     ` **10.0 s**
`Frames       ` **582**
`Sim speed    ` **0.91**
`Frame avg    ` **18.30 ms**
`Frame worst  ` **64.20 ms**
`Timing cost  ` **0.42 ms/f**
`Main waiting ` **1.85 ms/f on workers**
`Parallel work` **6.10 ms/f across workers**
*Every call was timed. Main and Work are ms/f, Peak is ms. On is M for the game thread, W for workers, M+W for both, and - where no time was recorded.*
**Frame**
> `ms/f  Frame` Part
> `4.10  22.4%` Physics
> `2.20  12.0%` Entities before
> `7.60  41.5%` Entities after
> `0.40   2.2%` Network
> `0.50   2.7%` Replication
> `0.30   1.6%` Plugins
**Top grids**
> `Main  Frame  Work  Peak   On` Grid
> `3.20  22.5%  1.10  9.04  M+W` Iron Hauler (2210 blocks)
> `1.75  12.3%  0.02  4.80    M` Outpost Kappa (5121 blocks)
**Top block types**
> `Main  Frame  Work  Peak  On` Block type
> `2.10  14.8%  0.00  3.10   M` MyAssembler
> `1.40   9.9%  0.00  2.20   M` MyRefinery
**Top players**
> `Main  Frame  Work  Peak   On` Player
> `3.90  27.5%  1.20  9.04  M+W` Player One (4 grids)
> `2.00  14.1%  0.10  4.80    M` Player Two (11 grids)
```

How to read it: "Entities after" uses 41.5% of the frame, and Iron Hauler alone uses 22.5%. A good next step is `!monitor grids --grid=ID` for that grid, or `!monitor blocktypes`.

Some lists overlap. A script runs inside its block's update, so it is in both "Top scripts" and its grid. Do not add the lists together.

### `!monitor grids`

The costliest grids.

| You type | Shows |
|---|---|
| `!monitor grids` | The top 10 grids |
| `!monitor grids --details` | With owners and factions |
| `!monitor grids --player=Player One` | Only that player's blocks |
| `!monitor grids --faction=HLR --top=5` | Only that faction's blocks, top 5 |
| `!monitor grids --gps` | And sends you GPS markers for them |

With `--details`, in the game:

```text
Grids
Measured 10.0 s, Frames 582, Sim speed 0.97
Frame avg 14.20 ms, Frame worst 41.30 ms
Timing cost 0.42 ms/f
Main waiting 1.85 ms/f on workers
Parallel work 6.10 ms/f across workers
Every call was timed. Main and Work are ms/f, Peak is ms. On is M for the game thread, W for workers, M+W for both, and - where no time was recorded.
Iron Hauler (Player One [HLR])
   Main 3.20  Frame 22.5%  Work 1.10  Peak 9.04  On M+W
Outpost Kappa (Player Two [KPA], Player Four [KPA])
   Main 1.75  Frame 12.3%  Work 0.02  Peak 4.80  On M
Drill Rig 7 (Player Three [<single>])
   Main 0.06  Frame 0.4%  Work 0.94  Peak 1.32  On M+W
```

`[<single>]` means the owner is in no faction. A grid removed during the run still shows, with "(removed)" after its name.

### Other lists

These work like `grids` and give the same columns:

| Command | Lists | Example |
|---|---|---|
| `!monitor blocktypes` | Block types, for example "MyRefinery" | `!monitor blocktypes --grid=ID` |
| `!monitor blocks` | Block definitions, for example "Large Refinery" | `!monitor blocks --block=Refinery` |
| `!monitor players` | Players, by the blocks they own | `!monitor players --top=20` |
| `!monitor factions` | Factions | `!monitor factions --secs=30` |
| `!monitor scripts` | Programmable blocks, as 'block name' in 'grid name' | `!monitor scripts --player=Player One` |
| `!monitor session` | Session components. Many mods use these. | `!monitor session` |
| `!monitor entities` | Entity types | `!monitor entities` |
| `!monitor logic` | Game logic components. Many mods use these. | `!monitor logic --top=20` |
| `!monitor callbacks` | Grid callbacks and deferred callbacks | `!monitor callbacks` |
| `!monitor rpcs` | RPCs from clients, then the biggest RPCs the server sent (KB/s and Events/f) | `!monitor rpcs --secs=30` |
| `!monitor mods` | Mods and plugins, as "Mod: name", "Plugin: name" or "Game". Each plugin's own Update is included. | `!monitor mods` |

### `!monitor physics`

Times physics stages, then the costliest physics clusters. A cluster is a group of physics objects close together, which the physics engine steps as one unit.

```text
### Physics
`Measured     ` **10.0 s**
`Frames       ` **582**
`Sim speed    ` **0.94**
`Frame avg    ` **16.10 ms**
`Frame worst  ` **38.00 ms**
`Timing cost  ` **0.42 ms/f**
`Main waiting ` **1.85 ms/f on workers**
`Parallel work` **6.10 ms/f across workers**
*Every call was timed. Main and Work are ms/f, Peak is ms. On is M for the game thread, W for workers, M+W for both, and - where no time was recorded.*
> `ms/f  Frame` Stage
> `5.20  32.3%` Physics
> `0.30   1.9%`  Raycasts
> `4.40  27.3%`  Step
> `0.40   2.5%`  Rigid bodies
Timed 18 of 18 active clusters, each alone in turn.
*This added at most 0.55 ms/f. Alone means every physics thread to itself, so these add up to more than normal physics.*
> `Grids   Km  Alone  Share  Peak` Cluster
> `   41  2.1   2.90  48.2%  6.40` [0] Outpost Kappa, Iron Hauler
> `    3  0.4   0.80  13.3%  1.10` [1] Drill Rig 7, Small Scout
*--inspect=N lists a cluster's grids, --takeme=N takes you there, --takeme=done finishes.*
```

| Column | Meaning |
|---|---|
| Cluster | The cluster's number, and its two costliest grids |
| Grids | How many grids are in it |
| Km | How wide it is |
| Alone | How long it takes when it steps alone, with all physics threads for itself. Use it to rank clusters. |
| Share | Its share of all cluster time |
| Peak | Its slowest step, in ms |

After `physics` you can use:

| You type | Does |
|---|---|
| `!monitor physics --inspect=0` | Lists the grids in cluster 0 |
| `!monitor physics --takeme=0` | Moves your character to cluster 0 and sends GPS markers for its grids. You must not sit in a seat. |
| `!monitor physics --takeme=done` | Finishes, and removes the GPS markers |

Timing clusters changes how the game schedules physics for a short time. Monitor does it only when you ask with `physics`. If another plugin changes the same physics code, Monitor refuses and says why: "Clusters weren't timed, because ...".

---

## Admin commands: looking inside a method

### `!monitor drill NAME`

Times everything that one method calls. NAME is part of a method name, or a row number from the last result. Takes `--secs=N`, up to 120.

| You type | Looks inside |
|---|---|
| `!monitor drill UpdateAfterSimulation` | A method whose name contains this text |
| `!monitor drill 2` | Row 2 of the last result |
| `!monitor drill ExampleShield --secs=30` | A mod's method, for 30 seconds |

```text
### MyCubeGrid.UpdateAfterSimulation
`Owner   ` **Game**
`Over    ` **10 s**
`Calls   ` **1240 /f**
`Main    ` **3.10 ms/f**
`Workers ` **0.00 ms/f**
`Own work` **0.40 ms/f**
> `Calls/f  Main  Work  Peak  On` Call
> `   1240  1.60  0.00  2.10   M` [0] MyGridConveyorSystem.UpdateAfterSimulation (Game)
> `   12.5  0.70  0.00  4.40   M` [1] MyCubeGrid.UpdatePhysicsShape (Game)
> `      3  0.40  0.00  1.10   M` [2] ExampleShieldLogic.UpdateAfter (Mod: Example Shields)
*Main and Work are ms/f, Peak is ms. On is M for the game thread, W for workers, M+W for both, and - where no time was recorded. !monitor drill N looks inside row N.*
```

- **Own work** is time in the method itself, not in the calls it makes.
- Monitor stops early if timing adds more than 3 ms to each frame. Then it says "Stopped early, because timing this was costing the server too much."
- You can search plugin updates, session components, methods that other plugins patched, and anything Monitor has already timed.
- If the method never runs, or the compiler built it into its caller, the reply says "Try the caller."

---

## Admin commands: settings

| Command | Reply |
|---|---|
| `!monitor detail grouped` | "Detail is grouped. One related group is measured at a time, so compare groups by share of the frame." This is the default. |
| `!monitor detail everything` | "Detail is everything. Every call is timed until you change it." This costs the server more. |
| `!monitor detail off` | "Detail is off. The tree keeps running, and lag-finding commands still work." |
| `!monitor everything on` | "Everything is on. Every call is timed until you turn it off." |
| `!monitor everything off` | "Everything is off. Detail is back to grouped." (or whichever mode you had before) |
| `!monitor trigger on` | "Monitor will gather evidence by itself when it notices lag." This is the default. |
| `!monitor trigger off` | "Monitor won't react to lag by itself. Its saved settings put this back when the server next starts." |

`everything` is never kept after a restart. After a restart, detail is `grouped` again.

In the first 30 seconds after the world is ready, `detail`, `everything` and the runs reply "Detail starts 30 seconds after the game is ready. Try again shortly."
