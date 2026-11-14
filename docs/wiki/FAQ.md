# FAQ

### Does Monitor slow down my server?

Very little. The always-on frame tree costs a few microseconds per frame. Background detail costs at most about 2% of a frame while a group is measured, and less on most servers. `!monitor status` shows Monitor's own cost under "Own cost".

Runs and `everything` time every call, so they cost more while they last. Every run reply says how much timing added.

### Does Monitor cause garbage collection pauses?

No. The measuring code keeps its data in native memory and creates no garbage while it runs.

### Does Monitor write to the server's disk?

The Magnetar plugin writes nothing to disk. Quasar writes history to its own `monitor` folder.

### Do I need Quasar?

No. Without Quasar, every chat command works and the server keeps about one hour of history in memory. Quasar adds charts, history for up to 7 days, incidents, Discord alerts and the All servers view.

### Does it work on Linux?

Yes, on Magnetar Interim servers. It also works on Windows, with Legacy and Interim.

### Does it work with Torch?

A Torch build exists as source code, but it is not supported and gets no new features. Quasar and Magnetar are the supported platforms.

### Which number should I look at first?

**Main**. It is time on the game thread, and only that time slows a frame. Then look at **Frame**, its share of the frame. See [Thread tracking](Thread-tracking).

### Why does a grid show more Work than a whole frame?

Work is added up over all worker threads, which run at the same time. Eight workers each busy for 3 ms make 24 ms of Work in one frame.

### What is the difference between background numbers and a run?

Background numbers measure one group of related work at a time, for almost no cost. They are good for trends. A run times every call for a few seconds. It is exact, and it is the way to find what causes lag.

### Why do the lists in `diagnose` add up to more than the frame?

Some lists overlap. A script runs inside its block's update, so its time is in "Top scripts" and in its grid. Players and factions are another way to group the same grids. Compare rows inside one list, not across lists.

### Why was a long freeze not reported?

It was probably a join hitch, a spike right after a world save, or it came during the cooldown after an earlier trip. See [Troubleshooting](Troubleshooting#no-alert-although-the-server-lagged).

### Can players see other players' grids?

No. `!monitor mine` and `!monitor test` show only the player's own grids. A rank is a number such as "12 of 340", with no names.

### Can players lag the server with `!monitor test`?

Tests are limited: 5 seconds each, once every 10 minutes per player, a 1-minute gap between any two runs on the server, and no tests below sim speed 0.80. Admins can change these limits or turn the command off.

### Does Monitor collect IP addresses?

No.

### Is the Discord webhook address safe?

Monitor never shows it on a page and never writes it to a log. It is stored in Quasar's `monitor/settings.json`, so protect that file.

### Can I use Monitor's data in my own plugin?

Yes. See [API](API).

### Where do I report a bug?

At https://github.com/buttheadbob/Monitor/issues. See [Troubleshooting](Troubleshooting#still-stuck) for what to include.
