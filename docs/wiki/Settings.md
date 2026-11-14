# Settings

Most settings are on the **Incidents** tab of the Monitor page in Quasar. They apply to every server. Quasar sends them to each server when the server starts and when you change them.

A Magnetar server without Quasar uses the defaults, and saves nothing.

## Lag settings

Open the **Incidents** tab, then **What counts as lag**.

| Setting on the page | Meaning | Default | Allowed |
|---|---|---|---|
| Watch for lag | Monitor reacts to lag by itself. | On | On or off |
| Sim speed under | A second below this counts as slow. | 0.90 | 0.1 to 1 |
| Or frame p95 over | A second with frame p95 above this counts as slow. | 0 (off) | 0 or more ms |
| For | How many slow seconds in a row start a trip. | 5 | 1 to 600 |
| Or one frame over | A spike: one frame above this starts a trip. | 1000 ms | 0 (off) or more |
| Not within | The grace window after a player spawns. See [Join hitch](Join-hitch). | 15 s | 0 (off) to 120 |
| Run for | How long a triggered run lasts. 0 keeps the evidence without a run. | 5 s | 0 to 60 |
| At most every | The cooldown between trips. | 900 s | 60 to 86400 |
| Time physics clusters | Triggered runs also time physics clusters. This changes physics scheduling when nobody watches, so it is off. | Off | On or off |
| Tell admins in the game | Send a chat message to online admins on a trip. | On | On or off |

### Examples

| You want | Set |
|---|---|
| Fewer alerts on a busy server that often runs at 0.85 | Sim speed under 0.80 |
| Alerts only for long lag | For 30 |
| No alerts for short freezes | Or one frame over 0 |
| A longer run as evidence | Run for 15 |
| Alerts at most once an hour | At most every 3600 |
| Silence in the game, but still incidents and Discord posts | Untick Tell admins in the game |

`!monitor trigger off` turns off **Watch for lag** on one server until that server restarts.

## Player settings

Open the **Incidents** tab, then **What players can run**.

| Setting on the page | Meaning | Default | Allowed |
|---|---|---|---|
| !monitor mine | Players can use `mine`. | On | On or off |
| !monitor test | Players can use `test`. | On | On or off |
| For | How long a test runs. | 5 s | 1 to 60 |
| At most every | Time between two tests by the same player. | 600 s | 60 to 86400 |
| Not under sim speed | Tests are refused below this sim speed. | 0.80 | 0 to 1 |

When a command is off, players who use it get a reply that says so.

## Discord settings

Open the **Incidents** tab, then **Discord alerts**.

- Enter the webhook URL for this server.
- Tick **For all servers** to use it for every server that has no webhook of its own.
- Click **Send a test** to check it.

See [Lag alerts and incidents](Lag-alerts-and-incidents#discord-alerts).

## History

At the top of the Monitor page, **Keep history for** sets how many days of history Quasar keeps.

| Setting | Meaning | Default | Allowed |
|---|---|---|---|
| Keep history for | Older history is deleted. | 3 days | 1 to 7 days |

Quasar keeps second-by-second data for 6 hours. It keeps minutes, background totals, events, incidents and runs for the number of days you choose. That is about 30 MB per server with the default.

## Background detail

Background detail is set with a chat command on each server. See [Commands](Commands#admin-commands-settings).

| Mode | Meaning |
|---|---|
| grouped | One group of related work is measured at a time. This is the default. It costs very little. |
| everything | Every call is timed, all the time. Exact, but it costs the server more. |
| off | No background detail. The frame tree and the runs still work. |

`everything` is never kept after a restart.

## Who can change what

| Quasar permission | Allows |
|---|---|
| CanView | Open the Monitor page and read everything. |
| CanControlServers | Start and delete runs, profile a grid, start a patch audit, look inside a method. |
| CanEditConfigs | Change the lag settings, the player settings and the history length. |
| CanManageDiscord | Set the Discord webhook. |

In the game, `howto`, `mine` and `test` are open to every player. All other commands need admin rights.

## Where settings are saved

- **Quasar:** `settings.json` in the `monitor` folder inside Quasar's install folder.
- **Magnetar without Quasar:** nothing is saved. The defaults are used.

Webhook addresses are kept in the same file. Treat the file as a secret.
