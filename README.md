# Monitor

Monitor shows what slows down a Space Engineers dedicated server.

It measures the server while the server runs. It keeps working during the worst lag. It tells you which grids, blocks, scripts, mods and players cost the most time.

Monitor has two parts that work together:

- **Magnetar plugin.** It runs on each server that uses the Magnetar plugin loader. It measures, and it answers chat commands.
- **Quasar plugin.** It runs in the Quasar server manager. It keeps history, draws charts and sends alerts for your Magnetar servers.

**Status:** pre-release. Both parts have been tested on test servers and on the author's own live servers.

**Source:** https://github.com/buttheadbob/Monitor

**Torch:** a Torch build exists on GitHub, but it is not supported. See [Torch build (unsupported)](#torch-build-unsupported).

## Contents

- [Words used in this guide](#words-used-in-this-guide)
- [Install](#install)
- [First steps](#first-steps)
- [Chat commands](#chat-commands)
- [What the columns mean](#what-the-columns-mean)
- [Lag alerts and incidents](#lag-alerts-and-incidents)
- [Join hitch](#join-hitch)
- [Thread tracking](#thread-tracking)
- [Settings](#settings)
- [The Monitor page in Quasar](#the-monitor-page-in-quasar)
- [What Monitor costs the server](#what-monitor-costs-the-server)
- [For plugin developers: the API](#for-plugin-developers-the-api)
- [Troubleshooting](#troubleshooting)
- [Building from source](#building-from-source)
- [Torch build (unsupported)](#torch-build-unsupported)
- [License](#license)

## Words used in this guide

| Word | Meaning |
|---|---|
| Frame | One step of the game. The server tries to run 60 frames each second. |
| Frame budget | The time one frame may take: 16.67 milliseconds (ms). If frames take longer, the game slows down. |
| Sim speed | How fast the game runs, compared with normal. 1.00 is full speed. 0.50 is half speed. |
| Lag | The game runs slower than normal. Sim speed falls below 1.00. |
| ms/f | Milliseconds per frame. The average time something takes in each frame. |
| Game thread | The one thread that runs each frame. It is also called the **main thread**. If the game thread is slow, the frame is slow. |
| Worker thread | A helper thread. The game gives some work to worker threads, so that the work runs at the same time on other CPU cores. Worker time costs the frame only when the game thread waits for it. |
| Main, Worker, Mixed | Where the time of something went: on the game thread (Main), on worker threads (Worker), or on both (Mixed). See [Thread tracking](#thread-tracking). |
| Spike | One frame that takes a very long time, for example one second. |
| Peak | The slowest single call in a list row. |
| p50, p95, p99 | Frame time percentiles. p95 is the time that 95% of frames stay under. Only the slowest 5% of frames take longer. |
| Run | Monitor times every call on the server for a few seconds. Runs give the most exact answers. A run makes the server a little slower while it lasts. |
| Background total | Every 30 seconds, Monitor adds up what it measured in the background. These numbers cost almost nothing. They are good for trends. A run is better for finding a cause. |
| Detail | How much Monitor measures in the background. See [`!monitor detail`](#admin-commands-settings). |
| Lag trip | Monitor decides that the server is lagging. A trip can start a run and send alerts. |
| Cooldown | The time after a lag trip during which no new trip can start. |
| Incident | A record of one lag event in Quasar, from its start to its end, with the evidence. |
| Join hitch | One long frame just after a player's character spawns. It is not counted as lag. See [Join hitch](#join-hitch). |
| Grace window | The seconds after a player spawns during which a spike is a join hitch, not lag. The default is 15 seconds. |
| Thread change | The time of something moved between the game thread and worker threads and stayed there. See [Thread tracking](#thread-tracking). |
| Cluster | A group of physics objects close together. The physics engine steps each cluster as one unit. |
| RPC | A network message that asks the server (or a client) to run a method. |
| Webhook | An address that lets Monitor post messages to a Discord channel. |

## Install

### Quasar

You need Quasar version 1.6 or newer.

1. Get the package folder `senx.monitor`. The script `tools/Package-Local.ps1` builds it to `artifacts/senx.monitor`.
2. Stop Quasar.
3. Copy the `senx.monitor` folder into Quasar's `Plugins` folder. If an older `senx.monitor` folder is there, replace it.
4. Start Quasar.
5. Quasar sends a small companion part to each managed server. Restart each server so that it loads the companion.
6. Install the Magnetar plugin on each server too. See the next part.

Quasar keeps Monitor's history and settings in the `monitor` folder inside Quasar's install folder.

### Magnetar

Magnetar builds Monitor from its source code. There is no file to copy.

To use your own copy of the source code:

1. On the server, press F8 to open MagnetarConfig.
2. Open **Local & Dev Folders**.
3. Pick `Monitor.xml` from the Monitor source folder.
4. Choose **Release**.
5. Restart the server.

If the server runs under Quasar, you can add the folder in Quasar's dev folders instead.

When Monitor is on MagnetarHub, you can install it from the hub like any other plugin.

A Magnetar server without Quasar still measures everything, and all chat commands work. It keeps about one hour of history in memory. It saves nothing to disk. It uses the default settings.

### Update or remove

- **Update:** replace the files in the same way, then restart.
- **Remove:** delete the `senx.monitor` folder from Quasar's `Plugins` folder, and remove Monitor from the Magnetar plugin list. Then restart. Monitor removes all its patches when it stops.

## First steps

Monitor starts measuring in the background 30 seconds after the world is ready.

**For admins:**

1. Type `!monitor status`. You see sim speed, frame times and where frame time goes.
2. If the server lags, type `!monitor diagnose`. Monitor times everything for 10 seconds. Then it lists the costliest grids, blocks, scripts, mods and more.
3. Open the Monitor page in Quasar to see charts and history.

**For players:**

1. Type `!monitor mine`. You see what each of your grids costs, and its rank among all grids.
2. Sit in your grid, then type `!monitor test`. Monitor times your grid for 5 seconds.

Type `!monitor howto` at any time for a short help text.

## Chat commands

All commands start with `!monitor`.

### Who can use them

- Players can use `howto`, `mine` and `test`.
- Only admins can use the other commands.
- `mine` and `test` must come from a player in the game. The server console cannot use them.

### How replies look

Monitor sends one finished reply for each command. Long replies come in pages.

- **In the game,** each row shows its name, with the numbers on the next line.
- **In Discord** (through a bot) and in the console, each row shows the numbers first, then the name. The numbers line up in columns.

A Discord reply looks like this:

```text
### Grids
`Measured   ` **10.0 s**
`Sim speed  ` **0.97**
`Frame avg  ` **14.20 ms**
*Every call was timed. Main and Work are ms/f, Peak is ms. On is M for the game thread, W for workers, M+W for both, and - where no time was recorded.*
**Top grids**
> `Main  Frame  Work  Peak  On ` Grid
> `3.20  22.5%  1.10  9.04  M+W` Rob Base 1
> `1.75  12.3%  0.40  4.80  M  ` Rob's Mega Miner
```

Grid names, player names and script names are made safe. A name like `@everyone` cannot ping anyone.

### Options for runs

Commands that time everything (runs) take these options. Write them after the command, for example `!monitor grids --secs=20 --top=5`.

| Option | What it does | Default and limits |
|---|---|---|
| `--secs=N` | How many seconds to measure. | 10. From 1 to 600. |
| `--top=N` | How many rows to show in each list. | 10. From 1 to 50. |
| `--grid=ID` | Only this grid and its blocks. | |
| `--this` | Only the grid you control now. You must sit at its controls. | |
| `--player=NAME` | Only blocks this player owns. NAME can be a name, an identity ID, or `sid/STEAMID`. | |
| `--faction=TAG` | Only blocks owned by this faction's members. TAG can be a tag, a name or a faction ID. | With `--player`, both must match. |
| `--block=TYPE` | Only blocks whose class name contains this text, for example `--block=Drill`. | Not with `--noblocks`. |
| `--noblocks` | Only the grids' own updates, without their blocks. | |
| `--details` | Show each grid's owners and their factions. | `grids` and `diagnose` only. |
| `--gps` | Send you GPS markers for the grids in the list. | `grids` only. |

Monitor refuses an option it does not know, or an option that does not fit the command. A typing mistake cannot measure the wrong thing.

The filters (`--grid`, `--this`, `--player`, `--faction`, `--block`, `--noblocks`) do not work with `diagnose`, `physics`, `session`, `entities`, `logic`, `callbacks`, `rpcs` or `mods`. Those rows do not belong to one grid or owner.

### Player commands

| Command | What it shows | Cost to the server |
|---|---|---|
| `!monitor howto` | A short help text about Monitor and its numbers. | None. |
| `!monitor mine` | Each of your grids: Main, Frame, Work, Rank and On. Then the total for all your grids. | None. It reads the latest background total. |
| `!monitor test` | Times one of your grids. Then it lists its block types, its costliest blocks and its scripts. Give a grid name, or sit at the grid's controls. | It times every call on the server while it runs. |

`mine` shows only your own grids. It never names other players. Your rank is a number, for example "12 of 340".

`test` has limits, because it costs the whole server:

| Limit | Default |
|---|---|
| How long a test runs | 5 seconds |
| Time between two tests by the same player | 10 minutes |
| Time between any two tests or runs on the server | 1 minute after the last one ends |
| Lowest sim speed for a test | 0.80. Below this, tests are refused, so they cannot add to lag. |
| Which grids | Only grids you own |

Admins can change these limits, or turn off `mine` or `test`. See [Settings](#settings).

### Admin commands: instant answers

These commands answer at once. They do not start a run.

| Command | What it shows |
|---|---|
| `!monitor status` | Sim speed, frames per second, frame p50, p95, p99 and max. Where frame time goes (the top of the tree). Garbage collection mode and counts. Monitor's own cost. World counts: players, factions, ping, grids, blocks, PCU, characters, floating objects and voxels. In Discord, also bars for the last hour. |
| `!monitor sim` | Best, worst and average sim speed, and the worst frame. Use `--secs=N` for the time span, up to 3600 seconds (1 hour). |
| `!monitor frames` | Where frame time went, as a tree, and how much time is left in a frame. Use `--secs=N`, up to 3600. |
| `!monitor net` | Network: KB sent and received each second, RPCs from clients, and what kind of data was sent. The top 10 players by data sent to them, with columns Down, Up, RPC/s, Ping and Queue. The 5 biggest events. Use `--secs=N`, up to 3600. |
| `!monitor top LIST` | The latest background total for one list. LIST is one of `grids`, `blocktypes`, `blocks`, `players`, `factions`, `entities`, `session`, `logic`, `callbacks`, `rpcs`, `rpcssent`, `scripts` or `mods`. Columns: Main, Work, Peak and On. |
| `!monitor health` | Monitor's own patches: how many methods, how long patching took, and how many call sites are ok, drifted, unavailable, taken over by another plugin, or left alone. |
| `!monitor patches` | Which other plugins patch the game, and which methods. |
| `!monitor sites` | For experts. Call counts and sampling for each measuring point, grouped as background detail measures them. |
| `!monitor cleangps` | Removes the GPS markers that Monitor sent you. |

### Admin commands: runs

These commands time every call for `--secs=N` seconds (10 by default). Then they reply. Each reply starts with the same facts:

| Fact | Meaning |
|---|---|
| Measured | How long the run measured. |
| Frames | How many frames ran. |
| Sim speed | Average sim speed during the run. |
| Frame avg, Frame worst | Average and slowest frame. |
| Timing cost | How much Monitor's timing added to each frame. The numbers in the reply include this. |
| Main waiting | How long the game thread waited for worker threads, per frame. This is the only way worker time slows a frame. It can read a little high, because it includes the time to hand out the work. |
| Parallel work | Time on worker threads in the game's parallel updates, added up across all workers, per frame. This is not frame time. |

| Command | What it lists |
|---|---|
| `!monitor diagnose` | The frame, then the top of every list: grids (with block counts), block types, blocks, players and factions (with grid counts), scripts, session components, game logic, callbacks, mods, RPCs and slow calls. Takes `--secs`, `--top` and `--details`. |
| `!monitor grids` | The costliest grids. |
| `!monitor blocktypes` | The costliest block types, for example "Refinery". |
| `!monitor blocks` | The costliest block definitions, for example "Large Refinery". |
| `!monitor players` | The costliest players, by the blocks they own. |
| `!monitor factions` | The costliest factions. |
| `!monitor scripts` | The costliest programmable blocks. |
| `!monitor session` | The costliest session components. Many mods use these. |
| `!monitor entities` | The costliest entity types. |
| `!monitor logic` | The costliest game logic components. Many mods use these. |
| `!monitor callbacks` | The costliest grid callbacks and deferred callbacks. |
| `!monitor rpcs` | The costliest RPCs from clients, and the biggest RPCs the server sent (columns KB/s and Events/f). |
| `!monitor mods` | The costliest mods and plugins. Each plugin's own Update is included. |
| `!monitor physics` | Physics stages (columns Stage, ms/f, Frame), then the costliest physics clusters (columns Cluster, Grids, Km, Alone, Share, Peak). |

After `!monitor physics`, you can use:

- `!monitor physics --inspect=N` to list the grids in cluster number N.
- `!monitor physics --takeme=N` to move your character to cluster N. You get GPS markers for its grids. You must not sit in a seat.
- `!monitor physics --takeme=done` to finish. This removes the GPS markers.

Timing physics clusters changes how the game schedules physics for a short time. Monitor does this only when you ask with `physics`. Monitor refuses to time clusters if another plugin changes the same physics code.

### Admin commands: looking inside a method

`!monitor drill NAME` times everything that one method calls. NAME is part of a method name, or a row number from the last result.

- Use `--secs=N`, up to 120 seconds.
- The reply shows the method's owner, Calls, Main, Workers and Own work. Then it lists each call inside it, with Calls/f, Main, Work, Peak and On.
- Type `!monitor drill N` to look inside row N of the last result.
- Monitor stops early if timing adds more than 3 ms to each frame.
- You can search plugin updates, session components, methods that other plugins patched, and anything Monitor has already timed.

### Admin commands: settings

| Command | What it does |
|---|---|
| `!monitor detail grouped` | Background detail measures one group of related work at a time. This is the default. It costs very little. |
| `!monitor detail everything` | Background detail times every call, all the time. This is exact, but it costs the server more. |
| `!monitor detail off` | No background detail. The frame tree and the run commands still work. |
| `!monitor everything on` | The same as `detail everything`. |
| `!monitor everything off` | Goes back to the detail mode you had before. |
| `!monitor trigger on` | Monitor reacts to lag by itself. This is the default. |
| `!monitor trigger off` | Monitor does not react to lag by itself on this server, until the server restarts. |

`everything` is never kept after a restart. After a restart, detail is `grouped` again.

## What the columns mean

| Column | Meaning |
|---|---|
| Main | Time on the game thread, in ms per frame. This is the time that can slow a frame. |
| Frame | Main as a share of the whole frame. |
| Work | Time on worker threads, in ms per frame, added up across all workers. It can be more than a whole frame. |
| Peak | The slowest single call, in ms. |
| On | Which threads the time went on. `M` is the game thread. `W` is worker threads. `M+W` is both. `-` means no time was recorded for that row. |
| Calls/f | Calls per frame. |
| Rank | (`mine` only) Where the grid stands among all grids, for example 12 of 340. |
| Alone | (`physics` only) How long one cluster takes when it steps alone, with all physics threads for itself. Use it to rank clusters. The numbers add up to more than normal physics time. |
| Share | (`physics` only) That cluster's share of all cluster time. |
| Km | (`physics` only) How wide the cluster is. |
| Down, Up | (`net` only) KB per second sent to the player and received from the player. |
| Queue | (`net` only) Data that waits to be sent to that player. If it keeps growing, that player cannot receive data fast enough. This often explains rubberbanding while sim speed is 1.00. |

## Lag alerts and incidents

Monitor watches for lag by itself. It checks once each second. It looks only at the game thread: sim speed and frame times. Other threads are recorded and shown, but they never start an alert.

### When Monitor decides that the server lags

Monitor starts a lag trip in three cases:

- Sim speed is low for several seconds in a row. By default, this means below 0.90 for 5 seconds.
- Frame p95 is high for those seconds. This check is off by default.
- One frame takes very long. This is a spike. By default, this means more than 1000 ms.

Monitor does not start a trip:

- In the first 30 seconds after the world is ready.
- While the server is paused.
- While a run is going. A run's own timing is not lag.
- For a spike in the 2 seconds after a world save. A save stops the game for a short time. That is expected.
- For a spike in the grace window after a player spawns. See [Join hitch](#join-hitch).

After a trip, no new trip can start for 15 minutes (the cooldown).

### What happens on a trip

1. Monitor marks the time on the charts.
2. For slow sim speed or slow frames, Monitor starts a run of 5 seconds. A spike gets no run, because it is over before a run could start.
3. Monitor keeps the evidence: the 20 slowest frames from the 30 seconds before the trip, the slowest single calls, and the run.
4. Monitor tells admins in the game, first that lag was noticed, then what the evidence shows. You can turn this off.
5. Quasar opens an incident, and posts to Discord if you set a webhook.

### Incidents

You find incidents on the **Incidents** tab in Quasar.

- An incident opens at the trip.
- It closes after 60 healthy seconds in a row.
- If the lag lasts longer than the cooldown, Monitor trips again. The new evidence joins the same incident.
- A server restart closes the incident.
- An incident shows how long it lasted, the lowest sim speed and the worst frame.
- It also shows what looked unusual, and a chart of the 5 minutes before and after.

### Discord alerts

Monitor can post to a Discord channel through a webhook. You do not need a bot.

| Post | When |
|---|---|
| Lag on a server, and what looked unusual | An incident opens |
| What caused it: top grids with owners and factions, mods, scripts, session components and slow calls | The evidence arrives |
| The server recovered, how long the lag lasted, and how bad it was | The incident closes |

- Set the webhook on the Incidents tab.
- A server can have its own webhook. One webhook can also cover all servers that have none.
- Click **Send a test** to check it.
- Only Discord addresses are accepted.
- Mentions are off. A grid called `@everyone` cannot ping anyone.
- The webhook address is a secret. Monitor never shows it on a page and never writes it to a log.
- Discord commands are not possible through a webhook. Use your bot or Quasar's chat page to run commands.

Quasar has its own sim speed alerts. If both use the same channel, one lag event gives two messages. Give Monitor its own channel.

### Baselines

Quasar learns what is normal for each server, for each hour of the day.

- They need 90 minutes of data for an hour, over at least two days, before they say anything.
- Something is "unusual" when the last 5 minutes are more than a quarter above that hour's usual high. It must also be at least 1 ms above normal.
- The Overview tab shows what is unusual now. Each incident records it.

## Join hitch

When a player's character spawns, the game loads the voxel files for the asteroids near it. The first time after a server start, this happens on the game thread. One frame can then take several seconds.

This is normal game behavior. It is not lag that you can fix. So Monitor treats it in a special way:

- A spike in the 15 seconds after a player's character spawns is a **join hitch**.
- A join hitch gives no alert, no run and no incident. It does not start the cooldown.
- Monitor writes one line in the log, for example: "Monitor noticed a join hitch: one frame took 6786 ms just after a character spawned."
- The charts show a join hitch marker in its own color.
- Only real players count. NPCs and bots, for example from Modular Encounters Systems (MES), do not open the grace window.
- Slow sim speed and slow frames still count as lag during the grace window. Only single spikes are excused.

Change the grace window with **"Not within N s of a spawn"** on the Incidents tab. The default is 15 seconds. The maximum is 120. 0 turns it off.

## Thread tracking

The game runs some work on the game thread and some work on worker threads. Only game thread time slows the frame directly. So Monitor shows where the time of each thing went.

### Main, Worker or Mixed

Every row in every list has a thread label:

| Label | Chat | Meaning |
|---|---|---|
| Main | `M` | Almost all of its time is on the game thread. |
| Worker | `W` | Almost all of its time is on worker threads. |
| Mixed | `M+W` | It uses both. Each side has at least 5% of its time. |
| (empty) | `-` | No time was recorded for it. |

A side with less than 5% of the time does not count. So a few stray calls do not make a row "Mixed".

Physics clusters have no label. A cluster is timed with all physics threads for itself, so a thread label means nothing there.

### Where you see it

- **Chat:** the `On` column.
- **Quasar:** a **Thread** column on every list. It shows the label and a small bar. The first part of the bar is game thread time. The rest is worker time.
- **Tree page:** every part of the tree is game thread time. The **Thread** column shows where the game thread waits for other threads:
  - "Main, waits on workers" for Task wait and the two Parallel parts.
  - "Main, waits on physics threads" for the physics Step.
- **Runs:** "Main waiting" and "Parallel work". See [Admin commands: runs](#admin-commands-runs).
- **Copy as text** on a run: each row starts with `M`, `W` or `M+W`.

### Thread changes

Sometimes something moves between threads. For example, a grid's update might stop running on worker threads and run on the game thread instead. That can make the server slower.

- Monitor checks every grid, block type, block, entity type, session component, game logic, callback, script, RPC, mod and mod part in each background total.
- A change counts only when the new label stays for two background totals in a row (about one minute). So a row that is near the 5% line does not jump back and forth.
- Things with less than 1 microsecond per frame are ignored. There is too little time to decide.
- The charts show each thread change as a marker in its own color.
- The Overview tab lists them under **Thread changes**, for example "Grid Miner moved from worker threads to the game thread".
- Each background total lists up to 40 changes, the costliest first. If there are more, you see "N more thread changes not listed".
- A thread change never starts an alert, a run or an incident.
- A new name can show as `#` and a number, for example "Grid #81234". The real name appears when that grid shows up in a normal list. After Quasar restarts, old thread changes can still show the number.

## Settings

### Lag settings

In Quasar, open the **Incidents** tab, then **What counts as lag**. These settings apply to every server.

| Setting on the page | Meaning | Default | Allowed |
|---|---|---|---|
| Watch for lag | Monitor reacts to lag by itself. | On | On or off |
| Sim speed under | A second below this counts as slow. | 0.90 | 0.1 to 1 |
| Or frame p95 over | A second with frame p95 above this counts as slow. | 0 (off) | 0 or more ms |
| For | How many slow seconds in a row start a trip. | 5 | 1 to 600 |
| Or one frame over | A spike: one frame above this starts a trip. | 1000 ms | 0 (off) or more |
| Not within | The grace window after a player spawns. | 15 s | 0 (off) to 120 |
| Run for | How long a triggered run lasts. 0 keeps the evidence without a run. | 5 s | 0 to 60 |
| At most every | The cooldown between trips. | 900 s | 60 to 86400 |
| Time physics clusters | Triggered runs also time physics clusters. This changes physics scheduling when nobody watches, so it is off. | Off | On or off |
| Tell admins in the game | Send a chat message to online admins on a trip. | On | On or off |

### Player settings

Open the **Incidents** tab, then **What players can run**.

| Setting on the page | Meaning | Default | Allowed |
|---|---|---|---|
| !monitor mine | Players can use `mine`. | On | On or off |
| !monitor test | Players can use `test`. | On | On or off |
| For | How long a test runs. | 5 s | 1 to 60 |
| At most every | Time between two tests by the same player. | 600 s | 60 to 86400 |
| Not under sim speed | Tests are refused below this sim speed. | 0.80 | 0 to 1 |

### Discord settings

Open the **Incidents** tab, then **Discord alerts**. Enter the webhook URL. In Quasar, tick **For all servers** to use it for every server without its own.

### History

| Setting | Meaning | Default | Allowed |
|---|---|---|---|
| History to keep | How many days of history Quasar keeps. Shorter means older history is deleted. | 3 days | 1 to 7 days |

Quasar keeps second-by-second data for 6 hours. It keeps minutes, background totals, events, incidents and runs for the number of days you choose. That is about 30 MB per server with the default.

### Who can change what in Quasar

| Quasar permission | Allows |
|---|---|
| CanView | Open the Monitor page and read everything. |
| CanControlServers | Start and delete runs, profile a grid, start a patch audit, look inside a method. |
| CanEditConfigs | Change the lag settings, player settings and history length. |
| CanManageDiscord | Set the Discord webhook. |

### Where settings are saved

- **Quasar:** `settings.json` in the `monitor` folder inside Quasar's install folder. Quasar sends the lag settings to each server when the server starts and when you change them.
- **Magnetar without Quasar:** nothing is saved. The defaults are used.

## The Monitor page in Quasar

Quasar has a **Monitor** page in its menu. It has these tabs:

| Tab | What it shows |
|---|---|
| Overview | Sim speed, time left in each frame, frame times, players, ping, data sent, grids, blocks, PCU, memory and garbage collection. Charts of what uses the frame. Unusual parts. Thread changes. Recent events. |
| Tree | Where frame time went over the chosen time span, as a tree. Total, Self, Share of frame and Thread. A large Self means time that the parts below do not explain. |
| Costs | Any background list as a donut chart, a line chart of its top 6, and a table with the Thread column. |
| Mods | The costliest mods and plugins over time. Open a row to see its parts. |
| Network | Data sent and received, data by kind, RPCs, and a table for each player. |
| World | Players online, factions, blocks, PCU, ping, grids, concealed grids, and a capacity table of frame time against players. |
| Incidents | Settings, and every incident with its evidence. |
| Compare | Two time spans side by side. Pick an event to fill in the time before and after it. |
| Patches | Which other plugins patch which game methods. An audit can measure what those patches cost. |
| Inside | Look inside a method, the same as `!monitor drill`. |
| Runs | Start a run, read results, compare two runs, and copy results as text. Runs from chat commands also appear here. Each list is a panel: click its heading to open or close it. |

There is also a **Servers** tab, which shows all servers side by side. Monitor also adds to Quasar's **Entities** page: an ms/f column for each grid, and a **Profile** button that times one grid for 10 seconds.

**Full charts** opens a page with many charts at once, for one server or for all servers.

**Lists on the Runs and Compare tabs** sit in two columns of panels. On a narrow screen they become one column. Click a panel's heading to open or close it. Your browser remembers which panels are open. "Mods and plugins" always sits above "Entity types". Long names end with "…". Point at a name to see all of it.

**Chart markers** are dashed lines on the charts:

| Marker | Meaning |
|---|---|
| Lag noticed | A lag trip. |
| World saved | A save ended. It shows how long it took. |
| Server started | The server restarted. |
| Plugin or mod changed | A plugin or mod was added, removed or updated. |
| Run, Detail set to | A run started, or detail changed. Runs make frames a little slower, so the marker explains the bump. |
| Join hitch | A long frame after a player spawned. |
| Thread change | Something moved between the game thread and worker threads. |

## What Monitor costs the server

Monitor is built to do as little harm as possible.

- **Always on:** a small tree of timers on the game thread. This costs a few microseconds per frame.
- **Background detail (grouped):** at most about 2% of a frame while a group is measured. If a group would cost more, Monitor measures only some of its calls.
- **Runs and `everything`:** every call is timed. This costs more. Every reply says how much timing added.
- **Network counting:** one table lookup per packet, on the network thread. Nothing on the game thread.
- **Memory:** native memory, prepared when the world loads. Hot code creates no garbage, so it does not cause garbage collection pauses.
- **Disk:** the Magnetar plugin writes nothing to disk. Quasar writes history.
- `!monitor status` shows Monitor's own cost under "Own cost".

## For plugin developers: the API

Other plugins can read Monitor's data and ask for runs.

| API | For | Reaches |
|---|---|---|
| `IMonitorApi` | Quasar plugins | Every server that Quasar has data for |
| `IMonitorServerApi` | Server plugins on Magnetar | Only the server it runs on |

- Everything is in the `SenX.Monitor.Api` project. It targets netstandard2.0.
- Reference the project. Do not ship the DLL with your plugin.
- Monitor might not be installed. Check for it first.
- Rows have a `Thread` value: Main, Worker, Mixed or Unknown.
- Events include `JoinHitch` and `ThreadJump`. Neither is an alert.
- Run details include `MainWaitMs` and `ParallelWorkerMs`.
- Check `Version` before you use newer parts. `IMonitorApi` is version 4. `IMonitorServerApi` is version 3.

Full details and examples are in [docs/PublicApi.md](docs/PublicApi.md). `samples/ApiConsumer` is a working example.

## Troubleshooting

**"Detail starts 30 seconds after the game is ready."**
Wait 30 seconds after the world loads, then try again.

**"No background totals yet." or "There's no total to read right now."**
Background totals arrive every 30 seconds. Wait, then try again. Check that detail is not off (`!monitor detail grouped`).

**`!monitor mine` says "None of your grids were in the latest total."**
Your grids did no work in the last 30 seconds. A grid that does nothing costs nothing.

**`!monitor test` is refused.**
The reply says why:
- "You can test again in N s." Your own cooldown is not over.
- "Someone else just ran a test." or "A measurement just ran." Wait for the time it gives.
- "The server is busy." Sim speed is below the lowest sim speed for tests.
- "You can only test your own grids." The grid belongs to someone else.

**"--this needs you to be controlling a grid."**
Sit at the controls of the grid (a cockpit or a remote control), then try again.

**`--takeme` says "Leave your seat first."**
Stand up from your seat, then try again.

**Clusters are not timed.**
Another plugin changes the same physics code, so Monitor refuses to guess. `!monitor health` shows what was taken over.

**`!monitor health` shows "Taken over" or "Unavailable".**
Another plugin rewrote some calls that Monitor times, or a game update changed them. Monitor skips only those parts. Everything else keeps working. Runs say when some numbers read low because of this.

**A spike right after a player joins is not an alert.**
That is a join hitch. It is expected. See [Join hitch](#join-hitch).

**The `On` column shows `-`.**
No time was recorded for that row in that measurement.

**A thread change shows `#` and a number instead of a name.**
The name is not known yet. It appears when that object shows up in a normal list.

**Discord gets no posts.**
Click **Send a test** on the Incidents tab. Check that the URL is a Discord webhook address. Check Quasar's log for a webhook error.

**Discord replies from a bot show `*` marks.**
Some bots (for example SEDiscordBridge) escape formatting. The reply is still readable.

**The Monitor page in Quasar is empty.**
Quasar collects every 30 seconds. Wait a minute. Check that the server is connected and that the Magnetar plugin is installed on it.

**The Monitor page stops showing new data for a server after an update.**
The Magnetar plugin and the Quasar plugin must come from the same build. Update both, then restart Quasar and the server.

## Building from source

The server plugin needs the Dedicated Server files and `PluginSdk.dll`.

1. Create a file `Directory.Build.props.user` next to `Directory.Build.props`.
2. In it, set `DS64` to the Dedicated Server's `DedicatedServer64` folder, and `MagnetarBin` to Magnetar's folder. You can also pass them on the command line.
3. Build `Monitor.slnx`.

- `tools/Package-Local.ps1 -QuasarInstall <your Quasar app folder>` builds the Quasar package in `artifacts/senx.monitor`. Add `-Destination` to build straight into a Plugins folder.
- `samples/PreviewHost` shows the Quasar pages with made-up data, without Quasar or a game.

| Project | Holds |
|---|---|
| `src/SenX.Monitor.Core` | The measuring code. It has no game references, so it can be tested anywhere. |
| `src/SenX.Monitor.Magnetar` | The server plugin. On Magnetar, the loader builds it from `Monitor.xml`. |
| `src/SenX.Monitor.QuasarCompanion` | The small part that Quasar sends to each server. It passes Quasar's requests to the server plugin. |
| `src/SenX.Monitor.Contracts` | Message types shared by the server plugin and Quasar. |
| `src/SenX.Monitor.Api` | The public API for other plugins. |
| `src/SenX.Monitor` | The pages and the history store. |
| `src/SenX.Monitor.Quasar` | The Quasar plugin. `quasar-hub.xml` publishes it. |
| `src/SenX.Monitor.Torch` | The unsupported Torch build. See [Torch build (unsupported)](#torch-build-unsupported). |

More technical details are in [docs/Phase1.md](docs/Phase1.md) to [docs/Phase7.md](docs/Phase7.md) and [docs/Charts.md](docs/Charts.md).

**Unsafe code:** the measuring code uses `unsafe` code and native memory. This is intended. The garbage collector never sees those buffers. Monitor ships no native files. The memory comes from `Marshal.AllocHGlobal`.

## Torch build (unsupported)

There is a Torch build in `src/SenX.Monitor.Torch`. It is only available as source code on GitHub. It is not supported, and it gets no new features. Quasar and Magnetar are the supported platforms. If you build it anyway, see [docs/Torch.md](docs/Torch.md).

## License

MIT. See [LICENSE](LICENSE).

Monitor's charts use [uPlot](https://github.com/leeoniya/uPlot) 1.6.32 by Leon Sorokin, under the MIT licence. The unchanged release files and their licence are in `src/SenX.Monitor/wwwroot/lib/uplot`.
