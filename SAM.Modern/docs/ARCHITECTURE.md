# Architecture

SAM Modern is **one executable with three roles**, chosen by its command line (`Program.cs`):

| Command line | Role |
|---|---|
| *(none)* | The app. Starts the local web server and shows one WebView2 window. |
| `--serve [--port N] [--token T]` | Web server only, no window. For development: open `http://127.0.0.1:N/#t=T` in any browser. |
| `--api library` / `--api game <appId>` | A short-lived **worker** process that talks to the Steam client. |

```
 WebView2 window (UI/MainForm.cs)
        │  http://127.0.0.1:<random port>/#t=<random token>
        ▼
 Web/ApiHost.cs ── Kestrel on loopback only, token + Host + Origin checks, JSON API under /api
        │  calls
        ▼
 Core/Services.cs ── Library, GameSessions, ScanService, FriendScanService, AutoService, ...
        │  one JSON line per message over stdin/stdout
        ▼
 Workers/WorkerProcess.cs ──► child process: Workers/WorkerMain.cs ──► Steam/ (SAM.API) ──► steamclient64.dll
```

## Why workers?

* Steam lets **one** process act as a given game per user. Every game therefore needs its own process (this is also how
  the original SAM works), and batch jobs start one worker per game, one after another.
* A hung Steam call must not take the app down: the host just kills the worker.
* While a worker for game X runs, Steam shows the user as "playing X". Closing the worker ends that.

## Where data comes from

| Data | Source | Cost |
|---|---|---|
| Which games the account owns | `library` worker: `IsSubscribedApp` for every app of the public game list (`Core/AppData.cs`, `GamesList`) | one worker, a few seconds, cached |
| Progress of every game | Local files in `<Steam>/appcache/stats` (`Core/Schema.cs`, `LocalProgress`) | instant, no Steam call, no "playing" |
| Exact progress, achievements, stats | `game` worker: `load` | a worker per game |
| Which DLC an achievement needs | The schema's achievement *groups* (`dlcappid`), see `WorkerMain` "DLC detection"; text matching as a fallback | free for games with groups |
| Friends' progress | `game` worker: `friends` (needs the game context), stored as counts only | a worker per game, explicit user action |

## Folder map

| Folder | Content |
|---|---|
| `Steam/` | Steam client API layer, derived from SAM.API of gibbed/SteamAchievementManager. See `Steam/README.md` for the list of changes. |
| `Core/` | Pure logic without UI: schema/progress reading (`Schema.cs`), KeyValues + VDF parsers, stores for everything persisted (`AppData.cs`), the services (`Services.cs`), account switching. |
| `Workers/` | `WorkerMain` (child side) and `WorkerProcess` (parent side, JSON-line protocol). |
| `Web/` | `ApiHost`: the HTTP server and all routes. |
| `UI/` | `MainForm`: the window that hosts WebView2. |
| `wwwroot/` | The web UI: `index.html`, `app.js` (no framework), `app.css`, `icons.js`. |

## Rules the code keeps (worth preserving when changing it)

1. **Nothing about friends is queried unless the user switched the comparison on, chose the friends, and clicked an action.**
   The host enforces this (`FriendService.RequireEnabled`, `confirm: true`, "friend-not-selected"), not only the UI.
2. **Only one batch job at a time** (`ApiHost.RequireNoBatchExcept`), and no game can be opened while one runs.
3. **A failed Steam call must never break the library**: DLC information and friends data are best-effort extras.
4. **Persisted files are plain JSON, written atomically**, and a corrupt file just means "start empty".
5. **Match Steam callbacks to their request** (`WorkerMain.AnswerFor`): one request can produce several callbacks.
6. All text shown to the user is English.

## Running and building

```powershell
dotnet build                      # debug build
bin\Debug\net10.0-windows\win-x64\SteamAchievementManager.exe --serve --port 51730 --token devtoken
dotnet publish -c Release -r win-x64 --self-contained true -o dist
```

Requirements: Windows 10/11 x64, the WebView2 runtime, a running Steam client. A framework-dependent build
(`--self-contained false`) also needs the .NET 10 Desktop Runtime and the ASP.NET Core Runtime 10.
