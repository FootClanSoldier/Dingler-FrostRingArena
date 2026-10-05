# Frost Ring Arena for Dingler

This branch (`arena`) adds the **Frost Ring Arena**, HEX's PvE mode, to [Dingler](https://github.com/Blitzkind/Dingler). It is built on Blitzkind's Dingler (commit `1c668bc`), and every change is on top of his commits. Dingler's license (AGPL-3.0) is unchanged. Apart from one pointer line in the README, the edits to existing files are listed under "For a merge".

**How it was made:** most of the code, and the research behind it, was written with Claude (Anthropic), working from the game client's own data and compiled code. Most steps were played and tested in the real client by its owner. A few paths have not been seen in play yet: the sleeve rewards, and an account flag earned in the middle of a run. Commits carry a `Co-Authored-By` line.

No game files are included. You need your own copy of HEX: Shards of Fate, as with stock Dingler. HEX is not affiliated with this project.

## What it adds

- **The arena:** a run of 20 fights in 4 tiers. Opponents are the client's own 109 arena decks, played by the client's own AI. It has strikes (3 ends the run), bosses (a lost boss is replayed), perfect tiers, Hogarth's 21 challenges with their buffs, loot and cash-out, account flags, "Skip Tier 1", and the 5 arena sleeves.
- **Deck import:** `/importdeck <link>` in chat, or a backup file in an inbox folder, adds decks from the [Hex Codex](https://hex-codex.romosjr.workers.dev/deck-builder/) deck builder to your account. Optional; it needs the `ids.json` and `gems.json` data files from that site's repo.
- **Fixes to Dingler itself,** found along the way (all small):
  - the engine's crash path could not end a game, so the match stayed registered (`HexRulesEngine.EndGame`, the pump loop);
  - `HexGameWrapper.IsGameEnded` was never set, so a finished game could be offered as a reconnect;
  - the login server logged passwords in plain text (`Dingler.Auth/appsettings.json`);
  - `GameManager`'s list of running games is now a `ConcurrentDictionary`.

## The rules, in short

The original server's rules are not in the client, so some are rebuilt. The owner chose the following, and each is one place in the code:

| Rule | Where |
|---|---|
| 4 tiers of 5 fights. Tier 1: 4 of 6 easy opponents, then Eternal Guardian. Tiers 2–4: 4 opponents and a boss each. | `Arena/ArenaBracketBuilder.cs` |
| Elites at fights 9, 12, 14, 17 and 19. The last boss is an Elite version. | same |
| Uruunaz and Zakiir: 1% each per run, replacing a normal fight. | same |
| One Hogarth challenge per tier. Winning it removes a strike, or gives a boss-fight buff if you have none. | `Arena/ArenaChallengeMods.cs`, `ArenaBattleService.cs` |
| Gold pouches 240/330/390/480 per win, boss chests (equipment), a bonus chest 25% of the time, a chest for each perfect tier, and Uruunaz's 50,000 gold and card. | `Arena/ArenaLoot.cs` |
| Loot is shown and logged. Nothing enters your collection, because Dingler already gives every player every card. | |

## Run it (Windows)

You need: your HEX: Shards of Fate game files, the free **.NET 10 SDK**, and git.

1. **Get the code:** `git clone <this repo>`, then `git checkout arena`.
2. **Game DLLs.** From your HEX folder's `Hex_Data\Managed`, copy these 7 files:
   `Assembly-CSharp-firstpass.dll`, `ICSharpCode.SharpZipLib.dll`, `NCalc.dll`, `SampleClassLibrary.dll`, `System.EnterpriseServices.dll`, `System.Web.Services.dll`, `UnityEngine.dll`.
   - `Assembly-CSharp-firstpass.dll`, `NCalc.dll` and `ICSharpCode.SharpZipLib.dll` go into the `DLLs` folder (for the build).
   - After building, copy all 7 into `Dingler.Terminal\bin\Release\net10.0`.
3. **Build:** `dotnet build Dingler.slnx -c Release -p:WarningsAsErrors=`. The last switch is needed with a newer SDK than `global.json` pins (it turns 8 new nullable warnings back into warnings).
4. **Settings.** Copy `local-env.example.cmd` to `local-env.cmd` and set `GamedataLocation` to your HEX folder. (You can edit `Dingler.Terminal\appsettings.json` instead.)
5. **Point the client at your server.** In your HEX folder's `config.ini` (keep a copy of the original):
   ```
   GameServerIP=127.0.0.1:9933
   CZEAuthUrl=http://localhost:5000/auth/hexlogin
   ```
6. **Start the servers, in this order:**
   1. `start-login-server.cmd`. Wait until `http://localhost:5000/.well-known/jwks.json` answers.
   2. `start-game-server.cmd`, then choose **Start Server**.
   3. Start HEX. **Restart HEX every time you restart the servers;** it doesn't reconnect by itself.
7. **Log in** with any name and password. A new name is created on first login.
8. **Play:** open the Frost Ring Arena in the client, pick a deck, and fight. Withdraw at any time to take your loot.

**Where your data lives:** `Dingler.Terminal\bin\Release\net10.0\data\` (`gameData.db` with accounts and decks, and `arena\` with runs and flags). To move to another PC, copy that folder. Logs are in `...\logs\log.txt`; lines starting with `Arena:` describe each fight.

**Windows Smart App Control:** it may block the freshly built, unsigned server files. The author tested with it switched off.

## Deck import (optional)

1. Clone the Hex Codex repo, or copy its `data` folder. Set `DeckImport__SiteDataPath` in `local-env.cmd` to that `data` folder.
2. **Chat:** on the deck builder choose *Copy link*. In HEX chat send `/importdeck ` and paste. Log out and in to see the deck.
3. **Inbox:** download the site's decks backup (it includes equipment). Put it into `Dingler.Terminal\bin\Release\net10.0\data\deck-inbox\<your name>\` and log in. Files are then moved to `imported\`.

HEX reads your deck list only at login, so a new deck always needs one login. An imported deck never replaces another; a taken name becomes "Name (2)".

## For a merge

New code: `Dingler.Game/Arena/`, `Dingler.Game/DeckImport/`, `Handlers/Request/Arena/`, and new handlers in `Handlers/Request/Campaign/`.

Changes to existing files:

| File | Change |
|---|---|
| `Games/HexRulesEngine.cs` (+ new `HexRulesEngine.Arena.cs`) | the AI seat hooks, the crash-path fix, an `EndGame` guard |
| `Games/GameManager.cs`, `GameTimer.cs`, `HexGameWrapper.cs` | arena game creation and clocks, `ConcurrentDictionary`, `IsGameEnded` |
| `Handlers/Request/Campaign/GetMasterListOfChallengers…`, `JoinCampaignArena…` | the empty stubs now answer |
| `Handlers/Request/Game/QuitOnReconnectionGame…` | declining an arena reconnect forfeits the fight |
| `Handlers/Request/ChatMessageRequestHandler.cs`, `Mail/UnreadMailAsyncRequestHandler.cs` | deck import hooks |
| `Services/CollectionCacheService.cs`, `DeckService.cs` | account flags in the profile stream, the arena deck lock |
| `CompositionRoot/CompositionRoot.cs` | service registration |
| `Dingler.Auth/appsettings.json`, `Dingler.Terminal/appsettings.json` | log level, deck import setting |
| `start-*.cmd`, `.gitignore`, `local-env.example.cmd` | settings in an untracked `local-env.cmd` |
