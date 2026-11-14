# Phase 4: Quasar collector and dashboards

Quasar now collects history from every connected server, stores it, and shows it on a Monitor page.

**Tested on a test server.** This has run in a real Quasar on a test machine, with one Magnetar server and no players, and its pages and boards have been opened there. It hasn't run on a real server yet.

## Gate

Real-host install, cluster fan-out, and every catalogue chart fed by real data. The install has been done in a real Quasar on a test machine, with one server. Cluster fan-out and real data haven't.

## What's built

- **Collector.** Runs inside Quasar whether or not anyone is watching. It pulls every connected server every 30 seconds, or every second while a page shows that server. A restarted server is noticed from its start time, and one failing server doesn't stop the others.
- **Storage.** Under `monitor/` in Quasar's install directory.

| History | Kept for |
|---|---|
| Seconds | 6 hours |
| Minutes, census passes, players each minute, background totals at 2-minute steps or 1-minute steps during lag, events and saved runs | The retention setting |

Retention is 3 days unless changed, and 7 at most. It's set on the page and needs `CanEditConfigs`, because shortening it deletes history. That's about 30 MB per server at the default, plus a little per player.

- **Names.** History holds ids, and the collector asks the worker for names through a new `identity.lookup` request, 256 at a time. Type keys change on every server restart, so rows are stored by name. That keeps a chart's line going across restarts.
- **Page.** `/monitor`, needing `CanView`, with a server picker and a time range from 5 minutes up to the retention setting. Turning off Live holds the charts and tiles still, so they can be studied.
- **Clusters.** Each node is its own server in the picker, grouped under its cluster's name. History is kept by cluster and slot, the same way Quasar's Analytics does it, so a replaced node carries on its slot's charts. To the collector, a replacement looks like a restart.
- **Charts.** Drawn in the browser with uPlot on a canvas. The page only says what to show, and the browser fetches `api/monitor/charts` over plain HTTP, needing `CanView`. So chart data never crosses the page's live connection. Short ranges refresh every second and long ones every 30, and fetching stops while the tab is hidden. Drag to zoom, double click to zoom out, and the crosshair follows across charts. Gauges and donuts stay SVG, because they're a few shapes.

| Tab | Shows |
|---|---|
| Overview | Sim gauge, available time, frame time, players, ping, blocks, PCU, then charts of what's using the frame, sim speed, available time, game loop time, frame percentiles and main thread now, then recent events |
| Costs | Any background list, including factions, as a donut, a line chart of its top 6, and a table |
| World | Who was online as a timeline, players, online members by faction, blocks, PCU, server and player ping, grids and objects, blocks and members by faction, players and factions |
| Servers | Every connected server side by side: a table of latest figures, then sim speed, available time, frame p99 and players as one line per server |
| Runs | Start a run, keep the results, and compare any two. Starting or deleting needs `CanControlServers`, which each action checks for itself |

**What's using the frame** is the main chart. It stacks physics, entities, network and the rest in ms/frame, with a dashed line at the 16.67 ms budget and sim speed on its own scale. When the bands reach the line, sim speed falls, and the band that grew is the cause.

**Available frame time** shows how much of the 16.67 ms is left, which is the warning sim speed can't give: sim stays at 1.00 until the room has run out. It has two lines, because an average hides spikes. The worst 1% line is the one to plan from. It's main thread time, so treat it as a guide when weighing up a feature or a player limit, not a promise: load doesn't always grow in a straight line with players.

Charts follow the old dashboards: legends sort by current value, series under 0.1 ms are hidden, and a series keeps one colour everywhere. Colours come from Quasar's palette, so charts follow its theme.

**Game loop time** shows update time against what's left of the frame, as two plain lines.

**Faction costs** are totalled by the worker at each sweep, from every owner and not just the top 20, and go through the same history as the other lists. Membership comes from the census, so someone who changes faction moves over within 30 seconds. Time on grids nobody owns, or owned by someone in no faction, is its own `<no faction>` row.

**Events** are dashed lines across a server's charts, and the Overview tab lists them in words.

| Event | Colour | Where it comes from |
|---|---|---|
| World saved, or the save failed | Purple | The worker, from `MySession.OnSaved`, at the end of the part of a save that holds up the game |
| Server started | Blue | Quasar, when a server's start time changes |
| Plugin or mod added, removed or changed | Orange | Quasar, comparing what a server says it started with against its last start |

Saves aren't drawn on ranges over 6 hours, where they'd hide the chart. A mod updating on the Workshop doesn't show, because the game keeps no version for it, and a plugin update only shows if its version number changed.

**The Servers tab** uses what the collector gathers every 30 seconds, so it can be that far behind. Pulling every server each second for one open tab isn't worth it. Servers don't tick together, so their rows are put in shared steps of time: at least 5 seconds for timings, and 60 for players. A step keeps a server's lowest sim speed and available time and highest frame p99, because a dip is what someone is comparing. Colours go by position in name order here, not by name, so two servers can't share one.

**Runs** are followed by Quasar itself, so closing the page doesn't lose one. The newest result opens by itself when it finishes. Comparing two sorts each list by what changed most. Lists hold each run's top 20, so a row in only one run shows a dash and no change, because missing isn't the same as zero. Runs started by chat commands are saved here too, once Quasar next pulls from that server.

## Preview host

`samples/PreviewHost` runs the pages with no Quasar and no game. Its server is simulated, but its numbers go through the real worker code: frame engine, sweep, census, runs, markers and history pull. There's only one server, because the worker code is process-wide, so the Servers tab shows one line and clusters can't be seen here.

```bash
dotnet run --project samples/PreviewHost/PreviewHost.csproj
```

## Changes from the plan

- **Quasar already loads uPlot on every page,** so Monitor uses that, and its own copy from the same version is only a fallback. Quasar's chart helper isn't a plugin API, so Monitor has its own small module.
- **History is kept for days, not a year.** There's no hour tier, because 7 days is the most anyone needs here.
- **Sim speed against block count is dropped.** Block count moves slowly and the crosshair already links charts, so it told admins little. What's using the frame against sim speed replaces it.
- **Available frame time is new,** and wasn't on the old dashboards.
- **Faction costs come from the worker,** not from joining in Quasar, because Quasar only sees the top 20 owners. A faction of many small owners would have read too low.
- **Comparing servers has its own tab.** The plan had an overlay on each chart, but most charts are per server by nature, and this needs lining rows up in time, which only makes sense where it's asked for.
- **Timeline markers came forward from Phase 5,** and the catalogue asks for them.

## Not built

From the chart catalogue:

- **Blocks vs Conceal,** built in Phase 7 from a flag Concealment already sets. See [Phase7.md](Phase7.md).

Patches, incidents, baselines and the Entities page additions are in [Phase5.md](Phase5.md).

## Tests to run

Written and compiling, but not run:

- `HistoryStoreTests`, `MonitorCollectorTests`, `ChartBuilderTests`, `MonitorRunsTests` and `MarkerTests` in `tests/SenX.Monitor.Ui.Tests`.
- `MarkerTests` and the faction tests in `SweepTests` in `tests/SenX.Monitor.Core.Tests`.

No tests yet: the Razor pages and charts. Open them in the preview host first, in light and dark, and at narrow widths. Check on the way:

- Tiles, tables and donuts update while Live is on, and hold still while it's off.
- Dashed lines land on the right moments, and the zoom doesn't lose them.
- The Servers tab's empty state, with one server and with none collected yet.

These also need a live server:

- `identity.lookup`, `runtime.info`, `run.start` and `run.result` through the real channel, and everything in [Phase3.md](Phase3.md#tests-to-run).
- A world save draws a line, and the plugin list holds what's really installed and nothing else.
- A cluster's picker groups, and a replaced node carrying on its slot.
