# API

Other plugins can read Monitor's data and ask for runs. This page is for plugin developers.

## The two ways in

| API | For | Reaches | Get it from |
|---|---|---|---|
| `IMonitorApi` | Quasar plugins | Every server that Quasar has data for, connected or not | Quasar's services (dependency injection) |
| `IMonitorServerApi` | Server plugins on Magnetar | Only the server it runs on | `MonitorServerApi.Current` |

Both lead to the same tools for one server, `IMonitorTools`. A Quasar plugin gets them with `For(server)`. `IMonitorServerApi` is those tools for its own server.

How far back the data goes:

| | Quasar plugin | Magnetar server plugin |
|---|---|---|
| History | Up to the history setting: 3 days by default, 7 at most | About one hour, which is what the server holds in memory |
| Saved runs | All of them | The latest one |
| Incidents | Yes | No, because Quasar keeps those |

Everything is in the `SenX.Monitor.Api` project. It targets netstandard2.0, so Legacy servers can load it. Times are UTC, and values are in milliseconds.

## Versions

| Version | `IMonitorApi` | `IMonitorServerApi` | Adds |
|---|---|---|---|
| 1 | 1 | 1 | The first API |
| 2 | 2 | 1 | `ChartImageAsync`, and `LostSites` on runs |
| 3 | 3 | 2 | `MonitorEventKind.JoinHitch` |
| 4 | 4 | 3 | `MonitorThread`, `MonitorThreads.Of`, `Thread` on rows, `MonitorEventKind.ThreadJump`, `MainWaitMs` and `ParallelWorkerMs` on runs |

- Check `Version` before you use anything newer than version 1. Calling something the installed Monitor does not have throws `MissingMethodException`.
- Nothing is ever removed or changed. A breaking change would be a new interface beside the old one.
- The assembly version stays 1.0.0.0, so a plugin built against a newer API still loads with an older Monitor.
- A test pins every public member, in `tests/SenX.Monitor.Ui.Tests/PublicApi.approved.txt`.
- New enum values can appear. Treat an event kind you do not know as information.

## Reference the project

Monitor is open source. Link to its project, and do not ship the DLL:

```xml
<ProjectReference Include="..\Monitor\src\SenX.Monitor.Api\SenX.Monitor.Api.csproj" Private="false" ExcludeAssets="runtime" />
```

Monitor brings the only copy. Quasar loads every plugin into one context and finds assemblies by name in every enabled plugin's folder. A copy in your plugin's folder could load instead of Monitor's, and an old copy could break Monitor.

For a server plugin, put Monitor's id `f58cf508-46b1-4964-9836-fd42429adbe0` in your manifest's `DependencyIds`, so Monitor is turned on with your plugin.

**Not solved yet:** a hub builds a plugin from its own repository, where `..\Monitor` does not exist, so a plugin published through a hub cannot compile against this project yet. This works today for a plugin you build yourself.

## Monitor might not be there

Monitor can be missing or turned off. Check first, by name only, and keep all code that names Monitor's types in methods or classes that run only after the check. The examples below show how.

## Rules

- **Permissions are your job.** The API cannot see who asks. On Quasar, check `CanView` before you show anything, and `CanControlServers` before a run, looking inside a method, or an audit. On a server, your command's permission level does this.
- **Say who asked.** Runs, looking inside and audits need `RequestedBy`. It goes in the log and on the saved run.
- **Never wait on a task on the game thread.** Some answers need the game thread, so `.Result` there freezes the server. Use `await`.
- **Events arrive off the game thread.** Do not touch the game in a handler. Keep handlers short.
- **A "no" is a reply, not an exception.** A refused request comes back with `State = Refused`, a `Message`, and `RetryAfter` when it is known. An exception means you cancelled.
- **Runs are limited.** Plugins get one run a minute on a server, counted from the end of the last run of any kind. The longest run is 600 seconds. Cancelling only stops your wait: the run finishes and is saved.

## IMonitorApi (Quasar plugins)

| Member | Gives |
|---|---|
| `int Version` | The API version. 4 at the moment. |
| `IReadOnlyList<MonitorServerInfo> Servers()` | Every server with data: `Id`, `Name`, `Cluster`, `Connected` and `LastSeen`. A server's `Id` stays the same when a cluster node is replaced. |
| `IMonitorTools For(string server)` | That server's tools. Everything that reads history works for a server that is not connected. |
| `MonitorLatest? Latest(string server)` | The same as the tool of that name. |
| `IReadOnlyList<MonitorSavedRun> SavedRuns(string server)` | Finished runs, newest first. |
| `void KeepFresh(string server)` | One-second collection for a few seconds. Call it again to keep going. |
| `Task<MonitorReply> RequestAsync(MonitorRequest, CancellationToken)` | One request for lists of costs. See [One request, one reply](#one-request-one-reply). |
| `event Action<MonitorRunFinished> RunFinished` | A run finished, on any server. |
| `event Action<MonitorEvent> EventRecorded` | An event was recorded, on any server. |
| `event Action<MonitorIncident> IncidentChanged` | Lag began or ended, on any server. |

None of these reach a game server. Only a run, looking inside, an audit and the patch list do.

## IMonitorServerApi (server plugins)

`MonitorServerApi.Current` gives it. It is null until the game is ready, and after Monitor shuts itself down, so read it each time and do not keep it. It has `Version` (3 at the moment) and all the tools below, for its own server only. `Server` on it is empty.

## The tools (IMonitorTools)

### Live

These do not wait, so you can call them as often as you like, on any thread.

| Member | Gives |
|---|---|
| `string Server` | Which server these tools are for. |
| `MonitorLatest? Latest()` | The latest second: `Frame` (sim speed, average and worst frame, free time), `FrameP99`, `PartsMs` (what the frame went on), `Network` and `World`. Null before the first data. |
| `MonitorStatus Status()` | `Connected`, `Started`, `LastData`, `RunGoing`, `LagSince` and `HistoryReach`. |
| `Task<IReadOnlyList<MonitorPlayerNow>> PlayersAsync(ct)` | Who is online, with ping and traffic, from the last census. |
| `Task<IReadOnlyList<MonitorFactionNow>> FactionsAsync(ct)` | Each faction's size, from the last census. |
| `Task<IReadOnlyList<MonitorRow>> TopAsync(list, count, ct)` | The costliest rows of one list, from the latest background total. |

`World` comes from the census, which takes about 30 seconds to come round. On Quasar, frame figures can be 30 seconds old too, unless you call `KeepFresh`.

### History

These read what has already been collected. They cost the game server nothing.

| Member | Gives |
|---|---|
| `SamplesAsync(from, to, lists, top, ct)` | Background totals across a range, oldest first, each with the server's figures from that moment. |
| `HistoryAsync(from, to, ct)` | Timings, world figures and events across a range, without lists. |
| `CostsAsync(from, to, lists, ct)` | What cost time across a range, as one total. The same as a `History` request. |
| `CostHistoryAsync(list, id, from, to, ct)` | One row of a list over time, by its id. |
| `CostHistoryAsync(list, name, from, to, ct)` | The same by name, for rows without an id. Two grids with one name both come back. |
| `EventsAsync(from, to, ct)` | Saves, restarts, changes, lag, runs, join hitches and thread changes across a range. |
| `CompareAsync(before, after, lists, ct)` | Two ranges side by side, for example before and after a change. |
| `ChartImageAsync(chart, from, to, width, height, ct)` | One chart as PNG bytes (version 2 and up). Null on a Magnetar server plugin, and when there is nothing to draw. Sizes from 320 x 200 to 1920 x 1080. |

Samples: every 30 seconds Monitor adds up the background measurements, and it keeps one as a sample every 2 minutes, or every minute while sim speed is below 0.7 and for 5 minutes after. A run leaves a gap, and a server with detail off keeps none.

### Runs

| Member | Gives |
|---|---|
| `MeasureAsync(MonitorRunOptions, ct)` | Measures now, with every call timed, and answers when done. This costs the server while it runs. |
| `CurrentRun()` | The run going now, who asked, and how long is left. |
| `SavedRunsAsync(ct)` | Finished runs, newest first, including lag trip runs. |
| `SavedRunAsync(id, lists, ct)` | A finished run by id, or the newest when `id` is null. |

`MonitorRunOptions` has `Seconds`, `Lists`, `RequestedBy`, and the filters `GridId`, `Player`, `Faction` and `BlockType`, which work as in chat.

### Going deeper

These rebuild methods on the running server. One goes at a time, each comes out by itself, each runs for at most 120 seconds, and plugins get one a minute.

| Member | Gives |
|---|---|
| `IncidentsAsync(ct)` | Lag noticed in the history kept, newest first. Empty on a server plugin. |
| `FindMethodsAsync(text, ct)` | Methods that can be looked inside, by part of their name. Searches are 10 seconds apart. |
| `LookInsideAsync(methodId, seconds, requestedBy, ct)` | Times what a method calls, like `!monitor drill`. |
| `PatchesAsync(ct)` | Which methods other plugins patched, and how. |
| `AuditAsync(methods, seconds, requestedBy, ct)` | Splits up to 8 patched methods into plugin prefixes, the original and plugin postfixes. |

### Events

| Event | When |
|---|---|
| `SampleRecorded` | A sample was kept, with every list and 20 rows. On the tools only. |
| `RunFinished` | A run finished, whoever started it, a lag trip included. A player's `!monitor test` is not one. |
| `EventRecorded` | A save, lag, a run starting, detail changing, a join hitch or a thread change. Through Quasar also a restart or a plugin or mod change. |
| `IncidentChanged` | Lag began or ended. Never raised on a server plugin. |

Events come on Monitor's own queue, one at a time. A handler that throws is logged, and the next one still runs. If the queue fills, the oldest are dropped, so do not count on hearing every one.

`MonitorEventKind` values: `Save`, `Restart`, `Change`, `Lag`, `Measuring`, `JoinHitch` (version 3) and `ThreadJump` (version 4). `JoinHitch` and `ThreadJump` are never alerts. A `ThreadJump` text can show `#` and an id when the name is not known yet.

## One request, one reply

`RequestAsync` asks for lists of costs, and every source gives the same reply shape. `MeasureAsync`, `CostsAsync` and `SavedRunAsync` do the same by name.

| `MonitorRequest` field | Meaning |
|---|---|
| `Server` | The server's id. Ignored on a server plugin. |
| `Source` | `Run` (measure now), `History` (a past range) or `SavedRun` (a finished run). |
| `Lists` | Which lists come back. Empty means all of them. |
| `Seconds`, `GridId`, `Player`, `Faction`, `BlockType`, `RequestedBy` | For a run. |
| `From`, `To` | For history. |
| `RunId` | For a saved run. Null means the newest. |

The lists: `Grids`, `BlockTypes`, `BlockDefinitions`, `Players`, `Factions`, `Scripts`, `SessionComponents`, `GameLogic`, `Callbacks`, `EntityTypes`, `Mods`, `Rpcs`, `RpcsSent` and `Clusters`.

| `MonitorReply` field | Meaning |
|---|---|
| `State` | `Finished`, `Refused` or `Failed`. |
| `Message`, `RetryAfter` | Why it was refused or failed, and when to try again. |
| `Source`, `From`, `To` | What answered, and the exact time range. |
| `Accuracy` | `EveryCall` for a run. `Background` for history, which is good for trends but not for naming a lag cause. |
| `Frame`, `PartsMs` | Sim speed and frame times, and what the frame went on. |
| `Run` | For a run: `Seconds`, `Frames`, `TimingMsPerFrame`, `Coverage`, `EndedEarly`, `LostSites`, `MainWaitMs`, `ParallelWorkerMs`, `RequestedBy`, `RunId`. |
| `Lists` | One entry per list asked for: `List`, `Available`, `Why` (when not available), `PerFrame` and `Rows`. |
| `SlowCalls` | The slowest single calls. |
| `Timeline`, `World`, `Events` | For history: timings, world figures and events across the range. |

Every `MonitorRow` has the same fields from every source:

| Field | Meaning |
|---|---|
| `Name` | The row's name. |
| `Id` | For grids, players, factions and scripts, whose ids outlast a restart. Null for other rows. |
| `MainMs`, `WorkerMs` | Game thread and worker time, in ms per frame. |
| `PeakMs` | The slowest single call. |
| `CallsPerFrame` | Calls per frame. For `RpcsSent`, events per frame. |
| `Thread` | `Main`, `Worker`, `Mixed`, or `Unknown` for no time and for clusters (version 4). `MonitorThreads.Of(main, worker)` is the rule: a side under 5% does not count. |
| `SeenShare` | For history: the share of background totals the row appeared in. 0.1 means it was in the top a tenth of the time. Always 1 for a run. |
| `Blocks`, `Grids` | Size, on grids, players and factions. |
| `BytesPerSecond` | For `RpcsSent`. |
| `SizeKm`, `Members` | For clusters. |

Good to know:

- Clusters are not in history. That list comes back with `Available = false` and a reason.
- A run has the top 20 of each list. History has the top 20, or 40 for mods.
- A cluster's time is not per frame: `PerFrame` is false on that list.
- Requests share a run: two plugins asking for different lists get one run, as long as it has no filter.

## Example: a Magnetar server plugin

A plugin with two admin commands and an event listener. `!lagnotes sim` answers at once. `!lagnotes grids` measures for 10 seconds, then replies. It still loads and runs when Monitor is not installed.

Project file (the paths to the game and to Magnetar's `PluginSdk.dll` are your own):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Sandbox.Game" HintPath="$(DS64)\Sandbox.Game.dll" Private="False" />
    <Reference Include="VRage" HintPath="$(DS64)\VRage.dll" Private="False" />
    <Reference Include="VRage.Game" HintPath="$(DS64)\VRage.Game.dll" Private="False" />
    <Reference Include="VRage.Library" HintPath="$(DS64)\VRage.Library.dll" Private="False" />
    <Reference Include="VRage.Math" HintPath="$(DS64)\VRage.Math.dll" Private="False" />
    <Reference Include="PluginSdk" HintPath="$(MagnetarBin)\PluginSdk.dll" Private="False" />
    <ProjectReference Include="..\Monitor\src\SenX.Monitor.Api\SenX.Monitor.Api.csproj" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

`LagNotesPlugin.cs`: the plugin, and the check for Monitor.

```csharp
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using PluginSdk.Commands;
using VRage.Plugins;

namespace LagNotes;

/// <summary>A Magnetar plugin that uses Monitor when it is there, and still works when it is not.</summary>
public sealed class LagNotesPlugin : IPlugin
{
    private static bool s_missing;
    private bool _listening;

    public void Init(object gameInstance)
    {
        ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(LagNotesCommands));
    }

    public void Update()
    {
        // Monitor's API appears once the game is ready, so start listening on the first frame it is there.
        if (!_listening && MonitorReady())
        {
            LagNotesEvents.Start();
            _listening = true;
        }
    }

    public void Dispose()
    {
        if (_listening)
            LagNotesEvents.Stop();
    }

    /// <summary>True when Monitor is loaded and ready. Names no Monitor type, so it is safe to call when Monitor is missing.</summary>
    public static bool MonitorReady()
    {
        if (s_missing)
            return false;

        try
        {
            return MonitorUser.Ready();
        }
        catch (Exception)
        {
            // Monitor's API assembly isn't there: Monitor is not installed or not enabled. Don't look again.
            s_missing = true;
            return false;
        }
    }
}

/// <summary>Everything that names Monitor's types, kept apart so the runtime loads them only when used.</summary>
internal static class MonitorUser
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Ready() => SenX.Monitor.Api.MonitorServerApi.Current != null;
}
```

`LagNotesCommands.cs`: the commands.

```csharp
using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PluginSdk.Commands;
using Sandbox;
using SenX.Monitor.Api;
using VRage.Game.ModAPI;

namespace LagNotes;

[CommandRoot("lagnotes", "Lag notes", "Reads Monitor's numbers")]
public sealed class LagNotesCommands : CommandModule
{
    // !lagnotes sim: an instant answer. Latest() doesn't wait for anything, so it is fine on the game thread.
    [Command("sim", "Sim speed and frame time now")]
    [Permission(MyPromoteLevel.Admin)]
    public string Sim()
    {
        if (!LagNotesPlugin.MonitorReady())
            return "Monitor isn't running on this server.";

        return SimText();
    }

    // !lagnotes grids: measures for 10 seconds, then replies. The command returns at once.
    [Command("grids", "Measures for 10 s, then lists the 5 costliest grids")]
    [Permission(MyPromoteLevel.Admin)]
    public string Grids()
    {
        if (!LagNotesPlugin.MonitorReady())
            return "Monitor isn't running on this server.";

        StartMeasuring(Context);
        return "Measuring for 10 s.";
    }

    // Kept apart, so Monitor's types load only after the check above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartMeasuring(CommandContext context)
    {
        // Never wait on the task here: this runs on the game thread, and the run needs it.
        _ = MeasureAndReplyAsync(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string SimText()
    {
        MonitorLatest? latest = MonitorServerApi.Current?.Latest();
        if (latest == null)
            return "Monitor has no numbers yet.";

        return string.Format(CultureInfo.InvariantCulture, "Sim speed {0:0.00}, frame p99 {1:0.0} ms, {2:0.0} ms free.",
            latest.Frame.SimSpeed, latest.FrameP99, latest.Frame.AvailableMs);
    }

    private static async Task MeasureAndReplyAsync(CommandContext context)
    {
        string text;

        try
        {
            IMonitorServerApi? monitor = MonitorServerApi.Current;
            if (monitor == null)
                return;

            MonitorReply reply = await monitor.MeasureAsync(new MonitorRunOptions
            {
                Seconds = 10,
                Lists = new[] { MonitorList.Grids },
                RequestedBy = "LagNotes",
            }, CancellationToken.None).ConfigureAwait(false);

            text = Describe(reply);
        }
        catch (Exception exception)
        {
            text = "The measurement failed: " + exception.Message;
        }

        // Replies go out on the game thread.
        MySandboxGame.Static.Invoke(() => context.Respond(text), "LagNotes");
    }

    private static string Describe(MonitorReply reply)
    {
        // A refusal is a normal answer, such as a run already going or the one-a-minute limit.
        if (reply.State != MonitorReplyState.Finished)
        {
            string wait = reply.RetryAfter is { } after ? " Try again in " + (int)after.TotalSeconds + " s." : string.Empty;
            return reply.State + ": " + reply.Message + wait;
        }

        var text = new StringBuilder();
        text.AppendLine(string.Format(CultureInfo.InvariantCulture, "Sim speed {0:0.00} over {1:0} s", reply.Frame?.SimSpeed ?? 0d, reply.Run?.Seconds ?? 0d));

        MonitorListReply? grids = reply.Lists.FirstOrDefault(list => list.List == MonitorList.Grids);

        foreach (MonitorRow row in (grids?.Rows ?? Array.Empty<MonitorRow>()).Take(5))
        {
            // Main is game thread time: the time that slows the frame.
            text.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:0.00} ms/f {1} {2}", row.MainMs, row.Thread, row.Name));
        }

        return text.ToString();
    }
}
```

`LagNotesEvents.cs`: listening to events.

```csharp
using System;
using SenX.Monitor.Api;

namespace LagNotes;

/// <summary>Listens to Monitor's events. Call Start once Monitor is ready, and Stop when the plugin is disposed.</summary>
internal static class LagNotesEvents
{
    private static IMonitorServerApi? s_monitor;

    public static void Start()
    {
        s_monitor = MonitorServerApi.Current;
        if (s_monitor == null)
            return;

        s_monitor.EventRecorded += OnEvent;
        s_monitor.RunFinished += OnRunFinished;
    }

    public static void Stop()
    {
        if (s_monitor == null)
            return;

        s_monitor.EventRecorded -= OnEvent;
        s_monitor.RunFinished -= OnRunFinished;
        s_monitor = null;
    }

    // Raised off the game thread, on Monitor's own queue. Keep it short, and don't touch the game here.
    private static void OnEvent(MonitorEvent e)
    {
        switch (e.Kind)
        {
            case MonitorEventKind.Lag:
                Console.WriteLine("Lag at " + e.Time.ToString("u") + ": " + e.Text);
                break;
            case MonitorEventKind.JoinHitch:
            case MonitorEventKind.ThreadJump:
                // Information only. Neither is an alert.
                break;
            default:
                // A newer Monitor can add kinds. Treat one you don't know as information.
                break;
        }
    }

    private static void OnRunFinished(MonitorRunFinished run) =>
        Console.WriteLine("Monitor finished run " + run.RunId);
}
```

What `!lagnotes grids` replies, with made-up names:

```text
Measuring for 10 s.
Sim speed 0.97 over 10 s
3.20 ms/f Mixed Iron Hauler
1.75 ms/f Main Outpost Kappa
0.06 ms/f Mixed Drill Rig 7
```

If another run happened less than a minute ago, the reply starts with `Refused:` and says when to try again.

## Example: a Quasar plugin

This is `samples/ApiConsumer` in the source. It adds a page that lists every server with its sim speed, and two buttons: the grids of the last hour (history, free) and the grids measured now (a run, which needs `CanControlServers`). It works with and without Monitor.

`ApiConsumer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>ApiConsumer</AssemblyName>
    <RootNamespace>ApiConsumer</RootNamespace>
    <IsPackable>false</IsPackable>
    <QuasarPluginAbstractionsProject Condition="'$(QuasarPluginAbstractionsProject)' == ''">../../../quasar/Quasar.Plugin.Abstractions/Quasar.Plugin.Abstractions.csproj</QuasarPluginAbstractionsProject>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />

    <!--
      Compile against the API and don't ship it: Monitor brings the one copy. A real plugin links
      to this project the same way, from its own copy of this repository.
    -->
    <ProjectReference Include="..\..\src\SenX.Monitor.Api\SenX.Monitor.Api.csproj" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup Condition="Exists('$(QuasarPluginAbstractionsProject)')">
    <ProjectReference Include="$(QuasarPluginAbstractionsProject)" Private="false" />
  </ItemGroup>

</Project>
```

`quasar-plugin.json`:

```json
{
  "id": "senx.monitor.sample",
  "displayName": "Monitor API sample",
  "version": "0.1.0",
  "entryAssembly": "ApiConsumer.dll",
  "entryType": "ApiConsumer.ApiConsumerPlugin",
  "projectPath": "samples/ApiConsumer/ApiConsumer.csproj"
}
```

`ApiConsumerPlugin.cs`: the plugin and its menu entry.

```csharp
using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Security;

namespace ApiConsumer;

/// <summary>
/// A small plugin that uses Monitor's public API, to prove it through a real Quasar host, with Monitor
/// both installed and not. It's also the example docs/PublicApi.md points to.
/// </summary>
public sealed class ApiConsumerPlugin : IQuasarPlugin
{
    public string Id => "senx.monitor.sample";

    public string DisplayName => "Monitor API sample";

    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context)
    {
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context)
    {
    }

    public IEnumerable<Assembly> GetRazorAssemblies()
    {
        yield return typeof(ApiConsumerPlugin).Assembly;
    }

    public IEnumerable<QuasarNavItem> GetNavItems()
    {
        yield return new QuasarNavItem("Monitor API sample", "/monitor-api-sample", string.Empty, QuasarNavZones.Tools, 900, QuasarPolicyNames.CanView);
    }

    public IEnumerable<QuasarExtensionContribution> GetExtensions() => [];
}
```

`SamplePage.razor`: the page. It names no Monitor type, so it loads when Monitor is missing.

```razor
@page "/monitor-api-sample"
@using System.Runtime.CompilerServices
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components
@using Quasar.Plugin.Abstractions.Security
@attribute [Authorize(Policy = QuasarPolicyNames.CanView)]
@inject IServiceProvider Services

@* Nothing on this page names a Monitor type, so it still loads when Monitor isn't installed. *@

@if (_panel == null)
{
    <p>Monitor isn't installed, so there's nothing to show.</p>
}
else
{
    <DynamicComponent Type="@_panel" />
}

@code {
    private const string ApiType = "SenX.Monitor.Api.IMonitorApi, SenX.Monitor.Api";

    private Type? _panel;

    protected override void OnInitialized()
    {
        // Null when Monitor is missing or disabled, because no folder Quasar looks in has the file.
        Type? api = Type.GetType(ApiType, throwOnError: false);

        if (api != null && Services.GetService(api) != null)
            _panel = Panel();
    }

    // Kept apart, so the runtime only loads Monitor's types once it's known they're there.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Type Panel() => typeof(MonitorPanel);
}
```

`MonitorPanel.razor`: everything that uses Monitor. It is only created after the check.

```razor
@using System.Globalization
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.Web
@using Quasar.Plugin.Abstractions.Security
@using SenX.Monitor.Api
@inject IMonitorApi Monitor
@inject IAuthorizationService Authorization
@inject AuthenticationStateProvider Authentication

<h1>Monitor API sample</h1>

<ul>
    @foreach (MonitorServerInfo server in Monitor.Servers())
    {
        <li>
            @server.Name: sim @(Monitor.Latest(server.Id)?.Frame.SimSpeed.ToString("0.00", CultureInfo.InvariantCulture) ?? "not collected yet")
            <button type="button" @onclick="() => HistoryAsync(server.Id)">Grids, last hour</button>
            <button type="button" @onclick="() => RunAsync(server.Id)">Grids, measure now</button>
        </li>
    }
</ul>

@if (_reply is { } reply)
{
    <p>@reply.State, @reply.Accuracy, @reply.Message</p>

    @foreach (MonitorListReply list in reply.Lists)
    {
        <h2>@list.List @(list.Available ? null : "(" + list.Why + ")")</h2>
        <ul>
            @foreach (MonitorRow row in list.Rows)
            {
                <li>@row.Name: @((row.MainMs + row.WorkerMs).ToString("0.00", CultureInfo.InvariantCulture)) ms/f</li>
            }
        </ul>
    }
}

@code {
    private MonitorReply? _reply;

    // The same reply shape from both, so showing it doesn't care which this was.
    private async Task HistoryAsync(string server) => _reply = await Monitor.RequestAsync(
        new MonitorRequest
        {
            Server = server,
            Source = MonitorSource.History,
            Lists = [MonitorList.Grids],
            From = DateTime.UtcNow.AddHours(-1),
            To = DateTime.UtcNow,
        },
        CancellationToken.None);

    private async Task RunAsync(string server)
    {
        // The API can't see who's asking, so checking is this plugin's job.
        AuthenticationState state = await Authentication.GetAuthenticationStateAsync();
        if (!(await Authorization.AuthorizeAsync(state.User, QuasarPolicyNames.CanControlServers)).Succeeded)
            return;

        _reply = await Monitor.RequestAsync(
            new MonitorRequest
            {
                Server = server,
                Source = MonitorSource.Run,
                Lists = [MonitorList.Grids],
                Seconds = 10,
                RequestedBy = "Monitor API sample, for " + state.User.Identity?.Name,
            },
            CancellationToken.None);
    }
}
```

Do not ask for Monitor's services in `ConfigureServices`. Plugins register in no fixed order, and services exist only after all of them have finished.

### Listening to events in a Quasar plugin

`IMonitorApi` raises events for every server. Subscribe once, for example in a component that lives as long as your page, or in your own singleton service that you create after the check above:

```csharp
Monitor.IncidentChanged += incident =>
{
    // Off the request thread, on Monitor's queue. Hand real work off.
    string state = incident.Closed == null ? "began" : "ended";
    Console.WriteLine($"Lag on {incident.Server} {state}: {incident.Reason}, lowest sim {incident.LowestSim:0.00}");
};
```

Remove your handler again when your component or service is disposed.

## More

The full technical notes are in [docs/PublicApi.md](https://github.com/buttheadbob/Monitor/blob/main/docs/PublicApi.md) in the source.
