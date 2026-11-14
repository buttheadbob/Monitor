<#
.SYNOPSIS
    Builds Monitor into a Quasar plugin package folder, the same shape Quasar installs from a hub.

.DESCRIPTION
    The build uses the arguments Quasar itself passes. Nothing is installed unless -Destination
    points at Quasar's Plugins folder, and Quasar only picks a new package up after a restart.

.EXAMPLE
    ./tools/Package-Local.ps1 -QuasarInstall <QuasarRoot>/QuasarApp
    Builds to artifacts/senx.monitor in this repo.

.EXAMPLE
    ./tools/Package-Local.ps1 -QuasarInstall <QuasarRoot>/QuasarApp -Destination <QuasarRoot>/Quasar/Plugins/senx.monitor
    Builds straight into Quasar's Plugins folder.
#>
[CmdletBinding()]
param(
    [string] $Destination,
    # The folder holding Agent\Magnetar.Protocol.dll and Quasar.Plugin.Abstractions.dll.
    [Parameter(Mandatory)]
    [string] $QuasarInstall,
    [string] $DS64
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $repo 'artifacts\senx.monitor' }

$protocol = Join-Path $QuasarInstall 'Agent\Magnetar.Protocol.dll'
$abstractions = Join-Path $QuasarInstall 'Quasar.Plugin.Abstractions.dll'

foreach ($required in $protocol, $abstractions) {
    if (-not (Test-Path $required)) { throw "Not found: $required. Set -QuasarInstall to your Quasar install." }
}

$companionOutput = Join-Path $Destination '.quasar\companions\senx.monitor'

# Quasar deploys every file in here to the servers, so nothing can be left from an older build.
if (Test-Path $companionOutput) { Remove-Item $companionOutput -Recurse -Force }

$adapterArgs = @(
    'build', (Join-Path $repo 'src\SenX.Monitor.Quasar\SenX.Monitor.Quasar.csproj'),
    '-c', 'Release', '-v:minimal', '-o', $Destination,
    "-p:QuasarPluginAbstractionsAssembly=$abstractions"
)

$companionArgs = @(
    'build', (Join-Path $repo 'src\SenX.Monitor.QuasarCompanion\SenX.Monitor.QuasarCompanion.csproj'),
    '-c', 'Release', '-v:minimal', '-o', $companionOutput,
    '-p:Platform=x64', '-p:CopyLocalLockFileAssemblies=true',
    "-p:MagnetarProtocolAssembly=$protocol"
)

if ($DS64) { $companionArgs += "-p:DS64=$DS64" }

& dotnet @adapterArgs
if ($LASTEXITCODE -ne 0) { throw 'The Quasar adapter failed to build.' }

& dotnet @companionArgs
if ($LASTEXITCODE -ne 0) { throw 'The Magnetar companion failed to build.' }

Copy-Item (Join-Path $repo 'quasar-plugin.json') $Destination -Force

$assets = Join-Path $Destination 'src\SenX.Monitor\wwwroot'
New-Item -ItemType Directory -Force $assets | Out-Null
Copy-Item (Join-Path $repo 'src\SenX.Monitor\wwwroot\*') $assets -Recurse -Force

Write-Host "Package ready: $Destination"
