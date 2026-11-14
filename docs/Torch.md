# Monitor on Torch

The whole of Monitor for one Torch server: measuring, history, reports, commands, and the pages and charts in Torch's own window, with full chart boards in a browser. There are no clusters, because there's no Quasar.

**Status: tested under Torch on a test server, with no players.** It hasn't run on a real server yet. See [What's left](#whats-left).

Torch's own IL reader reads all 54 methods Monitor rewrites, from the game DLLs Torch uses, and a rewrite in memory changes all 84 call sites at the expected count. The fingerprints come from that same reader.

## What's built

| Part | Where |
|---|---|
| The plugin Torch loads | `src/SenX.Monitor.Torch/MonitorTorchPlugin.cs`, with `manifest.xml` |
| The worker: hooks, census, runs, incidents, reports | Linked from `src/SenX.Monitor.Magnetar`, unchanged |
| Patching on Torch's `PatchManager` | `Worker/MsilRewriter.cs`, `PatchEngine.Torch.cs`, `DrillDown.Torch.cs`, `PatchAudit.Torch.cs`, `TorchHost.cs` |
| Chat commands | `MonitorCommands.cs` |
| History, charts, incidents, baselines, Discord alerts | Linked from `src/SenX.Monitor/Services`, unchanged |
| Charts | `Ui/ChartView.cs`, `ChartBoard.cs`, and `Figures.cs` for donuts and the gauge |
| Full chart boards | The same page Quasar serves, from `Web/BoardWebServer.cs` |
| Eleven tabs | `Ui/Tabs`: Overview, Tree, Costs, Mods, Network, World, Incidents, Compare, Patches, Inside, Runs |
| A preview with no Torch or game | `samples/TorchPreview`, fed by the web preview's simulated server |
| The fingerprint tool | `tools/SenX.Monitor.Torch.Lock` |

It isn't in `Monitor.slnx`, so the Quasar and Linux builds never see it. It has its own solution, `Monitor.Torch.slnx`.

## How it's put together

**Most of the code is shared, not ported.** About 3,700 lines of history and chart code and 6,000 of worker code compile as they are. The worker only touches Harmony and Magnetar in a few places, and those now sit in `*.Harmony.cs` and `*.Magnetar.cs` files beside what's shared, with Torch's versions in `Worker`. The frame loop moved from `MonitorPlugin` into `Worker.cs`, so both hosts run the same one. Magnetar's fingerprints came out identical after that change, so its table reads the same as before.

What net48 lacks is in `Shims`, as small stand-ins for what the shared files call. This adds no assemblies, which matters because Torch's binding redirects are fixed and a plugin can't add to them. The zip holds the manifest and Monitor's own DLLs, each with its PDB.

**Patching uses Torch's own system, with no Harmony anywhere.** The Torch DLL has no reference to it. Harmony is only for the Magnetar build. If another plugin loads Harmony on a Torch server, it will break Torch's patching, so Monitor logs an error naming the plugin where it can tell. It only checks the assembly's name, and never calls into it. The table of what to measure is shared, and `MsilRewriter` makes the same three edits as the Magnetar build, on Torch's `MsilInstruction`. Some things about Torch's system shaped this:

- **Post-transpilers, not priorities.** Monitor has to run last, so other plugins' changes are inside what's timed and they never see Monitor's edits. Priority can't do that on Torch: its sort overruns its list when a low priority entry comes before a higher one, so any transpiler added to the method later would throw during world load. Post-transpilers run after everything. One that's first in a method's list takes the top priority, which nothing added later can be above, and one joining a list stays at the default, which is safe beside other defaults. Monitor won't add one beside a negative priority, because by the time Torch's sort throws it has already overwritten its list, which loses the other plugin's entry. `PatchContext.RemoveAll` leaves post-transpilers behind, so Monitor takes its own out by hand.
- **Drill-down is a normal transpiler,** so it runs first and sees the method's own calls, not Monitor's hooks.
- **Monitor commits for itself, one method at a time, at `TorchGameState.Created`.** Plugins load before the game exists, and Torch's own commit runs inside `MySandboxGame`'s constructor, where a failure would stop the server. Monitor waits until the game's static constructors have run and clears whatever else is waiting. Then it commits each method separately, because a commit stops at its first failure and doesn't say which method that was. A method that fails costs its own scopes, the rest still go in, any other plugin's patches on it are put back, and nothing is left waiting for a commit Monitor doesn't control. This adds about 160 lines to Torch's log at startup.
- **A rebuild re-runs every transpiler,** including when another plugin changes a method later, so sites are counted afresh each time.
- **Torch can drop a `leave`.** When it writes a method back out, it drops the `leave` at the end of a `try` and uses the one the IL generator adds, which goes to the end of the handlers. If the original went somewhere else, the method changes: in `try { A(); return; } catch { } B();`, B would run after a success. One of Monitor's 54 methods has a `leave` like this, `MySandboxGame.Update`. Torch only drops one right before a handler, so Monitor's rewrites, drill-downs, audits and plugin timing all put a `nop` after it, which keeps it, and nothing ever reaches the `nop`. `tools/SenX.Monitor.Torch.Lock` checks that none are left in the 54 methods.
- **Audits are one post-transpiler.** Once a method is whole, with its prefixes and suffixes in place, the clock can be read around them with no shims and no priorities. So an audit can't trip Torch's sort, and it works on a method on a generic class.
- **Torch nests sibling catches.** It reads a `try` with several `catch` blocks as several tries and writes them back nested, so an exception thrown inside the first catch could reach the second. None of Monitor's 54 methods have this, and the tool checks. Drill-downs and plugin timing refuse a method that does. Audits don't need to, because a method being audited has already been rebuilt for the plugin that patched it.
- **Fingerprints are Torch's own.** Its reader and Harmony's don't see quite the same instructions, so `tools/SenX.Monitor.Torch.Lock` writes a separate table. Run it after a game update.

```bash
tools/SenX.Monitor.Torch.Lock/bin/Release/net48/SenX.Monitor.Torch.Lock.exe "<TorchDir>" --write-lock src/SenX.Monitor.Torch/Worker/Fingerprints.Generated.cs
```

**The charts are drawn by hand.** Blazor can't run on net48. WebView2 isn't on many Windows Servers, and a chart library would bring assemblies the redirects don't cover. `ChartView` takes the same `ChartData` the browser gets, so a chart means the same thing on both: stacked bands, overlay lines, the second scale on the right, broken lines at gaps, timelines and dashed markers. The plot and the cursor are separate layers, so moving the mouse doesn't rebuild the lines.

**The pages are built in C#, not XAML.** A mistake in XAML only shows up as a crash when the page opens in Torch. In C#, the compiler checks every property and binding name.

**The plugin's window keeps the basic charts, and the detail is in a popup.** WPF can't reach what a browser can with charts, so its tabs hold the same few charts as Quasar's page, and **Open full charts** opens the same board Quasar shows, in the admin's own browser, with nothing to install and no Node.js. A small web server inside the plugin serves it. [Charts.md](Charts.md#full-boards) describes the board. The server is about 300 lines over a plain socket:

- **It only starts when someone opens a board,** and stops with the plugin. Until then there's no port and no thread.
- **It only listens on this computer,** on `127.0.0.1` and a port the system picks.
- **Every address holds a random secret,** made new on each start. Without it, nothing is served.
- **It checks the `Host` header,** so a web page can't reach it by pointing a name it owns at `127.0.0.1`.
- **It only reads what's collected.** Anything but GET is refused, and only the page, its four files and the board's data can be asked for. Having a board open does make history refresh every second, the same as the plugin's own window.
- **It runs on 4 threads at below normal priority,** which sleep until asked. A connection that sends nothing times out after 2 seconds, and a whole request has to arrive within 5, so a stalled one holds up a thread for that long at most.
- **A board's data is written straight to the connection,** so a big one never becomes one big string in the game's process.
- **The address stays on show beside the button,** in case the server has no browser of its own to open.

It uses a plain socket because `HttpListener` needs a URL reservation or admin rights, and http.sys isn't there under Proton. Reaching a board from another computer would need real sign-in, so it can't be done yet. Until then, a remote desktop session or an SSH tunnel to the port does it.

**Colours come from Torch's theme.** Torch hosts a plugin in a Frame, which stops text colour being inherited, and it clears a control's merged dictionaries on each theme change. So text and background follow the theme's own keys, surfaces are a grey that suits light and dark, and chart colours are the page's fallback colours.

**A failed page can't take the server down.** Torch has no handler for its UI thread, so anything that escaped these pages would end the process. `MonitorView` catches what comes from Monitor's own code, logs it and says so on the page. The collector does the same for its side: it logs a failure and starts again a minute later. A number that isn't a number is drawn as a gap, because one reaching WPF's render thread can end the process too.

## How it differs from Quasar

- **No Servers tab, and no server picker.** There's one server.
- **No Entities page additions.** The cost column and "Profile this grid" are extensions of Quasar's own page.
- **No permissions.** Whoever can open Torch's window can do everything.
- **No `IMonitorApi`.** That's for Quasar plugins. `IMonitorServerApi` gives Torch plugins the same tools, over all of Monitor's history, because it's all here, with `Latest()` and what a run is doing read straight from the worker.
- **Charts refresh every 2 seconds, not 1,** because they're built inside the game's process. Ranges over an hour still refresh every 30 seconds.
- **Dragging across a chart zooms every chart on the tab.** A double click goes back. On the web page, this is per chart.
- **Tables sort when you click a header.** It came free with the control, but there's no arrow showing which way, because the header's arrow goes with its default look.
- **A legend only shows values.** Clicking one doesn't hide a line, as it does on the web page.
- **A row's grids open by selecting the row,** on lists that have them, rather than with a disclosure arrow.
- **Times in Compare are typed,** because WPF has no date and time picker.
- **Discord alerts have one webhook,** with no "for all servers".
- **History is under `<Instance>/Monitor`.** Its JSON files aren't interchangeable with Quasar's.
- **The Patches tab reads Torch's private table of patches,** because it has no public list. If a later Torch moves it, the tab comes back empty, and physics cluster timing refuses to run rather than guessing.
- **Drill-down can't patch a method on a struct,** because Torch's rebuilt method takes its instance as an object.
- **A run's report reaches a caller in one of two ways.** Torch's console and a bot that subscribed to replies, as Orion and SEDiscordBridge do, get it when it's ready. A command from any other thread is held until the report has gone, so it's in the replies Torch returns. A bot that calls on the game thread without subscribing has nothing listening when the report goes out, and gets no reply.
- **Commands read their options themselves,** because Torch can't fill a parameter array. `!monitor everything`, `trigger` and `detail` take on, off or a mode name as typed.

## Limits

- **A post-transpiler added after Monitor's runs after it and sees its edits.** The Profiler adds its own after Monitor does, which is one reason Monitor warns when both are installed. On a method that already had one when Monitor joined the list, a later positive priority would still hit the overrun in Torch's sort.
- **A method with an exception filter can't be drilled into,** because Torch can't read or write filters.
- **Rebuilding a method while the game runs** writes a 12-byte jump that isn't atomic. Drill-downs and audits do this between frames on the game thread, as Harmony does, so a method running on a worker thread at that moment is the risk.
- **Monitor's `Dispose` only runs on a plugin reload,** because Torch ends the process to stop or restart. Nothing is lost beyond the last few seconds of history, and up to 30 seconds of an open incident's worst figures if the process is killed.

## What it costs the game

On Quasar, all of this runs in another process. On Torch it shares the game's, and a garbage collection stops the game's threads whoever caused it. So:

- **Only the tab on screen does anything.** Tabs aren't built until first opened, and the clock stops when the page isn't showing or Torch is minimized.
- **Almost nothing runs on the game thread.** Collection, chart building and file reads are all on other threads, and long reads don't hold up the page. What has to touch the game goes there briefly, in small batches: looking up names for new rows, and starting a run, a drill-down or an audit.
- **A full board only costs anything while it's open and showing.** Its size depends on the range, from 6 ms and under 1 MB for 5 minutes to about 1.5 s and 230 MB for 3 days. That garbage lands in the game's process, so long ranges refresh every 2 minutes. [Charts.md](Charts.md#what-a-board-costs) has the figures.
- **Tables only rebuild when their rows change,** which for most lists is once in 30 seconds.
- **Collection keeps running with the page closed,** every 30 seconds, as it does on Quasar. That's what lets a problem overnight be looked at in the morning. About an hour of second-by-second rows stays in memory, and the rest is on disk.
- **History still goes through the same format Quasar reads,** rather than reading the rings directly. It's one path to prove, and it can change later if it shows up.

None of this has been measured. Monitor's Overview shows the game's GC and allocation, so its own effect can be read there once it runs.

## What's left

It has run under Torch on a test server, with no players. That covered loading from the zip, every site patched, `!monitor status`, runs from a command with physics clusters timed, and grids, identities and factions removed during a run.

What still needs a real server, in order:

1. Do charts fill in, and do the cursor, zoom and double click work? The pages have only been opened in `TorchPreview`. Does **Open full charts** open a browser, and does the board keep up under a real server's data?
2. Do a run, a drill-down and an audit started from the pages all go in and come back out, leaving the method working? Each one rebuilds a method while the game runs.
3. Do commands reply properly from the game, the console and a bot, including a run's late reply?
4. Do `!monitor mine` and `!monitor test` work for a real player?
5. Do the pages read properly on all five of Torch's themes?
6. Does a chart stay usable on a server with no graphics card, where WPF draws in software?
7. Are saved settings, runs and incidents read back after a restart? They go through the Newtonsoft stand-in, so this is the one place the shared code isn't quite the same.

## Building

`DS64` finds Torch's folder, and `TorchDir` overrides it.

```bash
dotnet build src/SenX.Monitor.Torch/SenX.Monitor.Torch.csproj -c Release
```

A Release build writes `artifacts/torch/Monitor.zip`, which goes in Torch's `Plugins` folder. With `LocalPlugins` on in Torch's config, that's all it takes. With it off, Torch skips a zip that isn't listed in `torch.cfg`, so its GUID, `f58cf508-46b1-4964-9836-fd42429adbe0`, needs adding there, unless `torch.cfg` lists no plugins yet. To see the pages with no Torch or game:

```bash
dotnet run --project samples/TorchPreview/TorchPreview.csproj -c Release
```

## Commands

Torch finds where a command's words start by searching the whole line for the first word, so the "on" of `!monitor everything on` is found in "monitor". Monitor gets the command's own name back as words, and `CaptureArgs.WordsAfter` takes it off again. `everything` and `trigger` read their words the same way instead of taking a parameter, because a parameter gets what Torch found.
