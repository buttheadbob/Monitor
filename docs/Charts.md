# Chart catalogue

What Monitor's dashboards need to show, taken from the Grafana and InfluxDB dashboards admins already use. The Quasar plugin draws all of these. The Magnetar worker only supplies numbers.

"Now" panels show the mean of the last 30 seconds. Over-time panels read from Quasar's history tiers.

There are two places to see them. The plugin's own page keeps a few charts on each tab, because Quasar gives a plugin limited room. The **full board** holds everything, in its own window. See [Full boards](#full-boards).

## Server health

| Panel | Form | Data | Source |
|---|---|---|---|
| Sim Speed (Now) | Gauge | Simulated frames against wall time | Tree, own clock |
| Sim Speed | Lines | Same | History |
| Game Loop Time | Lines | Update time against idle time, ms/frame | Tree |
| Main Thread (Now) | Donut | Physics, Entities, each session component, network, replication, parallel wait, GPS, other, idle | Tree plus detail |
| Main Thread (ms/f) | Stacked area | Same | History |
| What's using the frame | Stacked parts in ms/frame, with the 16.67 ms budget as a line and sim speed on a second axis | Tree | History |
| Available frame time | Lines: time left in an average frame, and in the worst 1% of frames | Tree and frame percentiles | History |

| Garbage collections | Lines | Gen 0, 1 and 2 each second | Tree |
| Main thread allocation | Line | MB allocated | Tree |
| Memory | Lines | Working set and managed heap, over days | Census |

## What is costing time

| Panel | Form | Data | Source |
|---|---|---|---|
| Sector Profiler (Now) | Donut | Time by entity type, including planets, voxels, characters, safe zones and mod logic classes | Group rollups |
| Sector Profiler (ms/f) | Stacked area | Same | History |
| Block Types (Now) | Donut | Time by block type: welders, PBs, projectors, refineries and so on | Group rollups |
| Block ms | Lines | Time per block type over time | History |
| Top Grid ms | Lines plus table | Slowest grids | Group rollups |
| Player ms | Lines | Time on grids a player owns | Group rollups, joined in Quasar |
| Faction ms | Lines | Same by faction | Group rollups, totalled by the worker from census membership |
| Mods and plugins | Lines plus table | Time by whose code it is, the costliest 6 charted | Group rollups, totalled by the worker |
| Inside a mod | Lines plus table | That mod's session components, game logic, callbacks and plugin Update, opened from its row | Group rollups |

## World and players

| Panel | Form | Data | Source |
|---|---|---|---|
| Online Player Count (Now) | Large stat | Players online | Census |
| Online Players (Now) | List | Names | Census |
| Online Players | Stacked timeline | Who was on, and when | History |
| Online Faction Members | Donut plus timeline | Players online by faction | Census |
| Total Blocks | Line | Blocks in the world | Census |
| Blocks by Faction | Lines | The 6 largest factions | Census, joined in Quasar |
| Blocks vs Conceal | Two lines | Active against concealed | Census, from Concealment's own flag on each grid |
| Player Ping | Lines | The 8 highest pings | Game, `MyClient.State.Ping` |
| Server Ping | Gauge | Mean of player pings | Game, same values |

A dedicated server has no ping of its own, because `MyGeneralStats.Ping` is only set on the client path. A mean of player pings fits the old gauge: 21.33 is what three whole-number pings average to.

## Network

Bytes are counted as they're handed to Steam or EOS and as they arrive, at all times.

| Panel | Form | Data | Source |
|---|---|---|---|
| Sent and received | Lines | KB/s, each second | Tree, by peer |
| What's being sent | Stacked area | State sync, events, replication and other | Same |
| RPCs from clients | Line | RPCs/s | Same |
| RPCs received, costliest 6 | Lines plus table | Time in each RPC | Group rollups |
| RPCs sent, biggest 6 | Lines plus table | KB/s, across everyone who got each one | Group rollups |
| Sent to each player | Lines | The 8 highest, in KB/s | Census |
| Waiting to reach each player | Lines | The 8 highest queues, in KB | Game, `BytesQueuedForSend` |
| Players now | Table | Down, up, RPCs/s, queued, refused and ping | Census |

## Across servers

One line per connected server, up to 30 seconds old.

| Panel | Form | Data | Source |
|---|---|---|---|
| Latest figures | Table | Sim speed, available time, frame p99, players, ping, grids, blocks, PCU | History |
| Sim Speed | Lines | Lowest in each step | History |
| Available frame time | Lines | Lowest in each step | History |
| Frame p99 | Lines | Highest in each step | History |
| Players online | Lines | Latest in each step | Census |

## Full boards

**Full charts** on the plugin page opens one server's board in a window of its own, and **All servers** opens the board for every server together. Each server can have its own window. On Torch, **Open full charts** opens the same board in the admin's browser.

A board is one plain web page, `src/SenX.Monitor/wwwroot/board.js`, and not a Blazor page. The server decides which panels there are, in `BoardBuilder`, and the page only draws what it's sent. So Quasar and the Torch plugin show exactly the same charts from one codebase. [ChartBenchmark.md](ChartBenchmark.md) has why it's uPlot plus own canvas and SVG.

| Board | Sections |
|---|---|
| One server | Overview (tiles and gauges), The frame, Costs, Where it goes (heatmaps and treemaps), Network, World, Events |
| All servers | A table of every server now, each linking to its own board, then the four shared charts, lost sim speed by server, and mods and grids by server |

Heatmaps and treemaps come from the same background totals as the cost charts:

- **Heatmaps** show the 20 costliest rows, with up to 720 steps of time. A step holds the worst value that fell in it, so a spike can't be averaged away. An empty cell means the row wasn't among the costliest then, which isn't the same as zero. The colour scale is a square root, so one huge spike doesn't turn everything else the same colour, and its labels show that.
- **Treemaps** show each row's average over the range on show. A row only in some totals is scaled by how much of the range it was seen in, so a brief spike doesn't read as a steady cost. Only mods nest, because their parts are stored with them. Grid and block type aren't stored together in history, and that breakdown is only in a run's results.

On a board:

- Dragging across any time chart zooms the whole board to that stretch, tiles and gauges included. A double click or **Reset zoom** goes back.
- The crosshair moves across every chart together, and the tooltip names any event near it.
- Clicking a legend row hides that line. Stacked charts don't, because hiding one band would leave those above it floating.
- Sections fold up, and stay that way in that browser.
- Refreshing stops while the window is hidden. **Live** off holds everything still.

A board keeps one dark look of its own, because it's outside Quasar's theme. Names come from players, so everything put on the page is escaped.

Plugins can also get any of the main charts as a picture. See [Chart images](PublicApi.md#chart-images).

### What a board costs

Building a board happens on the host, only while one is open. This is one board of 52 panels on three days of history, with 30 players and the top 20 rows of 11 lists every 2 minutes. It was measured on .NET 10 on a fast desktop with an SSD, so Torch's .NET Framework and a slower disk will read higher.

| Range on show | Refresh | Build and JSON | Allocated | JSON |
|---|---|---|---|---|
| 5 minutes | 3 s | 6 ms | 0.7 MB | 62 KB |
| 1 hour | 3 s | 21 ms | 4 MB | 429 KB |
| 6 hours | 30 s | 303 ms | 26 MB | 441 KB |
| 24 hours | 2 minutes | 1.1 s | 84 MB | 1.4 MB |
| 3 days | 2 minutes | 1.5 s | 230 MB | 3.6 MB |

Up to an hour comes from memory. Past that it reads files, once per board however many panels want the same thing. That's why long ranges refresh slowly. On Quasar this is in its own process. On Torch it's in the game's process, on a below normal thread, so holding a 3 day board open allocates about 2 MB a second until it's closed or hidden. The all servers board refreshes every 30 seconds up to 6 hours.

## Conventions kept from the old dashboards

- Legends sort by current value, highest first.
- Series under 0.1 ms are hidden, so a donut shows what matters.
- A series keeps one color everywhere: physics red, network and replication orange, session core blue, other purple, idle green.
- Time range presets, auto refresh and a server selector on every dashboard.
- Dashed lines on the timeline for restarts, saves and plugin or mod changes, listed in words on the Overview tab.
