:: One time setup for a fresh clone.
::  - Junctions to the Dedicated Server and Torch binaries, which Directory.Build.props finds by itself
::  - To use other paths, put them in Directory.Build.props.user, which wins over these
@echo off
cd /d "%~dp0"

set /p GAMEDIR="Folder containing SpaceEngineersDedicated.exe (inside Torch: the DedicatedServer64 folder): "
if exist GameBinaries rmdir GameBinaries
mklink /J GameBinaries "%GAMEDIR%"
if errorlevel 1 goto Error

set /p TORCHDIR="Folder containing Torch.Server.exe: "
if exist TorchBinaries rmdir TorchBinaries
mklink /J TorchBinaries "%TORCHDIR%"
if errorlevel 1 goto Error

echo Done! You can now open Monitor.slnx or Monitor.Torch.slnx.
goto End

:Error
echo An error occurred creating the junction.

:End
pause
