@echo off
rem Starts Dingler's login server (http://localhost:5000) with the portable .NET in HexPVE\dotnet-sdk.
rem Nothing is installed on Windows. Close this window to stop it.
title Dingler login server
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
cd /d "%~dp0Dingler.Auth\bin\Release\net10.0"
"%DOTNET_ROOT%\dotnet.exe" Dingler.Auth.dll
pause
