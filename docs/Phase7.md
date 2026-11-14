# Phase 7: Network and concealment

The three things left over from earlier phases: network usage per player, per-RPC timing, and Blocks vs Conceal.

**Tested on a test server.** Its tests pass, and it has run on a test server with no players. It hasn't run on a real server yet.

## Network usage per player

Monitor timed network code but had no bytes, so "sim 1.00 but rubberbanding" couldn't be explained.

Every packet goes through two calls, `IMyPeer2Peer.SendPacket` in `MyNetworkWriter.SendAll` and `ReadPacket` in `MyReceiveQueue.ReceiveOne`, one recipient at a time. Both are in `Sandbox.Game.dll`, and Magnetar doesn't touch this layer, so it's the same code on Legacy and Interim. Monitor swaps those two call sites for hooks that count.

- **It's always on.** It costs a table lookup per packet on the network thread and nothing on the game thread, so it has no gate. Sim speed can read 1.00 during the very thing this is for, so it can't wait for someone to ask.
- **Each second keeps** KB sent and received, what was sent split by kind, and RPCs from clients. The kinds are state sync, events, replication and other.
- **Each census pass keeps, per player,** KB/s down and up, RPCs/s, what's queued for them and packets refused. These are averages since the last pass.
- **The queue is the key figure.** Sent means handed to Steam, which may still be holding it. `BytesQueuedForSend` is what's waiting. A queue that keeps growing means that player can't take what the server's sending, which is what rubberbanding at sim 1.00 usually is.
- **The Overview tab shows what's being sent,** and the new Network tab has the charts and tables.

Figures are counted, not estimated. The game keeps its own totals, but only across all players, and those were left alone, because reading them would clear them.

The table holds 2048 peers, about 150 KB. That's different peers since a restart, not players online. Past that, a peer loses its own row and the totals stay right. Slots are never removed, so nothing can be emptied under a reader.

## Per-RPC timing

`MyReplicationServer.OnEvent` calls `MyReplicationLayer.Invoke` once for every RPC from a client, on the game thread. That call is swapped for a hook, and the RPC's `CallSite` says which method it runs, so one row covers that RPC on every grid and block.

It's an ordinary keyed site in the Session group, so it works like the other lists: every call in a run, background numbers in a sweep, `!monitor rpcs`, `!monitor top rpcs`, the Costs tab, what's new or has jumped, the Runs tab and both APIs. A row's time covers checking the RPC and running it.

- **Whoever sent it counts too.** Each player's RPCs/s comes from the same hook, so a client flooding the server shows up by name.
- **An RPC can be looked inside.** Any RPC that's been timed can be found by the drill-down, and the tables have a button for it.

### What each RPC sends

Kinds say how much of what's being sent is events. This says which ones, which is what to look at when events are most of it.

Every event goes out through `MyReplicationServer.DispatchEvent`, and two calls inside it are the only place that knows both which RPC it is and who it's for. Monitor adds a tally just before each one.

- **The game's call stays exactly as it was.** Its overloads are private and one takes an internal type, so swapping them would have meant publicizing `VRage` and calling them from a hook. Instead a few instructions run first, load the data, the RPC and the recipients, and hand them to a hook. They take off the stack what they put on, so the call's own arguments are never touched.
- **It checks before it loads.** Those arguments go in by position, and the wrong type wouldn't fail safely, it would crash the server. So a tally only goes in while the method still takes the data and then the RPC. If a game update changes that, the site reads unavailable and everything else carries on.
- **Bytes are what the event costs in total,** its size and header times everyone who gets it, so one small event sent to 40 players reads as 40. That's what makes a broadcast stand out.
- **It's always on,** like the rest of the byte counting. It's one lookup and two interlocked adds per event, on whichever thread raised it. The game can raise events from any thread, so this table is safe with many writers, where the packet counters don't need to be.
- **Each sweep keeps the 20 biggest,** ranked by bytes, and a run keeps what was sent while it ran. `!monitor rpcs` lists received and sent, and the Network tab charts the biggest 6.

The table holds 2048 RPCs, about 80 KB. The game has more than that, and uses far fewer. Past that, a new RPC isn't counted and nothing breaks.

## Blocks vs Conceal

This was waiting on Concealment publishing a count, because guessing from entity flags would put a wrong number on a chart. It turns out no guess is needed. Concealment marks everything it stops with its own flag, `(EntityFlags)4`, in both its Torch and Magnetar builds. The game doesn't use that bit, so a census pass only has to read it.

- **Grids and blocks concealed** are kept with each pass. They're counted in the totals as well, because concealed grids are still in the world.
- **Blocks vs Conceal** is on the World tab, with active and concealed lines.
- **Nothing was asked of Concealment.** It needs no change, and Monitor doesn't reference it. With no Concealment, the concealed line reads zero.

## Replies for the game and for Discord

Most admins run commands through a Discord bot, so every command lays out its reply for whoever asked. A report holds what the answer is, and a renderer decides how it looks. The test is whether a real player online asked. Bots run commands as the console, so anyone else gets the Discord layout.

For Discord, before Discord draws it:

```text
### Grids
`Measured   ` **10.0 s**
`Sim speed  ` **0.97**
`Frame avg  ` **14.20 ms**
*Every call was timed. Main and Work are ms/f, Peak is ms.*
**Top grids**
> `Main  Frame  Work  Peak` Grid
> `3.20  22.5%  1.10  9.04` LCY.Rob Base 1
> `1.75  12.3%  0.40  4.80` LCY.Rob's Mega Miner
```

In game, where the font can't line anything up, a row is its name with the figures under it:

```text
Grids
Measured 10.0 s, Sim speed 0.97
Frame avg 14.20 ms
Every call was timed. Main and Work are ms/f, Peak is ms.
-- Top grids --
LCY.Rob Base 1
   Main 3.20  Frame 22.5%  Work 1.10  Peak 9.04
```

Bots differ, and other apps may split or show a reply differently, so this assumes as little as it can. It's built on what's known from the two read so far, Orion and SEDiscordBridge:

- **A reply usually lands in an embed's description,** as text, so bold, quote bars and inline code work. A bot often adds its own title, so a report's title is only a small heading.
- **No code blocks.** Both bots join replies that arrive close together and split them on newlines at about 2000 characters. A table in a code block could be cut in half, and the rest of the embed would be wrong. So no line depends on another, and a split on any newline is safe.
- **Figures go in a padded inline code cell ahead of the name.** Inline code is monospaced, so the columns line up even though the rest of Discord's font isn't, and a long name can't push them out of line.
- **Facts stack one under another,** so no line carries too much.
- **`status` shows the last hour as bars,** the same idea as Orion's status card. These are left out in game, because the game's chat font has no bar characters.

**A chat command gets one finished report.** From the game or Discord, a command that takes time says it's started, holds on to whoever asked, collects everything, then builds one clean report and sends that. API calls get no "started" line, only the finished result.

**Long replies come in pages,** of at most 1000 characters in game and 1800 for Discord, breaking only between lines, with a heading kept with what follows it. This also settles the worry about `diagnose` in game: it used to go out as one message, and Torch rejects chat over 2048 characters.

**Names can't do harm.** Grid, player and script names are typed by players. An embed can't ping, but a bot might post a message instead, so in the Discord layout, and in webhook alerts, a name of `@everyone` is made harmless with a zero-width space. A name can't pose as a link, a backtick can't break the figures, and no name can start a line of its own. In game, names are left as typed.

The report itself lives in `SenX.Monitor.Contracts`, so the Torch build gets all of this as it is. Its only difference is the test for who's asking, which there is `Context.Player != null`.

Limits:

- **A bot running a command as a player who's online gets the game layout.** Nothing tells that apart from the player typing it.
- **The console and Quasar's chat page get the Discord layout too.** The marks show, but they stay readable.
- **A very narrow screen can wrap a row,** but the figures stay together and the name just moves to the next line.
- **Bots that haven't been read may behave differently.** One that cuts in the middle of a line could split a row, and one that drops text past a limit could lose the end of a long reply. A page fits in a message, so a bot that posts each reply as it comes is safe.
- **A bot that escapes markdown shows the marks.** SEDiscordBridge escapes `*`, so its readers see asterisks around values and notes. It stays readable, and Orion's readers don't see them.

## Commands

| Command | Shows |
|---|---|
| `!monitor net` | Totals and kinds over `--secs=N`, the top 10 players by what's sent to them, and the 5 biggest events. Instant, with no run. Admin |
| `!monitor rpcs` | Times everything, then lists the costliest RPCs received and the biggest sent. Admin |
| `!monitor top rpcs` | The latest background total of RPCs received. Admin |
| `!monitor top rpcssent` | The latest background total of RPCs sent. Admin |

`diagnose` now includes the top RPCs, both ways.

## Also fixed

- **Sampled session components, scripts and plugin Updates read too high.** `SessionHooks`, `ScriptHook` and `PluginUpdateShims` timed every call but still scaled by the group guard's shift, so if the guard ever sampled their group, they read 2^k too high. Other hooks already chose one call in 2^k first, and these do now as well. This only ever affected background numbers on a group the guard had tripped on. Runs never sample.
- **Calls below one a frame read as 0.** Background totals kept calls per frame as a whole number, so an RPC at 30 a second, or a rare callback, showed 0. Rows now keep hundredths, on every list, and still read as calls per frame.
- **`diagnose` was one very long chat message.** It now comes in pages, as above.

## The format changed

History batches went from `MNH2` to `MNH3` here, because seconds and players gained columns, and they've since moved on to `MNH4`. The worker and the Quasar plugin ship together, and nothing has been collected yet, so nothing needs migrating.

## Limits

- **Sent isn't the same as delivered.** It's what the server handed to Steam or EOS, without their own overhead.
- **RPCs sent cover events only.** State sync and replication aren't RPCs, so a server sending mostly state sync won't show it here. The kinds say which it is.
- **RPCs sent are counted as they're raised,** before they reach the network, so they can differ a little from the events kind. An event that's refused later still counts here.
- **Only RPCs from clients are timed.** Events the server sends don't run on the server, so they have no time to measure, and they're only seen as bytes.
- **RPCs sent have no per-player split.** A row is the total across everyone who got it.
- **Mod messages are a few rows.** Every mod's network messages go through the game's `ModMessage...` RPCs, so they show as those rows and can't be told apart by mod.
- **EOS has no queue figure,** so EOS players always read 0 there. Its `GetSessionState` doesn't fill it in.
- **Players are averages over about 30 seconds,** and kept once a minute, so a short burst shows smaller than it was. Totals are per second.
- **A player who joined part way through a pass** reads low for that pass.
- **RPC time isn't kept per player,** only the count.
- **Concealment in cluster mode** doesn't set its flag, so concealed reads zero there. A different plugin that hides grids its own way isn't seen either.

## Tests to run

Written and compiling, but not run:

- `tests/SenX.Monitor.Core.Tests`: `NetTrafficTests`, including real threads on the shared table, `ChatReportTests`, which replay a bot joining replies and splitting them on newlines, plus additions to `SweepTests`, `CaptureTests`, `HistoryTests` and `GroupRotationTests`.
- `tests/SenX.Monitor.Magnetar.Tests`: `NetHooksTests`, `ChatReplyTests`, plus tallies through real Harmony in `RewriterTests`.
- `tests/SenX.Monitor.Ui.Tests`: additions to `ChartBuilderTests`, `HistoryStoreTests`, `PublicApiTests` and `ReportTextTests`, with `PublicApi.approved.txt` regenerated. That file only gained lines.

`QueuedFor` and the concealed flag on a real grid have no tests, because they need the game.

These need a real server:

- `!monitor net` with players on, and each player's figures against what Steam reports.
- A player's queue growing when their connection is throttled, which is the claim this all rests on.
- The kinds on a real server, because a large grid streaming in should move replication and not state sync.
- `!monitor rpcs` and the Network tab with players doing things.
- RPCs sent against the events kind, which should come out close.
- A few commands through a real bot, including a long `diagnose` that splits into parts, a grid named `@everyone`, and `status` on a phone.
- A long `diagnose` in game, to see how the pages read.
- Blocks vs Conceal after `!conceal run`, and after a reveal.
- All of it on Interim as well as Legacy.
- The time `GetSessionState` takes with a full server, since it's a native call per player per pass.
