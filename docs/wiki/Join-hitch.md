# Join hitch

A **join hitch** is one long frame just after a player's character spawns. Monitor does not count it as lag.

## Why it happens

When a player's character spawns, the game loads the voxel files for the asteroids near it. The first time after a server start, this loading runs on the game thread. One frame can then take several seconds, and everyone on the server feels a short freeze.

This is normal game behaviour. It is not lag that an admin can fix. An alert for it would only teach admins to ignore alerts.

## What Monitor does

- A spike in the 15 seconds after a player's character spawns is a join hitch.
- A join hitch gives no alert, no run and no incident. It does not start the cooldown.
- The server log gets one line, for example:

  ```text
  Monitor noticed a join hitch: one frame took 6786 ms just after a character spawned. That's the game loading voxel files, so there's no alert or run.
  ```

- The charts show a **Join hitch** marker in its own colour.
- The APIs report a `JoinHitch` event. See [API](API).

## What still counts as lag

Only single spikes are excused. If sim speed stays low, or frames stay slow, during the 15 seconds after a spawn, that is still lag, and Monitor trips as usual.

## Players only

Only real players open the grace window. NPCs and bots, for example from Modular Encounters Systems (MES), do not. A server with many NPC spawns would otherwise excuse spikes almost all the time.

## Change the grace window

In Quasar, open the Monitor page, the **Incidents** tab, then **What counts as lag**. The setting is **Not within N s of a spawn**.

| Value | Effect |
|---|---|
| 15 | The default |
| 1 to 120 | A shorter or longer window |
| 0 | Off: every spike after a spawn is lag |

## How to tell a join hitch from real lag

| Join hitch | Real lag |
|---|---|
| One long frame | Many slow frames, or low sim speed for seconds |
| Right after a player spawns | At any time |
| Mostly after a server start, when asteroids are first loaded | Again and again |
| Join hitch marker on the chart | Lag noticed marker and an incident |

If players complain about freezes when someone joins long after a restart, look at the Join hitch markers on the Overview chart. Many markers mean many first-time asteroid loads, for example from players who spawn in new places.
