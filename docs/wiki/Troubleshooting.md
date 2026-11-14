# Troubleshooting

Find the message or the problem you see, then follow the steps.

## Installing

### The Monitor page says "Monitor's server plugin isn't running on this server."

The Quasar part works, but that server does not run the Magnetar plugin.

1. Open that server's plugin list (in Quasar, or with F8 in MagnetarConfig on the server).
2. Turn on **Monitor**, from MagnetarHub or as a dev folder with `Monitor.xml`.
3. Restart the server.
4. If Monitor is already on, look in the server's log for a Monitor build error. Magnetar builds Monitor from source when the server starts, and a build error stops it from loading.

### There is no Monitor page in Quasar

1. Check that the `senx.monitor` folder is directly inside Quasar's `Plugins` folder, and contains `quasar-plugin.json`.
2. Restart Quasar.
3. Open Quasar's UI plugin settings page and look for a load error.
4. Check that your Quasar user has the **CanView** permission.

### The Monitor page is empty for a server

- Quasar collects every 30 seconds. Wait a minute.
- Check that the server is connected in Quasar.
- Check that the Magnetar plugin is installed on that server.
- Monitor starts measuring 30 seconds after the world is ready. A server that is still loading has nothing yet.

### The page stops showing new data for a server after an update

The Magnetar plugin and the Quasar plugin must come from the same version. Update both, then restart Quasar and the server.

## Chat commands

### "Monitor is waiting for the game to be ready." or "Detail starts 30 seconds after the game is ready."

Wait 30 seconds after the world loads, then try again.

### "No background totals yet." or "There's no total to read right now."

Background totals arrive every 30 seconds. Wait, then try again. Check that detail is not off: `!monitor detail grouped`.

### `!monitor mine` says "None of your grids were in the latest total."

Your grids did no work in the last 30 seconds. A grid that does nothing costs nothing.

### `!monitor test` is refused

The reply says why:

| Reply | What to do |
|---|---|
| "You can test again in N s." | Your own cooldown is not over. Wait. |
| "Someone else just ran a test. Try again in N s." | Wait for the time it gives. |
| "A measurement just ran. Try again in N s." | Another run just ended. Wait. |
| "The server is busy, and a test would add to that." | Sim speed is below the lowest sim speed for tests (0.80 by default). Try when the server is calmer. |
| "You can only test your own grids." | The grid belongs to someone else. |
| "You don't own a grid with that name." | Check the name, or sit in the grid and type `!monitor test`. |
| "Sit at the controls of your grid, or name it" | You typed `!monitor test` without a name while not in a seat. |

### "--this needs you to be controlling a grid."

Sit at the controls of the grid (a cockpit or a remote control), then try again.

### "--takeme" says "Leave your seat first."

Stand up from your seat, then try again.

### A run says "A measurement is already running."

Only one run can go at a time on a server. Wait until it ends. `!monitor status` shows how many seconds are left.

### An option is refused

Monitor refuses options it does not know, and options that do not fit the command. The reply says which. See the options table on [Commands](Commands#options).

### Discord replies from a bot show `*` marks

Some bots escape formatting. The reply is still readable.

## Measuring

### Clusters are not timed

The reply says why, for example "Clusters weren't timed, because ...". Usually another plugin changes the same physics code, so Monitor refuses to guess. `!monitor health` shows what was taken over.

### `!monitor health` shows "Taken over" or "Unavailable"

Another plugin rewrote some calls that Monitor times, or a game update changed them. Monitor skips only those parts, and everything else keeps working. Runs say when some numbers read low because of this. The **Patches** tab lists them under "Calls Monitor can't time".

### A run says "Only N% of measured time fitted in the tables"

The server had more objects than Monitor's tables hold in one run. Totals stay right, but some rows read low. Use a filter, for example `--grid=ID` or `--player=NAME`, to measure a smaller part.

### The `On` column shows `-`

No time was recorded for that row in that measurement.

### A thread change shows `#` and a number

The name is not known yet. It appears when that object shows up in a normal list. See [Thread tracking](Thread-tracking#thread-changes).

## Alerts

### A spike right after a player joins is not an alert

That is a join hitch. It is expected. See [Join hitch](Join-hitch).

### No alert although the server lagged

Check, in this order:

1. **Watch for lag** is ticked on the Incidents tab, and `!monitor trigger off` was not used since the last restart.
2. The lag lasted long enough. By default sim speed must stay below 0.90 for 5 seconds.
3. The cooldown was over. By default it is 15 minutes after the last trip.
4. A run was not going at the time.
5. It was not a spike within 2 seconds of a save, or within the grace window after a spawn.

### Too many alerts

Lower **Sim speed under**, raise **For**, or raise **At most every**. See [Settings](Settings#lag-settings).

### Discord gets no posts

1. Click **Send a test** on the Incidents tab.
2. Check that the URL is a Discord webhook address.
3. Check Quasar's log for a webhook error.

### Two Discord messages for one lag event

Quasar's own sim speed alerts and Monitor post to the same channel. Give Monitor its own channel.

## Still stuck

Open an issue at https://github.com/buttheadbob/Monitor/issues. Include:

- the Monitor version and the Quasar version;
- Legacy or Interim, and Windows or Linux;
- the exact message, and the lines from the server log that start with "Monitor".

Remove player names and server addresses before you post.
