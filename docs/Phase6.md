# Phase 6: Command review and additions

A review of Monitor's commands against the Torch Profiler's, then the gaps and useful additions that came out of it.

**Tested on a test server.** Its tests pass, and it has run on a test server with no players. It hasn't run on a real server yet.

## Who can run what

- Every command is Admin, except `howto`, `mine` and `test`. There are no Moderator commands.
- `mine` and `test` must come from a player in the game. A console or other caller with no in-game identity is refused, in the command itself and not only by its permission level.

## Player commands

Players often ask to test their own grids, and the Profiler had no way to let them.

| Command | Does | Costs the server |
|---|---|---|
| `!monitor mine` | Each of your grids in ms/frame, its share of the frame and its rank, such as 12 of 340 grids | Nothing, because it reads the latest background total |
| `!monitor test` | Times the grid you're controlling, or one of yours by name, then lists its block types, costliest blocks and scripts | Timing every call for its length |
| `!monitor howto` | How to use Monitor, and what its numbers mean | Nothing |

`mine` reads the worker's full tables, so it finds any grid, not just the top 20. Its figures are background estimates, and it says so.

Players only ever see their own grids. Other players are never named, and a rank is only a number.

`test` is limited, because a run costs the whole server:

| Limit | Default |
|---|---|
| Length | 5 seconds |
| Between one player's tests | 10 minutes |
| Between any two players' tests | A minute after the last ends |
| Lowest sim speed it runs at | 0.80, so players can't add to lag |
| Whose grids | Only ones the player owns |

Admins can change these, or turn either command off, on the Incidents tab.

## Fixes to existing commands

**Your Profiler did these, and Monitor had stopped:**

- `--player` with `--faction` now needs both to match. The faction used to replace the player.
- `--player` takes a name, an identity id or `sid/STEAMID`, and `--faction` takes a tag, a name or a faction id.
- A run replies with one message. It used to send a chat message per line.
- `diagnose` shows block counts per grid, and grid counts per player and faction.
- `--grid=ID` refuses an id that isn't a grid, rather than measuring and finding nothing.

**These were misleading:**

- Filters are refused on `session`, `logic`, `callbacks`, `mods`, `entities`, `physics` and `diagnose`. Those rows aren't tied to a grid or an owner, so a filter was silently ignored and the output only looked filtered.
- `--gps` only works with `grids`, and `--details` with `grids` and `diagnose`. Elsewhere they're refused, not ignored.
- `sim` and `frames` only take `--secs`, and that now goes to 3600, which is what the worker holds.
- `frames` and `status` indent parts under what contains them, because a flat list read as more than the frame. `frames` also says what's left in a frame, which the Profiler's Idle line showed.
- `everything off` goes back to the mode you were in, and not always to grouped.
- `top` uses the same words as the commands: `blocks`, `players` and `entities`. The old words still work, and `factions` is listed now.
- `!monitor patches` now shows other plugins' patches, the same as the Patches tab. Monitor's own are in `!monitor health`.

**Left as they were:**

- `on` and `off`: `detail off` does this, and the tree stays on.
- `flags`: Monitor doesn't touch those flags, so there's nothing to show.
- `--tics`: `--secs` replaced it, because clusters are timed across the whole run.

## Runs

- **Chat runs reach the Runs tab,** with who started them. Runs from `test` don't, because they're one player's grid.
- **Runs and detail changes are marked on the charts,** because timing every call slows frames, and a bump with no reason looks like lag.
- **The Runs tab has the filters chat has:** player, faction and block type.
- **Every run lists the slowest single calls.**
- **Results can be copied as plain text,** for a ticket or a chat, and grids have a button that copies a GPS line.
- **Clusters list their grids** on the Runs tab, up to 30.

## New on the pages

- **Tree tab:** the whole tree over the chosen range, with total, self and share of frame. A large self means time the parts below don't explain.
- **Overview:** garbage collections, main thread allocation, memory over days, the GC mode, and what's new or has jumped compared with the history kept.
- **World:** blocks by faction over time, what's costing time while its owner is offline, and a capacity table of frame time against players.
- **Saves say how long they took,** using how long that frame ran, so no new patch was needed.
- **Admins are told in game** when lag is noticed, and again with what the evidence shows. It can be turned off.
- **Each plugin's own Update is timed,** and shows under `!monitor mods` and the Mods list. Magnetar's loader can't be checked offline, so these patches go on the plugins' own methods, once at startup.
- **Capacity only gives a limit when the history fits a line.** Otherwise it says it can't tell, because a line through scattered points always gives a number.

## Looking inside a method

Monitor could time a plugin's Update, its components and its callbacks, but not what they did inside. `!monitor drill` and the Inside tab now do, for any method, with no help from whoever wrote it.

- **Pick a method** by part of its name. Plugin updates, session components, methods other plugins patched and whatever timing has already seen can all be searched. That last one is how a mod's game logic and callbacks get there.
- **Every call it makes is timed** and listed, costliest first, and what's left is the method's own work. A row can be looked inside in turn.
- **It's one patch, on the method being looked at,** not one on each thing it calls, so a call the JIT inlined still counts. Async methods and iterators are patched where their code really is.
- **It only costs anything while it runs.** Rebuilding a method takes a moment on the game thread, so it only happens on request, one method at a time, for up to 120 seconds, and the patch comes out by itself.
- **It stops early** if the timing adds more than 3 ms a frame, and says so. A result also says when timing was a large part of what it measured.
- **Main thread and worker time are kept apart,** each with only its own calls' overhead taken off. Timing the calls inside a method isn't counted as that method's own work.

Limits:

- A call through an interface, a delegate or an abstract method gets a row, but can't be followed, because what runs isn't known until then.
- Framework property reads and constructors are left out, because timing them would cost more than they do.
- A call that throws loses its own time.
- A tail call isn't timed, because nothing can sit between it and its return. C# doesn't produce these.
- A method's first 256 different calls are followed, and the result says how many weren't.

## Mods over time

`!monitor mods` gives a mod's cost for one run. Background totals now keep it as well, so the Mods tab can chart it.

- **Each sweep totals every mod and plugin,** from session components, game logic, callbacks and plugin Updates. The game's own code isn't a row. Whose code a key is gets worked out on the low priority thread, once per key.
- **The tab charts the costliest 6 and lists them all.** Opening a row charts that mod's costliest parts over time and lists them, each with a button to look inside it.
- **More rows are kept than for other lists:** up to 40 mods and plugins, and the top 80 parts across all of them, because every mod should get its line.
- **The sweep ring doubled to 3 MB.** Those rows took it from 200 a sweep to 320 with every list full, which is under an hour, and an hour is what lets Quasar restart without a gap.
- **Mods are in the rest of Monitor too:** `!monitor top mods`, the Costs tab, what's new or has jumped, and history through both APIs.

Limits:

- These are background numbers, so they're for trends. A run settles a cause.
- A part only has a figure in sweeps where it made the top 80, so a small mod's parts can come and go.
- History is by name, so a mod that changes its title starts a new line.

## Threads

Every row says which threads its time went on, and a move between them shows on the charts. Only game thread time can set off an alert, as before.

- **Main, Worker or Mixed.** Worked out from the main and worker time Monitor already keeps per row, so the game thread does nothing new. A side under 5% of the time doesn't count, so a few stray calls don't make a row mixed. Clusters have none, because a cluster's time is one step with every physics thread to itself.
- **Everywhere rows are:** a Thread column with a word and a split bar on every list in both UIs, an `On` column in chat (M, W or M+W), the text report and both APIs. The Tree page says where the game thread waits for others: Task wait and the two Parallel parts wait on workers, and physics Step waits on the physics engine's threads.
- **Thread jumps.** Each background total compares every grid, block type, block, entity type, session component, game logic, callback, script, RPC, mod and mod part with the totals before, on the low priority thread. A change counts once it holds for two totals in a row, so a row near the line can't flap. Under 1 µs a frame there's too little time to say, and a total like that breaks the run, so the two still have to be in a row. Something missing from a total is forgotten. Owners and factions are left out, because they're sums of grids.
- **Where jumps go.** Up to 40 a total, costliest first, ride in the sweep ring as rows of their own list, so they reach Quasar and Torch with the totals they came from and need no new channel. A total that found more says how many in one more row, shown as "N more thread changes not listed". They become chart markers in their own colour, a Thread changes list on the overview, and `ThreadJump` events in both APIs. They're kept out of the Board's events panel and the Compare tab's choices. They never open an incident, send a webhook or start a run.
- **Names without asking the game thread.** Jumps cover every object, and names come from the game thread, so a jump never asks for one. It takes a name already known, from its total's own rows or from before, and shows the id until a normal row brings the name, which is then filled in. The file keeps the id, so after Quasar restarts an old jump can show one. The server API does the same from its own cache.
- **Older Quasar.** The history format is unchanged. A Quasar from before this stores jump rows as rows of a list nothing reads.
- **Main waiting.** A run shows how long the game thread waited for workers: the task wait, plus each parallel update's game thread time less the work the game thread did there itself. That's the only way worker time costs the frame. It includes the game thread handing the work out, so it can read slightly high. Next to it is the workers' time in the parallel updates. Only runs have these, because background detail times the parallel updates for part of the time.
- **Memory:** about 2.7 MB for the jump tables, allocated once on the low priority thread. The sweep ring holds over an hour at 400 rows a sweep.

## APIs

There are now two, in one assembly, with the same replies. See [PublicApi.md](PublicApi.md).

- **`IMonitorApi`** is for Quasar plugins, and covers every server Quasar has data on, connected or not.
- **`IMonitorServerApi`** is for server plugins, and only answers about the server it's on.

The API assembly moved to netstandard2.0, so Legacy servers can load it.

## Discord

Monitor posts incidents to a Discord webhook. A webhook only posts, so there's no bot to run, no library to ship and nothing in Quasar to change.

| Post | When |
|---|---|
| Lag on a server, and what looked unusual | An incident opens |
| What's behind it: top grids with their owners and factions, mods, scripts, session components and slowest calls | The evidence is in |
| It recovered, how long it lasted, and how bad it got | The incident closes |

Set it on the Incidents tab, which needs `CanManageDiscord`. A server can have its own webhook, and one can cover every server without its own. Send a test checks it works.

- **Posts go out on their own queue on the Quasar side,** so Discord being slow or down can't touch collection. A failure is only logged. The worker never touches the network.
- **The URL is a secret,** because anyone who has it can post to that channel. It's kept in `settings.json`, it never goes back to a page, and it's never logged.
- **Only Discord's own addresses are accepted,** because a webhook makes Quasar post to the address it's given.
- **Mentions are off.** Players choose grid names, so a grid called `@everyone` would otherwise ping the whole Discord server.
- **Rate limits:** a post that's limited waits as Discord asks, up to 30 seconds, and tries once more.

Quasar's own sim speed alerts carry on working alongside this. If both use one channel, one lag event gets two messages, so Monitor is better in a channel of its own.

Webhooks can't take commands, so `!monitor` commands aren't available from Discord. Quasar can already run them through its chat page.

## Storage

- Quasar now keeps each server's name beside its history, so one that's gone can still be listed.
- A server's old files used to be pruned only when new data arrived, so a server that had gone kept its files forever. Pruning now runs hourly for every server, and a server unheard of for longer than the retention setting is removed.
- History rows and census rows have new fields. Nothing has been collected yet, so nothing needs migrating.

## Decided against

- **An autosave scope.** It needs a new patch in `MySession.Update`, for what a save marker now says for nothing.
- **Keeping `everything` across a restart.** Held on by mistake, it would keep costing the server after every restart, and going back to grouped is the safer failure.
- **The Profiler's `custom`.** It timed named measurements that a plugin's author had added. The drill-down answers the same question for any method, without the author's help.

## Not built

Blocks vs Conceal, network usage per player and per-RPC timing were here, and are now built. See [Phase7.md](Phase7.md).

## Before submitting to the hub

`quasar-hub.xml` is there, with two things only you can settle:

- `Commit` is a placeholder.
- `RepoId` is `buttheadbob/Monitor`.

## Tests to run

Written and compiling, but not run:

- `tests/SenX.Monitor.Core.Tests`: `CaptureTests`, `MarkerTests`, `SweepTests`.
- `tests/SenX.Monitor.Magnetar.Tests`: `DrillDownTests`, `ServerApiTests`, `PluginHooksTests`.
- `tests/SenX.Monitor.Ui.Tests`: `MonitorWebhooksTests`, `ReportTextTests`, `NewCostTests`, `CapacityTests`, `PublicApiTests`, `ChartBuilderTests`, `MarkerTests`, `IncidentTests`, `MonitorCollectorTests`.

`PlayerCommands`, `TellAdmins` and `FindPlayer` have no tests, because they need the game.

`DrillDownTests` needs `Sandbox.Game` and `VRage.Game` to load, so the test project now references them. Nothing starts the game. It's the first test to load them, so a failure there may be the host and not the code.

These need a real server:

- `mine` and `test` as a player, and refused from the console.
- A long `diagnose` reply in game. It now comes in pages, see [Phase7.md](Phase7.md).
- A server plugin reaching `MonitorServerApi.Current`, on Legacy and Interim, with the API assembly not shipped beside it.
- Each plugin's Update showing under `!monitor mods`.
- A mod's lines on the Mods tab, and its parts when its row is opened.
- `!monitor drill` and the Inside tab on a real plugin's Update and a mod's game logic, and how long the rebuild takes.
- A real webhook: send a test, then a lag incident, and check how the posts look in Discord.
