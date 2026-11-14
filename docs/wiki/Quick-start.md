# Quick start

Monitor starts measuring 30 seconds after the world is ready. You do not need to set anything up.

## For admins: find what is slow

### 1. Look at the server now

Type:

```text
!monitor status
```

You see sim speed, frame times, where frame time goes, and world counts such as grids and PCU. If sim speed is 1.00 and frame p99 is under 16.67 ms, the server is fine right now.

### 2. If the server lags, find the cause

Type:

```text
!monitor diagnose
```

Monitor times every call on the server for 10 seconds. Then it lists the top of every list: grids, block types, blocks, players, factions, scripts, session components, game logic, callbacks, mods, RPCs and slow calls.

Look at the **Main** column first. It is time on the game thread, and only that time slows a frame. See [Thread tracking](Thread-tracking).

### 3. Look closer

Use the list that pointed at the cause:

| If diagnose shows | Type | To see |
|---|---|---|
| One grid at the top | `!monitor grids --details` | Its owner and faction |
| One block type at the top | `!monitor blocks` | Which block definitions |
| A player at the top | `!monitor grids --player=NAME` | That player's grids |
| A mod at the top | `!monitor mods` | Each mod's parts |
| A script at the top | `!monitor scripts` | Which programmable blocks |
| Physics at the top | `!monitor physics` | The costliest physics clusters |

All commands are on the [Commands](Commands) page.

### 4. Look back in time

The Monitor page in Quasar shows what happened while you were away:

- **Overview** shows charts for the time span you pick.
- **Incidents** shows every lag event, with its evidence.
- **Runs** shows every run, also the ones from chat commands.

See [Reading the pages](Reading-the-pages).

## For players: test your own grids

### See what your grids cost

Type:

```text
!monitor mine
```

You see each of your grids, what it costs, and its rank among all grids, for example "12 of 340". This costs the server nothing. It reads the latest background total.

### Test one grid

Sit at the controls of your grid, then type:

```text
!monitor test
```

Or name the grid:

```text
!monitor test My Miner
```

Monitor times your grid for 5 seconds. Then it lists its block types, its costliest blocks and its scripts.

A test has limits, because it costs the whole server. You can test once every 10 minutes, only your own grids, and only while sim speed is 0.80 or higher. See [Commands](Commands#player-commands).

## Help in the game

Type `!monitor howto` at any time for a short help text.
