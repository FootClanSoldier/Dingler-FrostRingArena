@echo off
rem Starts Dingler's game server (127.0.0.1:9933) with the portable .NET in HexPVE\dotnet-sdk.
rem Start the login server first. In the menu, choose "Start Server" (arrow keys, Enter). Choose "Exit" to stop it.
title Dingler game server
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
cd /d "%~dp0Dingler.Terminal\bin\Release\net10.0"
"%DOTNET_ROOT%\dotnet.exe" Dingler.Terminal.dll
pause
