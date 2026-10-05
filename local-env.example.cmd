@echo off
rem Copy this file to local-env.cmd (next to it) and edit the values. local-env.cmd is not tracked by git.
rem The start scripts call it before starting a server. Settings here override appsettings.json.

rem Folder of your HEX: Shards of Fate installation (the one with Hex_Data and Data).
set "GamedataLocation=C:/Games/HEX SHARDS OF FATE"

rem Optional: a portable .NET SDK folder. Leave it out to use the dotnet on your PATH.
rem set "DOTNET_ROOT=C:\path\to\dotnet-sdk"

rem Optional: the Hex Codex data folder (it holds ids.json and gems.json), only for the deck import feature.
rem set "DeckImport__SiteDataPath=C:/path/to/hex-codex/data"
