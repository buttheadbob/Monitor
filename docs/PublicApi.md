# Public API

Other plugins can read Monitor's data and ask for runs. There are two ways in, and both lead to the same tools for one server, `IMonitorTools`:

| | For | Reaches | Get it from |
|---|---|---|---|
| `IMonitorApi` | Quasar plugins | Every server Quasar has data on, connected or not | Quasar's services, then `For(server)` for one server's tools |
| `IMonitorServerApi` | Server plugins, on Magnetar and Torch | Only the server it's running on | `MonitorServerApi.Current` |

Everything is in `SenX.Monitor.Api`, which is netstandard2.0 so Legacy servers can load it.

The tools work the same everywhere, and they reach as far as the host's data does:

| | Quasar plugin | Torch plugin | Magnetar server plugin |
|---|---|---|---|
| History | Up to the retention setting: 3 days by default, 7 at most | The same | About an hour, which is what the worker holds |
| Saved runs | All of them | All of them | The latest one |
| Incidents | Yes | Yes | No, because Quasar keeps those |

Monitor itself has been tested on all three hosts, but no other plugin has called this API on a real server yet.

`samples/ApiConsumer` is a working example.

## Referencing it

Monitor is open source, so link straight to its project and don't ship the assembly:

```xml
<ProjectReference Include="..\Monitor\src\SenX.Monitor.Api\SenX.Monitor.Api.csproj" Private="false" ExcludeAssets="runtime" />
```

Quasar loads every plugin into one context, and resolves assemblies by name from every enabled plugin's folder. So a copy in your plugin's folder could load in place of Monitor's, and an old one could break Monitor itself. Monitor brings the one copy.

On MagnetarHub, put Monitor's id, `f58cf508-46b1-4964-9836-fd42429adbe0`, in `DependencyIds`, so Monitor is enabled with your plugin.

### Plugins a hub builds

This isn't solved yet. Linking to the project works for a plugin you build yourself, such as a Torch zip. A hub builds a plugin from its own repository, so `..\Monitor` isn't there, and MagnetarHub's loader gives a plugin no reference to any other plugin. A plugin published through a hub can't compile against this project as things stand.

On Magnetar, the loader compiles Monitor's API into Monitor's own plugin, under a name of its own. Monitor answers requests for `SenX.Monitor.Api` with that assembly, so that a plugin built against the project finds it there, but that hasn't been tried with a real plugin.

## Monitor might not be there

Nothing in `quasar-plugin.json` can require another plugin, so Monitor might be missing or disabled. Check first, using only its name, and keep everything that names Monitor's types in a component you only use after the check:

```csharp
private const string ApiType = "SenX.Monitor.Api.IMonitorApi, SenX.Monitor.Api";

protected override void OnInitialized()
{
    Type? api = Type.GetType(ApiType, throwOnError: false);

    if (api != null && Services.GetService(api) != null)
        _panel = Panel();
}

// Kept apart, so the runtime only loads Monitor's types once they're known to be there.
[MethodImpl(MethodImplOptions.NoInlining)]
private static Type Panel() => typeof(MonitorPanel);
```

Then show it with `<DynamicComponent Type="@_panel" />`. Inside `MonitorPanel`, `@inject IMonitorApi Monitor` is fine.

Don't resolve anything in `ConfigureServices`. Plugins register in no fixed order, and services only exist once they've all finished.

## On a server

A server plugin has no shared services to ask, so Monitor registers itself with a small locator:

```csharp
IMonitorServerApi? monitor = MonitorServerApi.Current;

if (monitor != null)
{
    MonitorReply reply = await monitor.RequestAsync(new MonitorRequest
    {
        Source = MonitorSource.Run,
        Lists = [MonitorList.Grids],
        Seconds = 10,
        RequestedBy = "MyPlugin",
    }, cancellationToken);
}
```

- `Current` is null until the game is ready, and if Monitor has shut itself down, so read it each time and don't keep it.
- It only ever answers about this server. `Server` in a request is ignored, and `Server` on the tools and on a sample is empty on Magnetar and `local` on Torch. For any other server, use a Quasar plugin.
- On Magnetar, a plugin that wants more than an hour can listen to `SampleRecorded` and keep what it hears. Samples can be read for about 90 minutes, but timings only go back about an hour, so older ones have no `Frame`, `PartsMs`, `Seconds` or `Frames`.
- Never wait on a task from this on the game thread. Some answers need the game thread to finish, so `.Result` there would freeze the server. Use `await`, which is safe anywhere: everything that can take a while happens off your thread.
- Events are raised off the game thread, so don't touch the game from a handler.

### On Torch

Torch plugins use this same API, in the same way. Monitor's Torch build holds the whole history itself, so these tools read all of it, with the same code Quasar's do, and `Latest()` reads the worker directly, so it's as fresh as Magnetar's. `Server` is `local`. `IMonitorApi` is for Quasar plugins, so it isn't there.

Link to the project without shipping it, as above, and list Monitor in your `manifest.xml`, so Torch loads it first:

```xml
<Dependencies>
  <PluginDependency>
    <Plugin>f58cf508-46b1-4964-9836-fd42429adbe0</Plugin>
  </PluginDependency>
</Dependencies>
```

## Permissions

The API can't see who's asking, and plugins aren't sandboxed, so it can't be a security boundary. On Quasar, your page or endpoint has to check, as Quasar requires of every action. On a server, your own command's permission level does that job:

| Doing this | Needs |
|---|---|
| Showing anything from Monitor | `CanView` |
| Asking for a run, looking inside a method, or an audit | `CanControlServers` |

Each of those needs `RequestedBy`, which goes in the log, and on the saved run for a run, so an admin can see who started it.

## The tools

Nothing here changes how the server runs unless it says so. Times are UTC and values are in ms.

Reading what's been collected doesn't measure anything, so it adds nothing to frames. Through Quasar it doesn't reach the server at all. On Torch it reads files inside the game's process, off the game thread. On Magnetar names come from the game, so a read that returns rows takes a little of a frame the first time it sees them.

**Live.** `Latest()` and `Status()` don't wait for anything being written, so they can be called as often as you like, on any thread. The one exception is the first `Status()` for a server that hasn't sent anything since Quasar started, which has to look it up.

| Tool | Gives |
|---|---|
| `Latest()` | The frame, what it went on, the network and the world, or null |
| `Status()` | Whether it's online, when it started, whether a run is going, how far back history reaches |
| `PlayersAsync` | Who's online, each one's ping and traffic, as of the last census. Empty for a server that's gone |
| `FactionsAsync` | Each faction's size, as of the last census. Empty for a server that's gone |
| `TopAsync` | The costliest rows of a list, from the latest whole background total |

On Magnetar, those last three take names from the game, in small batches, and remember them for a minute.

**History.** These read what's already been collected.

| Tool | Gives |
|---|---|
| `SamplesAsync` | Background totals across a range, oldest first, each with the server's figures from that moment |
| `HistoryAsync` | Timings, world figures and events across a range, with no lists |
| `CostsAsync` | What was costing time across a range, as one total |
| `CostHistoryAsync` | One row of a list over time, by id. Asking by name works for rows with no id, but two grids can share a name and they'd both come back |
| `EventsAsync` | Saves, restarts, changes, lag and runs across a range |
| `CompareAsync` | Two ranges side by side, such as before and after a change |
| `ChartImageAsync` | One chart as a PNG file's bytes, on Quasar and Torch. See Chart images below |

**Runs.**

| Tool | Gives |
|---|---|
| `MeasureAsync` | Measures now and answers when it's done. See Runs below. This costs the server while it runs |
| `CurrentRun()` | The run going now, who asked and how long is left. Who asked is only known for a run from the API or Monitor's page. Quasar only hears of a chat command's or a lag trip's at its next pull |
| `SavedRunsAsync` | Finished runs, newest first, a lag trip's included |
| `SavedRunAsync` | A finished run, by id or the newest |

**Going deeper.** Looking inside and auditing rebuild methods on the running server. One goes at a time, each comes out by itself, they run for at most 120 seconds, and plugins get one a minute.

| Tool | Gives |
|---|---|
| `IncidentsAsync` | Lag noticed in the history kept, newest first |
| `FindMethodsAsync` | Methods that can be looked inside, by part of their name. A search takes a game frame, so they're 10 seconds apart, and asking again sooner waits its turn |
| `LookInsideAsync` | Times what a method calls, and answers when it's done |
| `PatchesAsync` | Which methods other plugins have patched, and how |
| `AuditAsync` | Splits up to 8 patched methods into plugin prefixes, the original and plugin postfixes |

Only the latest result of each of those two is kept. If a newer one finishes first, yours comes back as `Failed` and says so.

`RequestAsync` below is still there, and it's what most of these are built on.

### Chart images

`ChartImageAsync` draws one chart and hands back the bytes of a PNG file. Monitor doesn't send it anywhere, so it's yours to attach to a reply, save or show. Check `Version` is at least 2 first.

```csharp
byte[]? png = await tools.ChartImageAsync(MonitorChart.FrameParts, from, to, 900, 420, cancellationToken);

if (png != null)
    await myBot.SendFileAsync(channel, "frame.png", png);
```

- **What's on it.** The same data as Monitor's own charts: stacked bands, the frame budget, sim speed on its second scale, gaps, and a dashed line for each event. `MonitorChart` lists what can be drawn, and the costs are background totals, so they're for trends. Times are UTC, because a picture doesn't know its reader's clock.
- **Null means nothing to draw,** such as a range with no history, or one that ends before it starts. A server plugin on Magnetar always gets null, because the worker only holds an hour and drawing would add to the game's own process. Plugins on Torch and Quasar get pictures.
- **Sizes** are kept between 320 by 200 and 1920 by 1080.
- **It's drawn by Monitor,** in plain managed code, so nothing extra is needed on the server. The letters are its own simple line font, which only has plain ASCII, so other alphabets show as question marks. It won't look quite as polished as the full boards.
- **Images are drawn one at a time, across all servers,** off the caller's thread, so asking for many just takes longer. Reading a long range costs what a chart costs. On Torch that's in the game's process, so don't ask for images on a timer.

### Where each live number is

A status page refreshing every 15 seconds only needs `Latest()` and `Status()`, and neither reaches the game.

| Number | Where |
|---|---|
| Online | `Status().Connected` |
| Uptime | `Latest().World.Uptime`, or the time since `Status().Started` |
| Sim speed | `Latest().Frame.SimSpeed` |
| Players online | `Latest().World.Players` |
| CPU | `Latest().World.CpuPercent`, the server's share of the CPUs it can use since the census before, which is the whole machine unless it's pinned or in a limited container |
| RAM | `Latest().World.MemoryMb` |
| Ping | `Latest().World.PingMs`, the mean of the players' pings |
| Static and dynamic grids | `Latest().World.StaticGrids` and `DynamicGrids` |
| Total blocks | `Latest().World.Blocks` |
| Asteroids | `Latest().World.Asteroids` |

`World` is from the last census, which takes about 30 seconds to come round, and it's null until the first one has finished. On Quasar the frame figures can be 30 seconds old too, unless `KeepFresh` is being called. On a server plugin they're a second or two old.

### Samples

Monitor takes these by itself, so there's nothing to schedule. It totals what background detail measured every 30 seconds, and keeps one as a sample every 2 minutes, or every minute from when sim speed drops below 0.7 until it's been back above that for 5 minutes. A run stops background totals while it measures, so a long run leaves a gap. A server with detail turned off takes none.

```csharp
IReadOnlyList<MonitorSample> samples = await tools.SamplesAsync(
    DateTime.UtcNow.AddHours(-6),
    DateTime.UtcNow,
    [MonitorList.Grids, MonitorList.Players],
    top: 5,
    cancellationToken);
```

Each sample has `Frame` and `PartsMs` for the stretch since the sample before, `Seconds` and `Frames` for how much time that really covers and what was simulated, which leaves out the server being away or paused, `World` with the numbers above from that moment, and its lists. A grid's `Id` stays the same from one sample to the next, so a chart line can follow it. Cost is `MainMs + WorkerMs`. Clusters aren't in samples, because timing them changes how physics is scheduled, so only a run has them.

`SampleRecorded` raises each one as it's kept, with every list and 20 rows, for a plugin that would rather keep its own. Those are the same samples `SamplesAsync` reads later, and a plugin that wants them all from then on can rely on hearing each one, unless it falls so far behind that the oldest are dropped.

## One request, one reply

`RequestAsync` is one way to ask for lists of costs, and every source gives the same reply, so a plugin can move between history and a run without new code. `MeasureAsync`, `CostsAsync` and `SavedRunAsync` are the same thing by name. `SamplesAsync` and `TopAsync` read background totals one at a time, and have their own shapes.

```csharp
MonitorReply reply = await monitor.RequestAsync(new MonitorRequest
{
    Server = server.Id,
    Source = MonitorSource.History,
    Lists = [MonitorList.Grids, MonitorList.Factions],
    From = DateTime.UtcNow.AddHours(-1),
    To = DateTime.UtcNow,
}, cancellationToken);
```

| Source | Gives | Costs the server |
|---|---|---|
| `Run` | Measures now, with every call timed. This is what settles a lag cause | Timing every call for its length |
| `History` | Totals over a past range, plus timings, world figures and events across it | Nothing |
| `SavedRun` | A run that already finished, by id or the newest | Nothing |

`Lists` picks what comes back, and leaving it empty means all of them. A request covers one server, so several servers take one request each.

**A reply always has the same shape.** `State` says `Finished`, `Refused` or `Failed`. A normal "no" is a reply, not an exception: server gone, a run in the way, the limit below, or the server paused. A refusal has a `Message`, and `RetryAfter` when that's known. A failure's `Message` says what went wrong. An exception means you cancelled.

**Check `Accuracy`.** `EveryCall` means a run. `Background` means history, which comes from one group of work at a time. It's good for trends, so don't present it as a lag cause.

**Each list says if it could answer.** `Available` is false when a source can't, and `Why` says why, so that isn't mistaken for an empty list.

- Clusters aren't in history.
- Clusters are only in a run that timed them.

`Rpcs` is the costliest RPCs from clients, in runs and in history, with a row's time covering checking the RPC and running it. `RpcsSent` is the biggest the server sent. Those rows hold no time: they have `BytesPerSecond`, across everyone who got each one, and `CallsPerFrame` is events.

**Rows are the same from every source:** name, main ms/frame, worker ms/frame, peak ms and calls per frame. `Id` is there for grids, players, factions and scripts, whose ids outlast a restart. Rows for nobody and for no faction don't have one. Each list is costliest first. A run has the top 20 of each, and a reply from history has the top 20, or 40 for mods. Two grids with one name stay two rows, comparing joins rows on their ids, and a grid's `Name` is the same from every source.

**Rows say how big they are.** `Blocks` is on grids, players and factions, and `Grids` on players and factions, going by who owns most of each grid. A run reads a grid's blocks when it ends, and everything else comes from the last census. A cluster has `Grids`, `SizeKm` and its costliest grids in `Members`, and its index is its place in the list.

**`PartsMs` says what the frame went on,** by category such as Physics, Entities or Network. Each ms is in one category only, and `Idle` is what was left of the frame's budget. A run has it, and so does a reply from history.

**`SeenShare` matters for history.** Background totals keep each sweep's top 20, or top 40 for mods, so a row outside that has no figure there. Its times are averaged over the sweeps it appeared in, and `SeenShare` is what share that was. A row at 0.1 was in the top rows a tenth of the time, so it isn't a steady cost. It's always 1 for a run.

**A cluster's time isn't per frame.** `PerFrame` is false on that list, because it's one step with all physics threads to itself. Clusters rank each other, and add up to more than physics really took.

## Runs

- **Every run measures everything,** so a list only picks what comes back. The saved run keeps them all.
- **Clusters only run when asked for,** by naming that list or asking for all, because they change physics scheduling.
- **Requests share a run.** Two plugins asking for different lists get one run, as long as it has no filter and covers clusters if they're wanted. A request that can't share is refused, with the time left.
- **A run can be limited** with `GridId`, `Player`, `Faction` or `BlockType`, the same as in chat.
- **Plugins get one run a minute on a server,** counted from the end of the last one. Any other run counts too, a lag trip's included, and a refused request doesn't use up your turn. This is there because a run costs the server, whoever asked. Through Quasar, a chat command's run only counts once Quasar has heard of it, at its next pull.
- **The longest run is 600 seconds.**
- **Cancelling only stops your wait.** The run finishes and is saved.

## Other reads

| Method | Gives |
|---|---|
| `Servers()` | Every server with data, and whether it's connected. A server's `Id` stays the same when a cluster node is replaced |
| `For(server)` | That server's tools. Everything that reads history works for a server that isn't connected |
| `Latest(server)` | The same as the tool of that name |
| `SavedRuns(server)` | The same as the tool of that name |
| `KeepFresh(server)` | One-second collection for a few seconds. Call it again to keep going |

None of these reach a game server. Only a run, looking inside, an audit and the patch list do. Detail mode isn't offered, because it changes what a server pays for everyone.

## Events

| Event | When |
|---|---|
| `SampleRecorded` | A sample was kept. On the tools only, so a Quasar plugin picks a server with `For(server)` |
| `RunFinished` | A run finished, whoever started it, a lag trip included. A player's `!monitor test` isn't one of these, because it only times their own grid |
| `EventRecorded` | A save, lag, a run starting, or detail changing. Through Quasar or on Torch, also a restart or a change of plugins or mods, which a Magnetar server plugin can't see because it restarts with the server |
| `IncidentChanged` | Lag began or ended. Never raised on a Magnetar server plugin, because Quasar tracks incidents |

`IMonitorApi` raises the last three for every server, and a server's tools raise them for that server only.

These arrive on Monitor's own queue, one at a time, so a slow handler holds up other plugins' events and never collection. Keep them short, and hand real work off. A handler that throws is logged, and the next one still runs. If the queue fills, the oldest are dropped, so don't rely on hearing every one.

## Versions

- `Version` goes up with each addition. Check it before using anything newer than you need, because calling something the installed Monitor doesn't have throws `MissingMethodException`.
- Nothing is removed or changed. A breaking change would be a new interface beside this one.
- The assembly version stays at 1.0.0.0 while changes only add things, so a plugin built against a newer API still loads against an older Monitor.
- A test pins everything public, in `tests/SenX.Monitor.Ui.Tests/PublicApi.approved.txt`.
- Version 3 (`IMonitorServerApi` 2) adds `MonitorEventKind.JoinHitch`: one long frame just after a character spawned, while the game loaded voxel files. It's on the charts, and isn't an alert. Code that switches on `MonitorEventKind` should treat a value it doesn't know as information.
- Version 4 (`IMonitorServerApi` 3) adds which threads a row ran on. `MonitorRow`, `MonitorCostPoint` and `MonitorInsideRow` have `Thread`: `Main`, `Worker`, `Mixed`, or `Unknown` for no time and for clusters. `MonitorThreads.Of` is the rule: a side under 5% of the time doesn't count. `MonitorEventKind.ThreadJump` says something's time moved between the game thread and workers and held for two background totals, and is never an alert. A jump is named only from names Monitor already has, so its text can show `#` and an id. A total with more than 40 jumps adds one event saying how many more weren't listed. `MonitorRunDetails` gains `MainWaitMs`, the game thread's time waiting for workers, which can read slightly high because it includes handing work out, and `ParallelWorkerMs`.
