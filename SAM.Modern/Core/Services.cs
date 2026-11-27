using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamModern.Workers;

namespace SamModern.Core
{
    // ==========================================================================================================
    // The services behind the HTTP API (see Web/ApiHost.cs). Overview:
    //
    //   LibraryService      which games the account has + their progress (local cache files, newest data wins)
    //   DlcIds / DlcOwnership / GameLoader / ProgressRecorder
    //                       everything about "this achievement needs a DLC you do not own"
    //   GameSessions        the ONE worker process for the game that is open in the UI
    //   FriendService       the friends list; FriendScanService loads friends' progress for many games
    //   ScanService         "Check progress": exact progress of many games through short-lived workers
    //   AutoService         "Auto-unlock": unlocks/locks everything SAM may change, with undo
    //   RarityService / ImageService   public web data (global unlock rates, cover images)
    //
    // The three batch jobs (ScanService, FriendScanService, AutoService) share one shape: Start() launches a background
    // task; the UI polls Status(cursor) once a second and receives only the results added since its last cursor; Cancel()
    // stops after the current game. Only ONE of them may run at a time (they all need Steam's single "game" slot).
    // ==========================================================================================================

    // ====================================================================== Library

    /// <summary>One entry of the account's game list (as reported by a library worker).</summary>
    public sealed class LibGame
    {
        public uint Id { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
    }

    /// <summary>On-disk shape of <c>library.json</c>. The list of owned games rarely changes, so it is cached until "Reload".</summary>
    public sealed class LibraryCache
    {
        public long SavedAt { get; set; }
        public string SteamId { get; set; }
        public string Language { get; set; }
        public bool Limited { get; set; }
        public List<LibGame> Games { get; set; } = new();
    }

    /// <summary>
    /// Builds the library the UI shows. Two sources are combined for every game:
    /// the LOCAL Steam cache (<see cref="LocalProgress"/>: instant, no side effects) and LIVE records (what a game worker
    /// reported, <see cref="ProgressStore"/>: exact but only available for games that were opened or scanned).
    /// </summary>
    public sealed class LibraryService
    {
        /// <summary>Only one library fetch (a worker process, slow) at a time.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Throws a clean API error unless Steam is installed, running and signed in; returns the active account.</summary>
        public static (SteamStatus status, uint accountId) RequireSteam()
        {
            var status = SteamEnv.GetStatus();
            if (status.Installed == false) throw new ApiException(409, "steam-missing", "Steam was not found on this computer.");
            if (status.Running == false) throw new ApiException(409, "steam-not-running", "Steam is not running.");
            if (status.LoggedIn == false) throw new ApiException(409, "steam-not-logged-in", "No account is currently signed in to Steam.");
            return (status, status.ActiveAccountId);
        }

        /// <summary>Chooses between the local-cache and the live record of one game.</summary>
        private static ProgressInfo Merge(ProgressInfo local, ProgressInfo live)
        {
            if (live == null) return local;
            if (local == null) return live;
            // The local Steam cache only wins if it has real user data and is newer than the live data.
            if (local.Unlocked != null && local.Ts > live.Ts) return local;
            return live;
        }

        /// <summary>
        /// A live record may be older than the ownership data derived from the game's achievement groups (DLC bought since,
        /// or recorded before groups were understood) - the group-based number wins, capped by what is still locked.
        /// </summary>
        private static ProgressInfo PreferGroupDlc(ProgressInfo merged, ProgressInfo local)
        {
            if (merged == null || local == null || local.Groups == false || ReferenceEquals(merged, local) || merged.Unlocked == null) return merged;
            int cap = Math.Max(0, merged.Total - merged.Unlocked.Value - (merged.ProtLocked ?? 0));
            int dlc = Math.Min(local.DlcLocked ?? 0, cap);
            if (dlc == (merged.DlcLocked ?? 0)) return merged;
            return new ProgressInfo
            {
                Total = merged.Total, Unlocked = merged.Unlocked, ProtTotal = merged.ProtTotal, ProtLocked = merged.ProtLocked,
                DlcLocked = dlc, Ts = merged.Ts, Src = merged.Src, Groups = true,
            };
        }

        /// <summary>
        /// The library for the active account: game list + progress + "new" markers.
        /// <paramref name="refresh"/> (the Reload button) re-asks Steam for the game list and the DLC ownership.
        /// </summary>
        public async Task<object> GetAsync(bool refresh)
        {
            var (_, accountId) = RequireSteam();
            var cachePath = Path.Combine(AppPaths.AccountDir(accountId), "library.json");

            var cache = refresh ? null : Json.ReadFile<LibraryCache>(cachePath);
            if (cache == null)
            {
                await _gate.WaitAsync();
                try
                {
                    cache = refresh ? null : Json.ReadFile<LibraryCache>(cachePath);
                    cache ??= await FetchAsync(accountId);
                }
                finally
                {
                    _gate.Release();
                }
            }

            // The account the worker was actually connected to is authoritative
            if (ulong.TryParse(cache.SteamId, out var id64)) accountId = SteamEnv.ToAccountId(id64);

            var ids = cache.Games.Select(g => g.Id).ToList();
            var ownership = await DlcOwnership.EnsureAsync(accountId, ids, refresh);
            var local = LocalProgress.ComputeAll(accountId, ids, DlcStore.Snapshot(accountId), ownership);
            var live = ProgressStore.Snapshot(accountId);
            var installed = SteamEnv.GetInstalledAppIds();
            var lastPlayed = SteamEnv.GetLastPlayed(accountId);
            var accounts = SteamEnv.GetAccounts(accountId);
            var account = accounts.FirstOrDefault(a => a.AccountId == accountId);

            var seen = SeenStore.Load(accountId);
            bool firstRun = seen.Initialized == false;
            bool seenDirty = false;

            var games = cache.Games.Select(g =>
            {
                local.TryGetValue(g.Id, out var l);
                live.TryGetValue(g.Id, out var v);
                var p = PreferGroupDlc(Merge(l, v), l);

                // "New" markers: a game that was not in the library when the user last looked, or a game
                // whose achievement count grew since then. The very first load only records the baseline.
                var key = g.Id.ToString(CultureInfo.InvariantCulture);
                bool isNew = false;
                int newAch = 0;
                if (seen.Known.TryGetValue(key, out var baseline) == false)
                {
                    if (firstRun) { seen.Known[key] = p?.Total; seenDirty = true; }
                    else isNew = true;
                }
                else if (p != null)
                {
                    if (baseline == null || p.Total < baseline) { seen.Known[key] = p.Total; seenDirty = true; }
                    else if (p.Total > baseline) newAch = p.Total - baseline.Value;
                }

                return new
                {
                    id = g.Id,
                    name = string.IsNullOrWhiteSpace(g.Name) ? "App " + g.Id.ToString(CultureInfo.InvariantCulture) : g.Name,
                    type = g.Type,
                    installed = installed.Contains(g.Id),
                    lastPlayed = lastPlayed.TryGetValue(g.Id, out var lp) ? (long?)lp : null,
                    known = p != null,
                    total = p?.Total,
                    unlocked = p?.Unlocked,
                    protTotal = p?.ProtTotal,
                    protLocked = p?.ProtLocked,
                    ts = p?.Ts,
                    src = p?.Src,
                    dlcLocked = p?.DlcLocked ?? 0,
                    isNew,
                    newAch,
                };
            }).ToList();

            if (firstRun) seen.Initialized = true;
            if (firstRun || seenDirty) SeenStore.Save(accountId, seen);

            return new
            {
                account = account == null ? null : new { steamId = account.SteamId, accountId = account.AccountId, name = account.AccountName, persona = account.PersonaName },
                language = cache.Language,
                savedAt = cache.SavedAt,
                limited = cache.Limited,
                games,
            };
        }

        /// <summary>Marks every game (and its current achievement count) as seen, clearing all "new" markers.</summary>
        public void AckAll()
        {
            var (_, accountId) = RequireSteam();
            var cache = Json.ReadFile<LibraryCache>(Path.Combine(AppPaths.AccountDir(accountId), "library.json"));
            if (cache == null) return;
            if (ulong.TryParse(cache.SteamId, out var id64)) accountId = SteamEnv.ToAccountId(id64);

            var ids = cache.Games.Select(g => g.Id).ToList();
            var local = LocalProgress.ComputeAll(accountId, ids, DlcStore.Snapshot(accountId));
            var live = ProgressStore.Snapshot(accountId);
            var seen = SeenStore.Load(accountId);
            foreach (var g in cache.Games)
            {
                local.TryGetValue(g.Id, out var l);
                live.TryGetValue(g.Id, out var v);
                seen.Known[g.Id.ToString(CultureInfo.InvariantCulture)] = Merge(l, v)?.Total;
            }
            seen.Initialized = true;
            SeenStore.Save(accountId, seen);
        }

        /// <summary>Asks Steam (through a short-lived library worker) which games the account owns, and caches the answer.</summary>
        private static async Task<LibraryCache> FetchAsync(uint accountId)
        {
            var (gamesPath, limited) = await GamesList.EnsureAsync(accountId);

            using var worker = WorkerProcess.Start("library");
            var data = await worker.SendAsync("library", new JsonObject { ["games"] = gamesPath }, 240000);

            var cache = new LibraryCache
            {
                SavedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                SteamId = data["steamId"]?.GetValue<string>(),
                Language = data["language"]?.GetValue<string>(),
                Limited = limited,
            };

            foreach (var item in data["games"]?.AsArray() ?? new JsonArray())
            {
                cache.Games.Add(new LibGame
                {
                    Id = item["id"].GetValue<uint>(),
                    Type = item["type"]?.GetValue<string>() ?? "normal",
                    Name = item["name"]?.GetValue<string>(),
                });
            }

            if (ulong.TryParse(cache.SteamId, out var id64)) accountId = SteamEnv.ToAccountId(id64);
            Json.WriteFile(Path.Combine(AppPaths.AccountDir(accountId), "library.json"), cache);
            return cache;
        }
    }

    // ====================================================================== DLC lookup

    /// <summary>
    /// Complete DLC ID list of a game from the public Steam store (only the app ID is sent). Steam's own client list is capped and
    /// only contains DLCs the account already has access to, so a DLC the account is missing would never show up there.
    /// Results are cached on disk. Only requested for games whose achievement texts mention a DLC.
    /// </summary>
    public static class DlcIds
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        private sealed class Entry
        {
            public long Ts { get; set; }
            public bool Failed { get; set; }
            public List<uint> Ids { get; set; } = new();
        }

        static DlcIds()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamAchievementManagerModern/8.0");
        }

        private static string PathFor(uint appId) => Path.Combine(AppPaths.Sub("dlcids"), appId.ToString(CultureInfo.InvariantCulture) + ".json");

        /// <summary>Cached IDs (possibly empty), or null if they have to be looked up.</summary>
        public static List<uint> TryGetCached(uint appId)
        {
            var e = Json.ReadFile<Entry>(PathFor(appId));
            if (e == null) return null;
            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - e.Ts;
            if (age > (e.Failed ? 3600 : 14 * 86400)) return null;   // failed lookups are retried after an hour
            return e.Ids;
        }

        /// <summary>Looks the DLC list up in the store API and caches it (a failure is cached too, but only for an hour).</summary>
        public static async Task<List<uint>> FetchAsync(uint appId)
        {
            var entry = new Entry { Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            try
            {
                var url = string.Create(CultureInfo.InvariantCulture, $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic&l=english");
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var root = doc.RootElement.GetProperty(appId.ToString(CultureInfo.InvariantCulture));
                if (root.GetProperty("success").GetBoolean() && root.GetProperty("data").TryGetProperty("dlc", out var dlc))
                {
                    entry.Ids = dlc.EnumerateArray().Select(x => x.GetUInt32()).ToList();
                }
            }
            catch (Exception)
            {
                entry.Failed = true;
            }

            Json.WriteFile(PathFor(appId), entry);
            return entry.Ids;
        }
    }

    // ====================================================================== DLC ownership (whole library)

    /// <summary>Which DLC app IDs were looked up for an account and which of them it owns.</summary>
    public sealed class DlcOwnershipSnapshot
    {
        public HashSet<uint> Checked { get; set; } = new();
        public HashSet<uint> Owned { get; set; } = new();
    }

    /// <summary>On-disk shape of <c>dlcowned.json</c>.</summary>
    public sealed class DlcOwnershipFile
    {
        public long Ts { get; set; }
        public List<uint> Checked { get; set; } = new();
        public List<uint> Owned { get; set; } = new();
    }

    /// <summary>
    /// Lets the library show "needs a missing DLC" for every game at once: the games' achievement groups name their DLC
    /// (local schema files), and the Steam client says which of those DLCs the account owns (one quick lookup, no game is started).
    /// </summary>
    public static class DlcOwnership
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static long _failedAt;

        private static string PathFor(uint accountId) => Path.Combine(AppPaths.AccountDir(accountId), "dlcowned.json");

        private static DlcOwnershipSnapshot ToSnapshot(DlcOwnershipFile f) =>
            new() { Checked = new HashSet<uint>(f?.Checked ?? new()), Owned = new HashSet<uint>(f?.Owned ?? new()) };

        /// <summary>Returns the ownership of every DLC the given games refer to, asking Steam only for what is unknown, stale (24 h) or forced.</summary>
        public static async Task<DlcOwnershipSnapshot> EnsureAsync(uint accountId, IEnumerable<uint> gameIds, bool force)
        {
            var needed = new HashSet<uint>();
            foreach (var id in gameIds)
            {
                foreach (var dlc in StatsSchema.GroupDlcIds(id)) needed.Add(dlc);
            }

            var file = Json.ReadFile<DlcOwnershipFile>(PathFor(accountId));
            var snapshot = ToSnapshot(file);
            if (needed.Count == 0) return snapshot;

            bool stale = file == null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - file.Ts > 24 * 3600;
            var todo = (force || stale ? needed : needed.Where(d => snapshot.Checked.Contains(d) == false).ToHashSet()).ToList();
            if (todo.Count == 0) return snapshot;
            if (Environment.TickCount64 - _failedAt < 120_000 && _failedAt != 0) return snapshot;   // do not hammer Steam after a failure

            await Gate.WaitAsync();
            try
            {
                using var worker = WorkerProcess.Start("library");
                var array = new JsonArray();
                foreach (var d in todo) array.Add(d);
                var data = await worker.SendAsync("owned", new JsonObject { ["ids"] = array }, 60000);

                // The worker's own account is authoritative (the user may have switched accounts meanwhile)
                if (ulong.TryParse(data["steamId"]?.GetValue<string>(), out var id64) && SteamEnv.ToAccountId(id64) != accountId) return snapshot;

                var owned = (data["owned"] as JsonArray)?.Select(n => n.GetValue<uint>()).ToHashSet() ?? new HashSet<uint>();
                var merged = ToSnapshot(file);
                if (force || stale) { merged.Checked.Clear(); merged.Owned.Clear(); }
                foreach (var d in todo) merged.Checked.Add(d);
                foreach (var d in todo) { if (owned.Contains(d)) merged.Owned.Add(d); else merged.Owned.Remove(d); }

                Json.WriteFile(PathFor(accountId), new DlcOwnershipFile
                {
                    Ts = force || stale ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : file?.Ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Checked = merged.Checked.ToList(),
                    Owned = merged.Owned.ToList(),
                });
                _failedAt = 0;
                return merged;
            }
            catch (Exception)
            {
                _failedAt = Environment.TickCount64;   // a missing DLC badge is a bonus - never break the library over it
                return snapshot;
            }
            finally
            {
                Gate.Release();
            }
        }
    }

    /// <summary>The one way every part of the host loads a game, so DLC information is the same everywhere.</summary>
    public static class GameLoader
    {
        /// <summary>Adds the store DLC id list (if known) to the payload of a worker command.</summary>
        private static JsonObject WithDlc(JsonObject payload, List<uint> ids)
        {
            payload ??= new JsonObject();
            if (ids != null && ids.Count > 0)
            {
                var array = new JsonArray();
                foreach (var id in ids) array.Add(id);
                payload["dlcIds"] = array;
            }
            return payload;
        }

        /// <summary>Adds the cached DLC IDs to a command (apply/reset also re-read the game and must see the same DLCs).</summary>
        public static JsonObject WithCachedDlc(JsonObject payload, uint appId) => WithDlc(payload, DlcIds.TryGetCached(appId));

        /// <summary>
        /// Loads a game. If its achievement texts mention a DLC and the DLC IDs are not known yet, they are looked up once
        /// and the game is loaded again with them.
        /// </summary>
        public static async Task<JsonNode> LoadAsync(Func<JsonObject, Task<JsonNode>> send, uint appId)
        {
            var cached = DlcIds.TryGetCached(appId);
            var data = await send(WithDlc(null, cached));
            if (cached == null && (data?["dlcInfo"]?["gated"]?.GetValue<int>() ?? 0) > 0)
            {
                var ids = await DlcIds.FetchAsync(appId);
                if (ids.Count > 0) data = await send(WithDlc(null, ids));
            }
            return data;
        }
    }

    // ====================================================================== Live data recorder

    /// <summary>Stores what a live "load" from a game worker revealed (progress and DLC-blocked achievements).</summary>
    public static class ProgressRecorder
    {
        /// <summary>Saves the summary and the list of DLC-blocked achievement ids of a worker's load/apply answer. Returns the stored progress.</summary>
        public static ProgressInfo Record(JsonNode data)
        {
            var summary = data?["summary"];
            if (summary == null || ulong.TryParse(data["steamId"]?.GetValue<string>(), out var id64) == false) return null;

            var accountId = SteamEnv.ToAccountId(id64);
            var appId = data["appId"].GetValue<uint>();
            var info = new ProgressInfo
            {
                Total = summary["total"].GetValue<int>(),
                Unlocked = summary["unlocked"].GetValue<int>(),
                ProtTotal = summary["protTotal"].GetValue<int>(),
                ProtLocked = summary["protLocked"].GetValue<int>(),
                DlcLocked = summary["dlcLocked"]?.GetValue<int>() ?? 0,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Src = "live",
            };
            ProgressStore.Set(accountId, appId, info);

            var gated = new List<string>();
            if (data["achievements"] is JsonArray list)
            {
                foreach (var a in list)
                {
                    bool unowned = a?["dlc"]?["owned"]?.GetValue<bool>() == false;
                    bool isProtected = a?["protected"]?.GetValue<bool>() == true;
                    var id = a?["id"]?.GetValue<string>();
                    if (unowned && isProtected == false && id != null) gated.Add(id);
                }
            }
            DlcStore.Set(accountId, appId, gated);
            return info;
        }
    }

    // ====================================================================== Game session

    /// <summary>
    /// Keeps at most one worker open: the one for the game that is currently open in the UI. While it is open, Steam
    /// shows the user as "playing" that game - which is why it is closed again as soon as the user leaves the game view.
    /// Calls are serialised, because a worker handles one command at a time.
    /// </summary>
    public sealed class GameSessions
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private WorkerProcess _worker;

        /// <summary>Sends a command to the worker of <paramref name="appId"/> (starting it, or replacing a worker of another game).</summary>
        private async Task<JsonNode> CallAsync(uint appId, string cmd, JsonObject payload, int timeoutMs)
        {
            await _gate.WaitAsync();
            try
            {
                LibraryService.RequireSteam();

                if (_worker == null || _worker.AppId != appId)
                {
                    _worker?.Dispose();
                    _worker = WorkerProcess.Start("game", appId);
                }

                try
                {
                    return await _worker.SendAsync(cmd, payload, timeoutMs);
                }
                catch (WorkerException ex) when (ex.Code is "exited" or "timeout" or "Load" or "CreateSteamPipe" or "ConnectToGlobalUser" or "AppIdMismatch" or "GetInstallPath" or "CreateSteamClient" or "Unknown")
                {
                    // The worker is broken (crashed, hung, lost Steam): drop it so the next call starts a fresh one.
                    _worker?.Dispose();
                    _worker = null;
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Every load/apply refreshes the stored progress of that game; a failure here must never fail the request.</summary>
        private static void RememberProgress(JsonNode data)
        {
            try
            {
                ProgressRecorder.Record(data);
            }
            catch (Exception)
            {
            }
        }

        public async Task<JsonNode> LoadAsync(uint appId)
        {
            var data = await GameLoader.LoadAsync(payload => CallAsync(appId, "load", payload, 90000), appId);
            RememberProgress(data);
            return data;
        }

        public async Task<JsonNode> ApplyAsync(uint appId, JsonObject changes)
        {
            var result = await CallAsync(appId, "apply", GameLoader.WithCachedDlc(changes, appId), 90000);
            RememberProgress(result?["data"]);
            return result;
        }

        /// <summary>Asks the open game's worker for the achievements of the given friends (the in-game "Friends" tab).</summary>
        public Task<JsonNode> FriendsAsync(uint appId, IEnumerable<string> steamIds)
        {
            var array = new JsonArray();
            foreach (var id in steamIds) array.Add(id);
            return CallAsync(appId, "friends", new JsonObject { ["steamIds"] = array }, 120000);
        }

        public async Task<JsonNode> ResetAsync(uint appId, bool withAchievements)
        {
            var result = await CallAsync(appId, "reset", GameLoader.WithCachedDlc(new JsonObject { ["achievements"] = withAchievements }, appId), 90000);
            RememberProgress(result?["data"]);
            return result;
        }

        /// <summary>Closes the open worker (of that game, or of whichever game if <paramref name="appId"/> is null) so Steam stops showing "playing".</summary>
        public async Task CloseAsync(uint? appId = null)
        {
            await _gate.WaitAsync();
            try
            {
                if (_worker != null && (appId == null || _worker.AppId == appId))
                {
                    _worker.Dispose();
                    _worker = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    // ====================================================================== Friends

    /// <summary>The friends list of the Steam client, and the guard that keeps every friends feature opt-in.</summary>
    public sealed class FriendService
    {
        private readonly object _lock = new();
        private uint _cachedFor;
        private long _cachedAt;
        private JsonNode _cached;

        /// <summary>Friends list from the Steam client (names and IDs only, no game data). Briefly cached.</summary>
        public async Task<JsonNode> GetListAsync(uint accountId)
        {
            lock (_lock)
            {
                if (_cached != null && _cachedFor == accountId && Environment.TickCount64 - _cachedAt < 60_000) return _cached;
            }

            using var worker = WorkerProcess.Start("library");
            var data = await worker.SendAsync("friends-list", null, 30000);

            lock (_lock)
            {
                _cached = data;
                _cachedFor = accountId;
                _cachedAt = Environment.TickCount64;
            }
            return data;
        }

        /// <summary>Throws unless the user has switched the friends comparison on. Called before ANY friend data is requested.</summary>
        public static FriendsSettings RequireEnabled(uint accountId)
        {
            var settings = FriendsStore.Get(accountId);
            if (settings.Enabled == false)
            {
                throw new ApiException(403, "friends-disabled", "The friends comparison is not enabled.");
            }
            return settings;
        }
    }

    // ====================================================================== Deep scan

    /// <summary>
    /// Queries the progress of games through the Steam API (one short worker per game). This is more accurate than the
    /// local cache, but makes Steam show "playing" for 1-2 seconds per game - therefore only on request.
    /// </summary>
    public sealed class ScanService
    {
        private readonly object _lock = new();
        private CancellationTokenSource _cts;
        private bool _running;
        private int _total, _done, _failed;
        private uint _current;
        private string _error;
        private readonly List<object> _results = new();

        /// <summary>Starts scanning the given games in the background (throws if a scan is already running).</summary>
        public void Start(uint accountId, IReadOnlyList<uint> ids)
        {
            lock (_lock)
            {
                if (_running) throw new ApiException(409, "scan-running", "A scan is already running.");
                _running = true;
                _total = ids.Count;
                _done = _failed = 0;
                _current = 0;
                _error = null;
                _results.Clear();
                _cts = new CancellationTokenSource();
            }

            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(accountId, ids, token));
        }

        public void Cancel()
        {
            lock (_lock) { _cts?.Cancel(); }
        }

        public bool IsRunning
        {
            get { lock (_lock) { return _running; } }
        }

        /// <summary>Progress counters plus the results added since <paramref name="cursor"/>; the UI passes back the returned cursor next time.</summary>
        public object Status(int cursor)
        {
            lock (_lock)
            {
                cursor = Math.Clamp(cursor, 0, _results.Count);
                return new
                {
                    running = _running,
                    total = _total,
                    done = _done,
                    failed = _failed,
                    current = _current,
                    error = _error,
                    cursor = _results.Count,
                    results = _results.Skip(cursor).ToList(),
                };
            }
        }

        /// <summary>The background loop: one short-lived game worker per game, one result recorded per game.</summary>
        private async Task RunAsync(uint accountId, IReadOnlyList<uint> ids, CancellationToken token)
        {
            try
            {
                foreach (var id in ids)
                {
                    if (token.IsCancellationRequested) break;

                    lock (_lock) { _current = id; }
                    ProgressInfo info = null;
                    bool failed = false;

                    try
                    {
                        using var worker = WorkerProcess.Start("game", id);
                        var data = await GameLoader.LoadAsync(payload => worker.SendAsync("load", payload, 90000), id);
                        info = ProgressRecorder.Record(data) ?? throw new InvalidDataException("Unexpected response from the Steam worker");
                    }
                    catch (WorkerException ex) when (ex.Code is "no-schema" or "steam-error")
                    {
                        // game has no achievements/statistics
                        info = new ProgressInfo { Total = 0, Unlocked = 0, ProtTotal = 0, ProtLocked = 0 };
                    }
                    catch (WorkerException ex) when (ex.Code is "Load" or "CreateSteamPipe" or "ConnectToGlobalUser" or "CreateSteamClient" or "GetInstallPath")
                    {
                        lock (_lock) { _error = "Lost connection to Steam: " + ex.Message; }
                        break;
                    }
                    catch (Exception)
                    {
                        failed = true;
                    }

                    lock (_lock)
                    {
                        _done++;
                        if (failed) _failed++;
                    }

                    if (info != null)
                    {
                        info.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        info.Src = "live";
                        ProgressStore.Set(accountId, id, info);
                        lock (_lock)
                        {
                            _results.Add(new { id, total = info.Total, unlocked = info.Unlocked, protTotal = info.ProtTotal, protLocked = info.ProtLocked, dlcLocked = info.DlcLocked ?? 0, ts = info.Ts, src = info.Src });
                        }
                    }

                    await Task.Delay(120, CancellationToken.None);
                }
            }
            finally
            {
                lock (_lock) { _running = false; _current = 0; }
            }
        }
    }

    // ====================================================================== Friends' progress (batch)

    /// <summary>
    /// "Load friends' progress": asks Steam, game after game, how far the SELECTED friends are (counts only), so the library can show
    /// one bar per friend on every card. Needs one short worker per game (Steam gives out friends' achievements only inside a game
    /// context), so - like the deep scan - it only ever runs on request.
    /// </summary>
    public sealed class FriendScanService
    {
        private const int MaxAgeSeconds = 6 * 3600;   // games loaded more recently are skipped (unless forced)
        private readonly object _lock = new();
        private CancellationTokenSource _cts;
        private bool _running;
        private int _total, _done, _failed, _skipped;
        private uint _current;
        private string _error;
        private readonly List<object> _results = new();

        public bool IsRunning
        {
            get { lock (_lock) { return _running; } }
        }

        public void Cancel()
        {
            lock (_lock) { _cts?.Cancel(); }
        }

        /// <summary>
        /// Starts the scan. Games whose result is younger than <see cref="MaxAgeSeconds"/> are skipped unless
        /// <paramref name="force"/> is set. Does nothing (and reports "not running") if everything is up to date.
        /// </summary>
        public void Start(uint accountId, IReadOnlyList<uint> ids, bool force)
        {
            var settings = FriendService.RequireEnabled(accountId);
            if (settings.Selected.Count == 0) throw new ApiException(400, "no-friends", "No friends are selected.");

            var selected = settings.Selected.ToList();
            var todo = ids.Where(id => force || FriendProgressStore.IsFresh(accountId, id, selected, MaxAgeSeconds) == false).ToList();

            lock (_lock)
            {
                if (_running) throw new ApiException(409, "friend-scan-running", "Loading the friends' progress is already running.");
                _total = todo.Count;
                _skipped = ids.Count - todo.Count;
                _done = _failed = 0;
                _current = 0;
                _error = null;
                _results.Clear();
                if (todo.Count == 0) return;   // everything is up to date
                _running = true;
                _cts = new CancellationTokenSource();
            }

            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(accountId, todo, token));
        }

        public object Status(int cursor)
        {
            lock (_lock)
            {
                cursor = Math.Clamp(cursor, 0, _results.Count);
                return new
                {
                    running = _running,
                    total = _total,
                    done = _done,
                    failed = _failed,
                    skipped = _skipped,
                    current = _current,
                    error = _error,
                    cursor = _results.Count,
                    results = _results.Skip(cursor).ToList(),
                };
            }
        }

        private async Task RunAsync(uint accountId, IReadOnlyList<uint> ids, CancellationToken token)
        {
            try
            {
                foreach (var id in ids)
                {
                    if (token.IsCancellationRequested) break;

                    // The selection may have changed meanwhile - the current one always wins, and "deactivated" stops everything.
                    var settings = FriendsStore.Get(accountId);
                    if (settings.Enabled == false || settings.Selected.Count == 0) break;

                    lock (_lock) { _current = id; }
                    var perFriend = new Dictionary<string, FriendGameProgress>();
                    bool failed = false;

                    try
                    {
                        var array = new JsonArray();
                        foreach (var friend in settings.Selected) array.Add(friend);

                        // timeoutMs: how long the worker waits for Steam per friend. A short wait keeps a scan over hundreds
                        // of games moving; a friend Steam does not answer for within 8 s is stored as "timeout".
                        using var worker = WorkerProcess.Start("game", id);
                        var res = await worker.SendAsync("friends", new JsonObject { ["steamIds"] = array, ["timeoutMs"] = 8000 }, 180000);

                        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        foreach (var r in res["results"] as JsonArray ?? new JsonArray())
                        {
                            var friend = r?["steamId"]?.GetValue<string>();
                            if (friend == null) continue;
                            bool ok = r["ok"]?.GetValue<bool>() == true;
                            perFriend[friend] = new FriendGameProgress
                            {
                                U = ok ? r["unlocked"]?.GetValue<int>() ?? 0 : 0,
                                T = ok ? r["total"]?.GetValue<int>() ?? 0 : 0,
                                S = ok ? "ok" : r["reason"]?.GetValue<string>() ?? "request",
                                Ts = now,
                            };
                        }
                    }
                    catch (WorkerException ex) when (ex.Code is "no-schema" or "steam-error")
                    {
                        // The game has no achievements: nothing to compare.
                    }
                    catch (WorkerException ex) when (ex.Code is "Load" or "CreateSteamPipe" or "ConnectToGlobalUser" or "CreateSteamClient" or "GetInstallPath")
                    {
                        lock (_lock)
                        {
                            _error = "Could not connect to Steam for this game: " + ex.Message
                                + ". A running game or another Achievement Manager window may be holding Steam's game slot.";
                        }
                        break;
                    }
                    catch (Exception)
                    {
                        failed = true;
                    }

                    if (perFriend.Count > 0)
                    {
                        FriendProgressStore.Set(accountId, id, perFriend, FriendsStore.Get(accountId).Selected);
                    }

                    lock (_lock)
                    {
                        _done++;
                        if (failed) _failed++;
                        if (perFriend.Count > 0)
                        {
                            _results.Add(new { id, friends = perFriend });
                        }
                    }

                    await Task.Delay(120, CancellationToken.None);
                }
            }
            finally
            {
                lock (_lock) { _running = false; _current = 0; }
            }
        }
    }

    // ====================================================================== Auto-unlock (batch)

    /// <summary>What Auto-unlock did in one game (also the record "Undo" works from).</summary>
    public sealed class AutoGameResult
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public int Changed { get; set; }
        public int Failed { get; set; }
        public int SkippedProtected { get; set; }
        public int SkippedDlc { get; set; }
        public bool Stored { get; set; }
        public string Error { get; set; }

        /// <summary>Why changes failed, in words (for example "1 needs the DLC Black Ops 7").</summary>
        public string FailWhy { get; set; }

        /// <summary>The achievements that were actually changed - needed to undo a run.</summary>
        public List<string> Ids { get; set; } = new();
    }

    /// <summary>On-disk shape of <c>autorun.json</c>: the most recent run that changed something, so it can be undone.</summary>
    public sealed class AutoLastRun
    {
        public string Mode { get; set; }
        public long Ts { get; set; }
        public List<AutoGameResult> Games { get; set; } = new();
    }

    /// <summary>
    /// Works through a list of games one after another: loads the game, unlocks (or locks) every achievement SAM is
    /// allowed to change, stores, and moves on. Achievements that are set by the game itself ("protected") and
    /// achievements that need a DLC the account does not own are never touched.
    /// </summary>
    public sealed class AutoService
    {
        private readonly object _lock = new();
        private CancellationTokenSource _cts;
        private bool _running;
        private string _mode = "unlock";
        private int _total, _done, _changed, _problems;
        private uint _current;
        private string _currentName, _error;
        private readonly List<AutoGameResult> _results = new();

        private static string LastPath(uint accountId) => Path.Combine(AppPaths.AccountDir(accountId), "autorun.json");

        public bool IsRunning
        {
            get { lock (_lock) { return _running; } }
        }

        /// <summary>True if the last run changed something that can still be reversed.</summary>
        public bool HasUndo(uint accountId)
        {
            var last = Json.ReadFile<AutoLastRun>(LastPath(accountId));
            return last != null && last.Games.Any(g => g.Ids.Count > 0);
        }

        /// <param name="mode">"unlock" or "lock".</param>
        /// <param name="only">If given, only these achievement ids are touched per game (used by Undo so it reverses exactly the previous run).</param>
        public void Start(uint accountId, string mode, IReadOnlyList<uint> ids, Dictionary<uint, HashSet<string>> only = null)
        {
            lock (_lock)
            {
                if (_running) throw new ApiException(409, "auto-running", "An auto-unlock run is already in progress.");
                _running = true;
                _mode = mode;
                _total = ids.Count;
                _done = _changed = _problems = 0;
                _current = 0;
                _currentName = null;
                _error = null;
                _results.Clear();
                _cts = new CancellationTokenSource();
            }

            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(accountId, mode, ids, only, token));
        }

        /// <summary>Reverses the previous run: locks again exactly what it unlocked (or the other way round).</summary>
        public void StartUndo(uint accountId)
        {
            var last = Json.ReadFile<AutoLastRun>(LastPath(accountId));
            var games = last?.Games.Where(g => g.Ids.Count > 0).ToList();
            if (games == null || games.Count == 0)
            {
                throw new ApiException(404, "nothing-to-undo", "There is nothing to undo.");
            }

            var mode = last.Mode == "unlock" ? "lock" : "unlock";
            Start(accountId, mode, games.Select(g => g.Id).ToList(), games.ToDictionary(g => g.Id, g => new HashSet<string>(g.Ids)));
        }

        public void Cancel()
        {
            lock (_lock) { _cts?.Cancel(); }
        }

        public object Status(uint accountId, int cursor)
        {
            lock (_lock)
            {
                cursor = Math.Clamp(cursor, 0, _results.Count);
                return new
                {
                    running = _running,
                    mode = _mode,
                    total = _total,
                    done = _done,
                    changed = _changed,
                    problems = _problems,
                    current = _current,
                    currentName = _currentName,
                    error = _error,
                    cursor = _results.Count,
                    canUndo = _running == false && HasUndo(accountId),
                    results = _results.Skip(cursor).Select(r => new
                    {
                        id = r.Id,
                        name = r.Name,
                        changed = r.Changed,
                        failed = r.Failed,
                        failWhy = r.FailWhy,
                        skippedProtected = r.SkippedProtected,
                        skippedDlc = r.SkippedDlc,
                        stored = r.Stored,
                        error = r.Error,
                    }).ToList(),
                };
            }
        }

        /// <summary>Summarises the worker's failure list: "2 need the DLC X · 1 refused by Steam".</summary>
        private static string DescribeFailures(JsonArray failed)
        {
            var parts = new List<string>();
            foreach (var group in failed.GroupBy(f => f?["reason"]?.GetValue<string>() ?? "steam"))
            {
                int n = group.Count();
                parts.Add(group.Key switch
                {
                    "dlc" => $"{n} need the DLC {string.Join(", ", group.Select(f => f?["detail"]?.GetValue<string>()).Where(d => string.IsNullOrEmpty(d) == false).Distinct().Take(3))}",
                    "protected" => $"{n} controlled by the game",
                    "unknown" => $"{n} unknown to Steam (outdated game data)",
                    _ => $"{n} refused by Steam without a reason",
                });
            }
            return string.Join(" · ", parts);
        }

        /// <summary>
        /// For every game: load it, pick the achievements to change, apply them in one go, record what really changed.
        /// Selection rules - unlock: skip already unlocked, protected (game-controlled) and DLC-blocked ones; lock: only
        /// unlocked, non-protected ones. The ids that Steam accepted are kept in the last-run file, which is written even
        /// when the run is cancelled or aborted (otherwise a half-finished run could not be undone).
        /// </summary>
        private async Task RunAsync(uint accountId, string mode, IReadOnlyList<uint> ids, Dictionary<uint, HashSet<string>> only, CancellationToken token)
        {
            try
            {
                foreach (var id in ids)
                {
                    if (token.IsCancellationRequested) break;

                    lock (_lock) { _current = id; _currentName = null; }
                    var result = new AutoGameResult { Id = id };
                    bool abort = false;

                    try
                    {
                        using var worker = WorkerProcess.Start("game", id);
                        var data = await GameLoader.LoadAsync(payload => worker.SendAsync("load", payload, 90000), id);
                        result.Name = data["name"]?.GetValue<string>();
                        lock (_lock) { _currentName = result.Name; }
                        ProgressRecorder.Record(data);

                        var wanted = new JsonArray();
                        var wantedIds = new List<string>();
                        foreach (var a in data["achievements"] as JsonArray ?? new JsonArray())
                        {
                            var achId = a?["id"]?.GetValue<string>();
                            if (achId == null) continue;

                            bool achieved = a["achieved"]?.GetValue<bool>() == true;
                            bool isProtected = a["protected"]?.GetValue<bool>() == true;
                            bool dlcBlocked = a["dlc"]?["owned"]?.GetValue<bool>() == false;

                            if (only != null && (only.TryGetValue(id, out var allowed) == false || allowed.Contains(achId) == false)) continue;

                            if (mode == "unlock")
                            {
                                if (achieved) continue;
                                if (isProtected) { result.SkippedProtected++; continue; }
                                if (dlcBlocked) { result.SkippedDlc++; continue; }
                            }
                            else
                            {
                                if (achieved == false) continue;
                                if (isProtected) { result.SkippedProtected++; continue; }
                            }

                            wanted.Add(achId);
                            wantedIds.Add(achId);
                        }

                        if (wantedIds.Count > 0)
                        {
                            var payload = new JsonObject
                            {
                                ["unlock"] = mode == "unlock" ? wanted : new JsonArray(),
                                ["lock"] = mode == "lock" ? wanted : new JsonArray(),
                                ["stats"] = new JsonArray(),
                            };

                            var res = await worker.SendAsync("apply", GameLoader.WithCachedDlc(payload, id), 120000);
                            var failedItems = (res["failed"] as JsonArray) ?? new JsonArray();
                            var failed = failedItems.Select(f => f?["id"]?.GetValue<string>()).Where(x => x != null).ToHashSet();
                            result.Failed = failed.Count;
                            result.FailWhy = DescribeFailures(failedItems);
                            result.Stored = res["stored"]?.GetValue<bool>() == true;
                            result.Ids = wantedIds.Where(i => failed.Contains(i) == false).ToList();
                            result.Changed = result.Ids.Count;
                            ProgressRecorder.Record(res["data"]);
                        }
                        else
                        {
                            result.Stored = true;
                        }
                    }
                    catch (WorkerException ex) when (ex.Code is "no-schema" or "steam-error")
                    {
                        result.Error = "No achievements";
                    }
                    catch (WorkerException ex) when (ex.Code is "Load" or "CreateSteamPipe" or "ConnectToGlobalUser" or "CreateSteamClient" or "GetInstallPath")
                    {
                        lock (_lock)
                        {
                            _error = "Could not connect to Steam for this game: " + ex.Message
                                + ". A running game or another Achievement Manager window may be holding Steam's game slot.";
                        }
                        abort = true;
                    }
                    catch (Exception ex)
                    {
                        result.Error = ex.Message;
                    }

                    if (abort) break;

                    lock (_lock)
                    {
                        _done++;
                        _changed += result.Changed;
                        if ((result.Error != null && result.Error != "No achievements") || result.Failed > 0) _problems++;
                        _results.Add(result);
                    }

                    await Task.Delay(200, CancellationToken.None);
                }
            }
            finally
            {
                try
                {
                    List<AutoGameResult> changed;
                    lock (_lock) { changed = _results.Where(r => r.Ids.Count > 0).ToList(); }
                    if (changed.Count > 0)
                    {
                        Json.WriteFile(LastPath(accountId), new AutoLastRun { Mode = mode, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Games = changed });
                    }
                }
                catch (Exception)
                {
                }

                lock (_lock) { _running = false; _current = 0; }
            }
        }
    }

    // ====================================================================== Rarity

    /// <summary>Global unlock rates from the public Steam Web API (no API key required).</summary>
    public sealed class RarityService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private readonly Dictionary<uint, Dictionary<string, double>> _memory = new();

        public RarityService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamAchievementManagerModern/8.0");
        }

        /// <summary>Achievement id -> percent of all players who unlocked it. Cached in memory and for 7 days on disk; empty if unavailable.</summary>
        public async Task<Dictionary<string, double>> GetAsync(uint appId)
        {
            lock (_memory)
            {
                if (_memory.TryGetValue(appId, out var cached)) return cached;
            }

            var file = Path.Combine(AppPaths.Sub("rarity"), appId.ToString(CultureInfo.InvariantCulture) + ".json");
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7))
            {
                var fromDisk = Json.ReadFile<Dictionary<string, double>>(file);
                if (fromDisk != null)
                {
                    lock (_memory) { _memory[appId] = fromDisk; }
                    return fromDisk;
                }
            }

            var result = new Dictionary<string, double>();
            try
            {
                var url = string.Create(CultureInfo.InvariantCulture,
                    $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v0002/?gameid={appId}&format=json");
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var list = doc.RootElement.GetProperty("achievementpercentages").GetProperty("achievements");
                foreach (var item in list.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString();
                    var p = item.GetProperty("percent");
                    double value = p.ValueKind == JsonValueKind.Number
                        ? p.GetDouble()
                        : double.Parse(p.GetString() ?? "0", CultureInfo.InvariantCulture);
                    if (name != null) result[name] = Math.Round(value, 1);
                }

                Json.WriteFile(file, result);
            }
            catch (Exception)
            {
                // Not every game has public data - then there is simply no rarity.
            }

            lock (_memory) { _memory[appId] = result; }
            return result;
        }
    }

    // ====================================================================== Images

    /// <summary>Cover images for the library. Looks in Steam's own local cache first, then our cache, then the public CDN.</summary>
    public sealed class ImageService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private readonly HashSet<string> _failed = new();

        public ImageService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamAchievementManagerModern/8.0");
        }

        private static string FileFor(string kind) => kind switch
        {
            "capsule" => "library_600x900.jpg",
            "hero" => "library_hero.jpg",
            "logo" => "logo.png",
            _ => "header.jpg",
        };

        public async Task<(string path, string contentType)?> GetAsync(uint appId, string kind)
        {
            var file = FileFor(kind);
            var contentType = file.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg";

            // 1) Steam's own library cache - already local, costs nothing
            var local = Path.Combine(SteamEnv.LibraryCacheDir, appId.ToString(CultureInfo.InvariantCulture), file);
            if (File.Exists(local)) return (local, contentType);

            // 2) our own image cache
            var cacheDir = AppPaths.Sub("imgcache");
            var cached = Path.Combine(cacheDir, string.Create(CultureInfo.InvariantCulture, $"{appId}_{file}"));
            if (File.Exists(cached)) return (cached, contentType);

            var key = appId.ToString(CultureInfo.InvariantCulture) + "/" + file;
            lock (_failed) { if (_failed.Contains(key)) return null; }

            // 3) public Steam CDN
            try
            {
                var url = string.Create(CultureInfo.InvariantCulture, $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{file}");
                using var response = await Http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    if (bytes.Length > 0)
                    {
                        await File.WriteAllBytesAsync(cached, bytes);
                        return (cached, contentType);
                    }
                }
            }
            catch (Exception)
            {
            }

            lock (_failed) { _failed.Add(key); }
            return null;
        }
    }
}
