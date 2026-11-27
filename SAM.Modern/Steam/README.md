# Steam/ - the Steam client API layer

This folder is derived from **SAM.API** of [gibbed/SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager)
(zlib license, see `../LICENSE-SAM.txt`). It loads `steamclient(64).dll` from the Steam installation, creates a pipe and a user
connection, and wraps the few native interfaces the app needs (`ISteamClient018`, `ISteamUser012`, `ISteamUserStats013`,
`ISteamApps001/008`, `ISteamUtils005`, plus `ISteamFriends015`).

Every change against the original is marked in the code with `MODIFIED (SAM Modern)` or `ADDED (SAM Modern)`:

| Change | Where | Why |
|---|---|---|
| **Fix:** `GetISteamApps` delegate got the missing `this` pointer and `ThisCall` | `Wrappers/SteamClient018.cs` | The original only worked by accident on x86; on x64 every argument shifts by one register and Steam returns NULL. |
| Loads `steamclient64.dll` in a 64-bit process | `Steam.cs` (`Load`) | Needed when the host runs as x64 (current Steam clients are 64-bit). |
| Install path: per-user `SteamPath` first, then both HKLM views | `Steam.cs` (`GetInstallPath`) | Works for per-user and portable Steam setups. |
| Added `ISteamFriends015` + wrapper, `Client.GetFriends()` | `Interfaces/`, `Wrappers/`, `Client.cs` | Friends list (names and IDs only), created on demand. |
| Added DLC list of the running app | `Wrappers/SteamApps008.cs` | `GetDLCCount` / `GetDLCDataByIndex`. |
| Added `GetUserAchievementAndUnlockTime(user, ...)` | `Wrappers/SteamUserStats013.cs` | Read the achievements of another user after `RequestUserStats(thatUser)`. |
| Added `UserStatsStored` callback | `Callbacks/UserStatsStored.cs` | Wait for the result of `StoreStats`. |
| Removed `UserItemsReceived`, `ItemRequestResult` | `Types/` | Unused. |

## Things worth knowing

* **Do not delete "unused" methods from the `Interfaces/ISteam*.cs` files.** The *order* of the methods in such an interface is the
  layout of the native virtual-function table. A removed declaration shifts every later method by one slot and calls end up in the
  wrong function.
* **Callback structs are tightly packed** (`Pack = 1`): in `UserStatsReceived` the `SteamIdUser` field starts at byte offset 12,
  right after `Result`.
* **One request can produce several callbacks.** For example `RequestUserStats(self)` is answered twice (about 0.2 s apart). Always
  match a callback to its request by `SteamIdUser` instead of taking the first one that arrives (see `AnswerFor` in
  `Workers/WorkerMain.cs`).
