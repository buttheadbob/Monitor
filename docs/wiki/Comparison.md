# Comparison

This page compares three tools that help you see how a Space Engineers server performs:

- **Quasar built-in**: the monitoring that comes with Quasar itself. Quasar's agent runs inside each server and sends numbers to Quasar.
- **Torch Profiler**: the "Profiler" plugin for Torch servers, from the Torch plugin site. Its source is TorchAPI/Profiler on GitHub. This page uses the newest source: tag v3.1.0, with the last change on 23 August 2023.
- **Monitor**: this plugin.

Every entry comes from the tool's own source code or documentation. "Not stated" means we could not find it there. It does not mean the tool cannot do it.

The tools are not in competition. Quasar's monitoring and Monitor work side by side on the same server. The Torch Profiler runs only on Torch, so you cannot use it on a Magnetar server.

## Words used on this page

| Word | Meaning |
|---|---|
| Run | The tool times every call on the server for a few seconds, then shows the result. |
| History | Numbers that are kept, so you can look back at them later. |
| ms/f | Milliseconds per frame: the average time something takes in each frame. |
| Game thread | The one thread that runs each frame. Only its time slows the frame directly. |

For other words, see the [Glossary](Glossary).

## Features

| Feature | Quasar built-in | Torch Profiler | Monitor |
|---|---|---|---|
| **Runs on** | Quasar, with its agent on each Magnetar server | Torch servers only (.NET Framework 4.8) | Magnetar servers (Windows Legacy and Interim, Linux Interim), with Quasar or alone |
| **How you use it** | Web pages in Quasar | Chat commands `!profile ...` | Chat commands `!monitor ...` and web pages in Quasar |
| **Sim speed over time** | Yes. A sample every 2 seconds. | `!profile sim` measures for a set time. Nothing is kept. | Yes. Every second. |
| **CPU use** | Yes: process CPU and server simulation CPU | Not stated | In the API only (`CpuPercent`) |
| **Memory use** | Yes | Not stated | Yes, with garbage collection counts |
| **Players, PCU, grids, entities over time** | Yes | No | Yes, plus blocks, factions, characters, floating objects and concealed blocks |
| **Player ping** | Collected as 0: the agent sets ping to 0 for every player | Not stated | Yes, for each player, and the average |
| **History kept** | 30, 45, 60 or 90 days (30 by default): raw samples for about 2 hours, then minutes, then hours | None. Results go to chat. | 1 to 7 days in Quasar (3 by default), with seconds for 6 hours. About 1 hour on a server without Quasar. |
| **Where the frame goes** | Simple mode: Frame, Update, Physics, Scripts, Network and Other | `!profile frames`, for a set time | A tree of about 40 parts, always on, with "Self" time for each part |
| **Time per grid** | Extensive mode: the top 20 grids, every second | `!profile grids` | Yes: in runs, in background totals and in history |
| **Time per block type and block definition** | No | `!profile blocktypes`, `!profile blocks` | Yes |
| **Time per entity type** | Extensive mode: the top 20 | `!profile entities` | Yes |
| **Time per player and per faction** | No | `!profile players`, `!profile factions` | Yes |
| **Time per programmable block (script)** | The agent collects it, but no page shows it | `!profile scripts` | Yes |
| **Session components** | No | `!profile session` | Yes |
| **Game logic and grid callbacks** | No | Not stated | Yes |
| **Mods and plugins** | Plugin statistics: numbers that plugins report about themselves, kept in memory | Not stated | Time by mod and by plugin, with each plugin's own Update, and a page for each mod's parts |
| **Physics clusters** | No | `!profile physics`. It steps clusters one by one while it measures. Its help says this slows down the game thread. | `!monitor physics`. Clusters are timed one at a time through the game's own code, and the time this adds is limited. Only on request. |
| **Network** | The agent collects the top network events, but no page shows them | Not stated | `!monitor net` and a Network page: data sent and received, RPCs, and each player's traffic and queue |
| **Game thread and worker threads** | Not stated | Shows game thread ms/f. Worker time is shown only for `custom`. | Every row is labelled Main, Worker or Mixed. Runs show how long the game thread waited for workers. Thread changes are marked. See [Thread tracking](Thread-tracking). |
| **Filters for a run** | No runs | `--grid`, `--entity`, `--this`, `--player`, `--faction`, `--top`, `--secs`, `--gps`, `--details` | The same, plus `--block` and `--noblocks` |
| **Unknown options** | No options | Options it does not know are ignored | Options it does not know are refused, so a typing mistake cannot measure the wrong thing |
| **Commands for players** | No | No. All commands are for moderators. | `!monitor mine` and `!monitor test`, for the player's own grids only, with limits |
| **Look inside a method** | No | `!profile custom`: measurements that a plugin's author added to their code | `!monitor drill` and the Inside page: any method, with no help from its author |
| **Patches by other plugins** | No | No | Lists which plugins patch which game methods. An audit measures what those patches cost. |
| **Lag alerts** | Yes. Two Discord rules on sim speed (a sharp drop, and a low average over 60 seconds). Browser notifications for sim speed, CPU, memory and unsaved time, off by default. | No | Yes. A lag trip starts a run, keeps the evidence, tells admins in the game, and can post the cause to Discord. See [Lag alerts and incidents](Lag-alerts-and-incidents). |
| **Lag incidents with evidence** | No | No | Yes: start, end, worst frames, slowest calls and the run |
| **Join hitch** (a long frame when a player spawns) | No | No | Recognised, and not counted as lag. See [Join hitch](Join-hitch). |
| **What is normal for this hour** | No | No | Yes: baselines for each hour of the day |
| **Several servers on one page** | Yes: the fleet dashboard, and several servers or cluster nodes on one Analytics chart | No | Yes: the [All servers view](All-servers-view) |
| **Charts** | Yes: the Analytics page, with ranges from 30 seconds to 30 days | No. Results are text in chat. | Yes: Monitor's page and Full charts |
| **API for other plugins** | Yes: analytics series, recent activity, entities and players | Not stated | Yes: `IMonitorApi` for Quasar plugins and `IMonitorServerApi` for server plugins. See [API](API). |
| **Licence** | AGPL | No licence file in the repository | MIT |

## What each one does well

**Quasar built-in**

- It is already there. You install nothing.
- It keeps the longest history: up to 90 days.
- It shows CPU use on its charts.
- Its alerts also cover CPU, memory and unsaved game time.
- Its dashboard covers every server, including servers in a cluster.

**Torch Profiler**

- Its commands are short and easy to remember. Monitor's run commands follow the same pattern.
- It answers in chat, so it needs no web page.
- `!profile custom` shows measurements that a plugin author built into their own plugin.

**Monitor**

- It times grids, block types, blocks, players, factions, scripts, session components, game logic, callbacks, mods, plugins, RPCs and physics clusters, in one run.
- It keeps measuring in the background, so you can look back at what happened while nobody was watching.
- It notices lag by itself, and keeps the evidence as an incident.
- It shows where time went: on the game thread or on worker threads.
- It knows that a long frame after a player spawns is a join hitch, not lag.
- It lets players test their own grids, with limits.
- It can look inside any method, and measure what other plugins' patches cost.

## How to choose

- **You run Magnetar servers under Quasar:** use Quasar's monitoring and Monitor together. Quasar shows CPU and long-term history. Monitor shows what causes lag.
- **You only want to know whether a server is healthy:** Quasar's built-in charts and alerts are enough.
- **You want to know why a server lags:** use Monitor. Start with `!monitor diagnose`.
- **You run Torch servers:** use the Torch Profiler. Monitor's Torch build is not supported.
- **Two alerts for one lag event:** Quasar's sim speed alerts and Monitor's alerts can both post to Discord. Give each its own channel, or turn one of them off.

## Sources

- **Quasar:** the Quasar source code (Quasar.Agent: `GameBridge.cs`, `AgentProfiler.cs`, `AgentProfilerPatches.cs`; Quasar: `Components/Pages/Analytics.razor`, `Services/Analytics/AnalyticsStoreOptions.cs`, `Services/Discord/DiscordOptions.cs`, `InstanceNotificationMonitor.cs`) and Quasar's monitoring documentation, checked on 14 November 2026.
- **Torch Profiler:** https://github.com/TorchAPI/Profiler at commit `f32f02f8` (tag v3.1.0 is the newest tag): `ProfilerCommands.cs`, `RequestParamParser.cs`, `ProfilerConfig.cs`, `Core/ProfilerResult.cs`, `Core.Patches/MyPhysics_StepWorlds.cs`. Plugin page: https://torchapi.com/wiki/index.php/Plugins/Profiler.
- **Monitor:** this wiki and the Monitor source.

Other hubs were checked too. MagnetarHub, PluginHub and QuasarHub have no general server profiler. The only plugin there named a profiler is ModNetworkProfiler on PluginHub. It is a client plugin that counts mod network messages only.
