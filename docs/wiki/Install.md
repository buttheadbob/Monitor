# Install

Monitor has two parts. Install both for the full set of features:

1. The **Quasar plugin**, once, in Quasar.
2. The **Magnetar plugin**, on each server.

You can also install only the Magnetar plugin. See [Without Quasar](#without-quasar).

## What you need

- Quasar version 1.6 or newer.
- Servers that run the Magnetar plugin loader. Both Windows servers (Legacy and Interim) and Linux servers (Interim) work.

## Quasar

1. Get the package folder `senx.monitor`.
   - From source: run `tools/Package-Local.ps1 -QuasarInstall <your Quasar app folder>`. It builds the folder in `artifacts/senx.monitor`.
2. Stop Quasar.
3. Copy the `senx.monitor` folder into Quasar's `Plugins` folder. If an older `senx.monitor` folder is there, replace it.
4. Start Quasar.
5. Quasar builds a small companion part and sends it to each managed server. Restart each server, so that it loads the companion.

Quasar keeps Monitor's history and settings in the `monitor` folder inside Quasar's install folder.

## Magnetar

Magnetar builds Monitor from its source code. There is no DLL to copy.

### From MagnetarHub

When Monitor is on MagnetarHub, install it like any other hub plugin: turn on **Monitor** in the server's plugin list (in Quasar, or in MagnetarConfig with F8 on the server), then restart the server.

### From your own copy of the source

1. Download the source from https://github.com/buttheadbob/Monitor.
2. On the server, press F8 to open MagnetarConfig.
3. Open **Local & Dev Folders**.
4. Pick `Monitor.xml` from the Monitor source folder.
5. Choose **Release**.
6. Restart the server.

If the server runs under Quasar, you can add the folder in Quasar's dev folders instead.

## Check that it works

1. Wait until the world is loaded, then wait 30 more seconds.
2. In the game or the server console, type `!monitor status`. You should see sim speed and frame times.
3. In Quasar, open the **Monitor** page and pick the server. After about a minute the charts fill in.

If the Monitor page says "Monitor's server plugin isn't running on this server", the Quasar part works, but the server does not run the Magnetar plugin. Turn Monitor on in that server's plugin list and restart the server. If it is on, look in the server's log for a Monitor build error. See [Troubleshooting](Troubleshooting).

## Without Quasar

A Magnetar server without Quasar still measures everything, and every chat command works.

- It keeps about one hour of history in memory.
- It writes nothing to disk.
- It uses the default settings. See [Settings](Settings).

## Update

Both parts must come from the same version.

1. Replace the `senx.monitor` folder in Quasar's `Plugins` folder.
2. Update the Magnetar plugin (from the hub, or from your source folder).
3. Restart Quasar, then restart each server.

## Remove

1. Delete the `senx.monitor` folder from Quasar's `Plugins` folder.
2. Remove Monitor from each server's plugin list.
3. Restart Quasar and the servers.

Monitor removes all its patches when it stops. To delete its history too, delete the `monitor` folder inside Quasar's install folder.

## Torch

A Torch build exists as source code in `src/SenX.Monitor.Torch`. It is not supported and gets no new features. See `docs/Torch.md` in the source if you build it anyway.
