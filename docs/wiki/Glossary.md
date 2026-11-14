# Glossary

| Word | Meaning |
|---|---|
| Alone | (`physics` only) How long one physics cluster takes when it steps alone, with all physics threads for itself. Use it to rank clusters. |
| Audit | A measurement on the Patches tab that splits a patched method's time into plugin prefixes, the original method and plugin postfixes. |
| Background total | Every 30 seconds, Monitor adds up what it measured in the background. These numbers cost almost nothing. They are good for trends. |
| Baseline | What is normal for a server at each hour of the day. Quasar learns it from history. |
| Calls/f | Calls per frame. |
| Census | Monitor's count of players, factions, grids, blocks, PCU and more. One pass takes about 30 seconds. |
| Cluster | A group of physics objects close together. The physics engine steps each cluster as one unit. |
| Concealed grid | A grid that a concealment plugin has stopped. It costs almost nothing. |
| Cooldown | The time after a lag trip during which no new trip can start. 15 minutes by default. |
| Detail | How much Monitor measures in the background: `grouped`, `everything` or `off`. |
| Drill | Looking inside one method by timing everything it calls. `!monitor drill`, or the Inside tab. |
| Evidence | What Monitor keeps on a lag trip: the slowest frames just before, the slowest single calls, and a run. |
| Frame | One step of the game. The server tries to run 60 frames each second. |
| Frame budget | The time one frame may take: 16.67 ms. If frames take longer, the game slows down. |
| Frame (column) | Main time as a share of the average frame. |
| Game thread | The one thread that runs each frame. Also called the main thread. If it is slow, the frame is slow. |
| GC | Garbage collection: the .NET runtime freeing unused memory. A gen 2 collection can pause the server. |
| Grace window | The seconds after a player spawns during which a spike is a join hitch, not lag. 15 seconds by default. |
| Incident | A record of one lag event in Quasar, from its start to its end, with the evidence. |
| Join hitch | One long frame just after a player's character spawns, while the game loads asteroid voxel files. Not counted as lag. |
| Lag | The game runs slower than normal. Sim speed falls below 1.00. |
| Lag trip | Monitor decides that the server is lagging. A trip can start a run and send alerts. |
| Main | Time on the game thread, in ms per frame. The time that can slow a frame. |
| Main waiting | In a run: how long the game thread waited for worker threads, per frame. |
| Main, Worker, Mixed | Where the time of something went: on the game thread, on worker threads, or on both. |
| Marker | A dashed line on a chart that marks an event, such as a save, a restart or a lag trip. |
| ms | Millisecond: one thousandth of a second. |
| ms/f | Milliseconds per frame. The average time something takes in each frame. |
| On | The chat column for the thread label: `M`, `W`, `M+W` or `-`. |
| p50, p95, p99 | Frame time percentiles. p95 is the time that 95% of frames stay under. |
| Parallel work | In a run: time on worker threads in the game's parallel updates, added up over all workers, per frame. Not frame time. |
| PCU | Performance cost units: the game's own measure of how heavy blocks are. |
| Peak | The slowest single call in a row, in ms. |
| Queue | (`net` only) Data waiting to be sent to a player. If it keeps growing, that player cannot receive data fast enough. |
| Rank | (`mine` only) Where a grid stands among all grids, for example 12 of 340. |
| RPC | A network message that asks the server (or a client) to run a method. |
| Run | Monitor times every call on the server for a few seconds. The most exact answer. |
| Sample | A background total that Monitor keeps for the API: every 2 minutes, or every minute during low sim speed. |
| SeenShare | (API) The share of background totals a row appeared in. |
| Self | (Tree tab) Time in a part that the parts inside it do not explain. |
| Session component | Code that runs once per frame for the whole world. Many mods use these. |
| Sim speed | How fast the game runs, compared with normal. 1.00 is full speed. 0.50 is half speed. |
| Spike | One frame that takes a very long time, for example one second. |
| Thread change | Something's time moved between the game thread and worker threads and stayed there for two background totals. |
| Timing cost | In a run: how much Monitor's timing added to each frame. |
| Webhook | An address that lets Monitor post messages to a Discord channel without a bot. |
| Work | Time on worker threads, in ms per frame, added up over all workers. It can be more than a whole frame. |
| Worker thread | A helper thread. The game gives some work to worker threads so that it runs at the same time on other CPU cores. |
