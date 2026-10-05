@echo off
rem Starts Dingler's game server (127.0.0.1:9933). Start the login server first.
rem In the menu, choose "Start Server" (arrow keys, Enter). Choose "Exit" to stop it.
rem Machine-specific settings go in local-env.cmd (not tracked); copy local-env.example.cmd to start.
title Dingler game server
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
if exist "%~dp0local-env.cmd" call "%~dp0local-env.cmd"
set "DOTNET=dotnet"
if defined DOTNET_ROOT set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
cd /d "%~dp0Dingler.Terminal\bin\Release\net10.0"
"%DOTNET%" Dingler.Terminal.dll
pause
