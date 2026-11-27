# Steam Achievement Manager Modern

A modernized, **modified version** of
[gibbed/SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager) (zlib license, see `LICENSE-SAM.txt`).
The Steam integration (`Steam/`) comes from the original (SAM.API) and has been adapted; everything else is new.

> **This is not the original Steam Achievement Manager** and it is not made or endorsed by its author. It is an altered
> version, marked as such as the zlib license requires. It is also not affiliated with Valve; "Steam" is a trademark of Valve.
> Changing achievements can be considered cheating by some games, their communities or leaderboards - use it at your own risk,
> on games where you are fine with that.

**What is different from the original?** The interface is a modern web UI hosted in a WebView2 window (instead of WinForms
lists), served by a small local server; every Steam conversation runs in a separate worker process; the app targets .NET 10
and 64-bit Steam. The features below are new. How it is built: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). What was changed in the
Steam layer: [Steam/README.md](Steam/README.md).

## Features

| Feature | How it works |
|---|---|
| **Choose the account** | Account menu at the top right. Steam can only be signed in with *one* account at a time, so when you switch, SAM shuts Steam down cleanly and restarts it with the chosen account (auto-login). Beforehand, `loginusers.vdf` and the previous registry values are backed up to `%LOCALAPPDATA%\SteamAchievementManagerModern\backups`. |
| **Auto-unlock** | Unlocks every achievement SAM may change, game after game: for all games that still have something left, for the games in the current view, or for games you pick. A confirmation is required, you can stop after any game, and **"Undo this run"** locks again exactly what the run unlocked. A *Lock* mode exists as well (extra confirmation, only for games you tick). |
| **100% games** | Gold trophy badge in the top-right corner of the card plus a gold frame. Switch "Hide 100% games" and filter "100% reached". |
| **Games without achievements** | Switch "Hide games without achievements" and filter "No achievements". |
| **Unlockable only in the game** | Orange controller badge showing how many in-game achievements are still open. They are marked with a lock in the game view and cannot be toggled. |
| **Needs a DLC you don't own** | Violet package badge with the number of achievements locked behind a missing DLC; per achievement a "Missing DLC: …" tag, and a banner in the game view explaining why 100% is out of reach. **Primary source:** many games assign every achievement to a *group* and every group to a DLC in the stats schema Steam keeps locally (`dlcappid`) - SAM uses exactly that link, so no guessing is involved. Ownership comes from `IsSubscribedApp`; if an achievement of that DLC is already unlocked, the DLC is treated as accessible (Steam's flag alone is not always right). **Fallback** for games without such groups: the link is detected from the achievement text (for example "requires Scandinavia DLC"; several DLCs in one text are handled), using the DLC ID list of the public Steam store (only the app ID is sent, cached for 14 days). For games with such groups the badge appears for the **whole library at once** (one quick ownership lookup in the Steam client, cached for 24 hours and refreshed with "Reload"; no game is started). The tooltip of each tag says which source was used. Verified with Euro Truck Simulator 2 (text fallback: 100 of 110 DLCs missing, 72 locked achievements identified) and Call of Duty (groups: 2 missing DLCs cover all 68 locked achievements). |
| **Why a change failed** | If Steam refuses a change, a dialog lists every affected achievement with its reason: needs a DLC you don't own (names the DLC), controlled by the game, unknown to Steam (outdated game data), or "refused without a reason". Steam itself never states a reason, so the last case is the honest fallback. The Auto-unlock details show the same summary per game. |
| **New in your library / new achievements** | Green "New" badge for games added since you last looked, cyan "+N" badge for games whose achievement count grew. Opening a game, or "Mark all as seen", clears the marker. The library refreshes itself in the background; new achievements are noticed when Steam updates its local data (open the game or use "Check progress"). |
| **Several badges at once** | All markers sit in the top-right corner of a card and can appear together. |
| **Filters & sorting** | Search (name/app ID), completion, SAM-ability, what's new, progress range, minimum achievements, game type, installation, last played; eight-plus sort orders; card and list view; dark/light theme. |
| **Collapsible filter sections** | Every filter group folds away. A collapsed group still shows what is set inside it, and active filters are listed as removable chips above the games. |
| **Quick views** | One-click filter sets ("Still to do", "Almost done", "Easy wins", ...). Save your own with *Save current view*, or select specific games and save them as a view ("Select games"). Views are stored in `prefs.json`. |
| **Friends comparison** | **Off** by default. After "Activate" you choose friends; achievement data is queried only for those, and only when you click "Compare" inside a game or "Load friends' progress" in the library. The Steam IDs of your selection are stored, plus - if you load their progress - only counts per game (for example 12 of 45), never achievement names or dates. Deselecting a friend or deactivating deletes them. |
| **Friends' progress on the cards** | "Load friends' progress" (sidebar, *Friends comparison*) asks Steam, game after game, how far your selected friends are - for the games in the current view or all games with achievements (roughly 1.5 seconds per game and friend; you appear as "playing" for a moment, there is a Cancel button). Afterwards every card shows one slim bar per friend under the game's own bar (avatar, name, x/total; gold at 100%; at most three, then "+N more"), in card and list view. If Steam hands out nothing for a game (the friend does not own or never played it, or keeps game details private - Steam does not say which), the card says "Friend: no data" instead of staying silent, with the explanation in its tooltip; the sidebar summarizes how many games were checked and how many had data. Games loaded within the last 6 hours are skipped on the next run. |
| **Friends in the overview tiles** | With friends' progress loaded, the tiles *Games with achievements*, *100% completed* and *Achievements unlocked* get one slim row per friend: how many of your games they have (Steam shows their progress), how many of those they completed, and how many achievements they unlocked of the total in those games (with a bar). Only games you both have can be compared, so the numbers are counted over those; the tooltip of a row adds your own numbers for the same games. Until every game has been checked, the first tile says how many were (with a "Load" link). Same privacy rules as above - it only uses the stored counts. |

Also included: editing statistics (behind a safety switch), achievement rarity (public Steam web API, no key needed),
progress of all games instantly from the local Steam cache, an optional deep scan through the Steam API,
and `Esc` to go back from a game.

## Running

[Download the latest release](https://github.com/enmel541/SteamAchievementManager/releases/latest), unzip it and start
`SteamAchievementManager.exe` (Steam must be running). Requirements: Windows 10/11 x64 and the Microsoft WebView2 runtime
(preinstalled on Windows 11). The release brings its own .NET runtime.

## Building

With the .NET 10 SDK:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o dist
```

With `--self-contained false` the build is much smaller, but the PC then needs both the
[.NET 10 Desktop Runtime and the ASP.NET Core Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0).

## Notes

* **One game at a time:** Steam only lets one process act as a game for your account. Do not use two Achievement Manager
  windows (or a Steam game) at the same time while one of them has a game open or runs Auto-unlock.
* **Linked statistics:** Some games link achievements to statistics (for example "travel one mile"). Unlocking can raise such a
  statistic, and locking again can reset it to 0. *Undo* restores the achievements, not these linked statistics.
* **Progress:** The percentages come from the local Steam cache (`appcache/stats`) - instant and without side effects.
  For games without a cache, or for exact values, use "Check progress". During that check you appear as "playing"
  for 1-2 seconds per game, which is why it only runs on request.
* **In-game achievements:** Detected through the `permission` flag in the Steam schema (`permission & 3 != 0`, as in the original SAM).
* **Friends:** Steam only hands out what your friend has made visible to friends.
* **Security:** The local server listens on `127.0.0.1` only, requires a random token and checks the Host/Origin headers.
* **Bug fixed from the original:** `ISteamClient018::GetISteamApps` was called without the `this` pointer. That only worked
  by accident on x86 and fails on 64-bit Steam.

Data (caches, settings, views, backups): `%LOCALAPPDATA%\SteamAchievementManagerModern`.
