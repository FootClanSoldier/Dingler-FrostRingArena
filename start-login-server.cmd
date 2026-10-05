@echo off
rem Starts Dingler's login server (http://localhost:5000). Close this window to stop it.
rem Machine-specific settings go in local-env.cmd (not tracked); copy local-env.example.cmd to start.
title Dingler login server
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
if exist "%~dp0local-env.cmd" call "%~dp0local-env.cmd"
set "DOTNET=dotnet"
if defined DOTNET_ROOT set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
cd /d "%~dp0Dingler.Auth\bin\Release\net10.0"
"%DOTNET%" Dingler.Auth.dll
pause
