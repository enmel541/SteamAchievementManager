using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace SamModern.Core
{
    // ==========================================================================================================
    // Everything this app persists lives under %LOCALAPPDATA%\SteamAchievementManagerModern:
    //
    //   prefs.json                          UI preferences, saved views (one file for all accounts)
    //   games.xml                           the list of all Steam apps (downloaded, cached for 3 days)
    //   backups\                            copies made before an account switch
    //   accounts\<accountId>\
    //     library.json                      the games of the account (from a library worker)
    //     progress.json                     progress determined live (game sessions, deep scan)
    //     dlc.json / dlcowned.json          DLC-blocked achievements / which DLCs the account owns
    //     seen.json                         what the user has already seen (for the "New" badges)
    //     friends.json                      friends comparison settings (selected Steam IDs only)
    //     friendprogress.json               friends' progress COUNTS per game (never names or dates)
    //     autorun.json                      history of "Auto-unlock" runs, needed for "Undo"
    //
    // Every store below is a small static class: an in-memory copy guarded by a lock, written back as JSON with an
    // atomic temp-file-and-rename. Files are plain JSON on purpose, so a user can read, edit or delete them.
    // ==========================================================================================================

    /// <summary>An error that is meant to reach the UI as a clean JSON error (HTTP status + machine-readable code + message).</summary>
    public sealed class ApiException : Exception
    {
        public int Status { get; }
        public string Code { get; }
        public ApiException(int status, string code, string message) : base(message) { Status = status; Code = code; }
    }

    /// <summary>Locations of the app's data folders. Directories are created on first use.</summary>
    public static class AppPaths
    {
        public static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamAchievementManagerModern");

        /// <summary>Per-account folder: every Steam account has its own library, progress, friends settings, etc.</summary>
        public static string AccountDir(uint accountId)
        {
            var dir = Path.Combine(Root, "accounts", accountId.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string Sub(string name)
        {
            var dir = Path.Combine(Root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// JSON helpers shared by the stores and the API. Property names are camelCase (the web UI's convention).
    /// Reading and writing never throw: a missing or corrupt cache file simply means "start empty", and a failed write
    /// loses nothing but a cache.
    /// </summary>
    public static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };

        /// <summary>Reads a JSON file, or returns null if it does not exist or cannot be parsed.</summary>
        public static T ReadFile<T>(string path) where T : class
        {
            try
            {
                if (File.Exists(path) == false) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Writes atomically (temp file, then rename) so a crash can never leave a half-written file behind.</summary>
        public static void WriteFile(string path, object value)
        {
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
                File.Move(tmp, path, true);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// Progress values determined live (from game sessions and scans), stored per account. They are newer and more exact
    /// than what the local Steam cache says, so the library prefers them (see <c>LibraryService</c>).
    /// </summary>
    public static class ProgressStore
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<uint, Dictionary<string, ProgressInfo>> Data = new();

        private static string PathFor(uint accountId) => Path.Combine(AppPaths.AccountDir(accountId), "progress.json");

        private static Dictionary<string, ProgressInfo> Load(uint accountId)
        {
            if (Data.TryGetValue(accountId, out var map) == false)
            {
                map = Json.ReadFile<Dictionary<string, ProgressInfo>>(PathFor(accountId)) ?? new Dictionary<string, ProgressInfo>();
                Data[accountId] = map;
            }
            return map;
        }

        public static Dictionary<uint, ProgressInfo> Snapshot(uint accountId)
        {
            lock (Gate)
            {
                var result = new Dictionary<uint, ProgressInfo>();
                foreach (var kv in Load(accountId))
                {
                    if (uint.TryParse(kv.Key, out var id)) result[id] = kv.Value;
                }
                return result;
            }
        }

        public static void Set(uint accountId, uint appId, ProgressInfo info)
        {
            lock (Gate)
            {
                var map = Load(accountId);
                map[appId.ToString(CultureInfo.InvariantCulture)] = info;
                Json.WriteFile(PathFor(accountId), map);
            }
        }
    }

    /// <summary>
    /// Achievements per game that need a DLC the account does not own (found while loading a game live).
    /// Lets the library show "blocked by DLC" without opening every game again.
    /// </summary>
    public static class DlcStore
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<uint, Dictionary<string, List<string>>> Data = new();

        private static string PathFor(uint accountId) => Path.Combine(AppPaths.AccountDir(accountId), "dlc.json");

        private static Dictionary<string, List<string>> Load(uint accountId)
        {
            if (Data.TryGetValue(accountId, out var map) == false)
            {
                map = Json.ReadFile<Dictionary<string, List<string>>>(PathFor(accountId)) ?? new Dictionary<string, List<string>>();
                Data[accountId] = map;
            }
            return map;
        }

        public static Dictionary<uint, HashSet<string>> Snapshot(uint accountId)
        {
            lock (Gate)
            {
                var result = new Dictionary<uint, HashSet<string>>();
                foreach (var kv in Load(accountId))
                {
                    if (uint.TryParse(kv.Key, out var id) && kv.Value.Count > 0) result[id] = new HashSet<string>(kv.Value);
                }
                return result;
            }
        }

        public static void Set(uint accountId, uint appId, IEnumerable<string> gatedAchievementIds)
        {
            lock (Gate)
            {
                var map = Load(accountId);
                var list = gatedAchievementIds.Distinct().ToList();
                var key = appId.ToString(CultureInfo.InvariantCulture);
                if (list.Count == 0) map.Remove(key); else map[key] = list;
                Json.WriteFile(PathFor(accountId), map);
            }
        }
    }

    /// <summary>On-disk shape of <c>seen.json</c>.</summary>
    public sealed class SeenFile
    {
        public bool Initialized { get; set; }

        /// <summary>appId -> achievement count the user last acknowledged (null = unknown at that time).</summary>
        public Dictionary<string, int?> Known { get; set; } = new();
    }

    /// <summary>
    /// Remembers what the user has already seen, so that new games and games with new achievements can be flagged.
    /// The first time an account is loaded everything counts as seen (nothing is flagged retroactively).
    /// </summary>
    public static class SeenStore
    {
        private static readonly object Gate = new();

        private static string PathFor(uint accountId) => Path.Combine(AppPaths.AccountDir(accountId), "seen.json");

        public static SeenFile Load(uint accountId)
        {
            lock (Gate) { return Json.ReadFile<SeenFile>(PathFor(accountId)) ?? new SeenFile(); }
        }

        public static void Save(uint accountId, SeenFile file)
        {
            lock (Gate) { Json.WriteFile(PathFor(accountId), file); }
        }

        /// <summary>The user has now seen this game with <paramref name="total"/> achievements (clears its "New"/"+N" badge).</summary>
        public static void Ack(uint accountId, uint appId, int? total)
        {
            lock (Gate)
            {
                var file = Load(accountId);
                file.Known[appId.ToString(CultureInfo.InvariantCulture)] = total;
                Save(accountId, file);
            }
        }
    }

    /// <summary>UI preferences (theme, filters, saved quick views). Stored on disk because the local port changes on every start.</summary>
    public static class PrefsStore
    {
        private const int MaxBytes = 512 * 1024;
        private static readonly object Gate = new();
        private static string FilePath => Path.Combine(AppPaths.Root, "prefs.json");

        /// <summary>The preferences exactly as stored. The host does not interpret them - the web UI owns the format.</summary>
        public static string ReadRaw()
        {
            lock (Gate)
            {
                try { return File.Exists(FilePath) ? File.ReadAllText(FilePath) : "{}"; }
                catch (Exception) { return "{}"; }
            }
        }

        public static void WriteRaw(string json)
        {
            if (json == null || json.Length > MaxBytes) throw new ApiException(413, "too-large", "Preferences are too large.");
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, true);
            }
        }
    }

    /// <summary>On-disk shape of <c>friends.json</c>.</summary>
    public sealed class FriendsSettings
    {
        public bool Enabled { get; set; }
        public bool Auto { get; set; }
        public List<string> Selected { get; set; } = new();
    }

    /// <summary>
    /// Friends-comparison settings per account. Default: OFF. Only the Steam IDs of the selected friends are
    /// stored - never any achievement data of friends.
    /// </summary>
    public static class FriendsStore
    {
        public const int MaxSelected = 25;
        private static readonly object Gate = new();

        private static string PathFor(uint accountId) => Path.Combine(AppPaths.AccountDir(accountId), "friends.json");

        public static FriendsSettings Get(uint accountId)
        {
            lock (Gate)
            {
                return Json.ReadFile<FriendsSettings>(PathFor(accountId)) ?? new FriendsSettings();
            }
        }

        /// <summary>
        /// Validates and saves the settings. IDs must be real individual SteamID64s; at most <see cref="MaxSelected"/> are kept.
        /// Turning the feature off clears the selection, and anyone no longer selected loses their stored counts.
        /// </summary>
        public static FriendsSettings Set(uint accountId, bool enabled, bool auto, IEnumerable<string> selected)
        {
            var clean = (selected ?? Enumerable.Empty<string>())
                .Where(s => ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > SteamEnv.SteamId64Base)
                .Distinct()
                .Take(MaxSelected)
                .ToList();

            var settings = new FriendsSettings { Enabled = enabled, Auto = auto && enabled, Selected = enabled ? clean : new List<string>() };
            lock (Gate)
            {
                Json.WriteFile(PathFor(accountId), settings);
            }

            // Whoever is no longer selected (or everyone, when deactivated) loses the stored counts as well
            FriendProgressStore.Prune(accountId, settings.Selected);
            return settings;
        }
    }

    /// <summary>One friend's progress in one game: just the counts, nothing else.</summary>
    public sealed class FriendGameProgress
    {
        public int U { get; set; }        // achievements the friend has unlocked
        public int T { get; set; }        // achievements the game has
        public string S { get; set; }     // "ok", or why there is nothing to show: "hidden", "no-data", "timeout", "request"
        public long Ts { get; set; }      // Unix time of the query
    }

    /// <summary>
    /// What "Load friends' progress" found, per account: friend Steam ID -> app ID -> counts (unlocked / total). Never any unlock
    /// times or achievement names. Only friends that are currently selected are kept - deselecting or deactivating deletes them.
    /// </summary>
    public static class FriendProgressStore
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<uint, Dictionary<string, Dictionary<string, FriendGameProgress>>> Data = new();

        private static string PathFor(uint accountId) => Path.Combine(AppPaths.AccountDir(accountId), "friendprogress.json");

        private static Dictionary<string, Dictionary<string, FriendGameProgress>> Load(uint accountId)
        {
            if (Data.TryGetValue(accountId, out var map) == false)
            {
                map = Json.ReadFile<Dictionary<string, Dictionary<string, FriendGameProgress>>>(PathFor(accountId)) ?? new();
                Data[accountId] = map;
            }
            return map;
        }

        /// <summary>app ID -> friend ID -> progress, for the given friends only.</summary>
        public static Dictionary<string, Dictionary<string, FriendGameProgress>> Snapshot(uint accountId, IEnumerable<string> friends)
        {
            var result = new Dictionary<string, Dictionary<string, FriendGameProgress>>();
            lock (Gate)
            {
                var map = Load(accountId);
                foreach (var friend in friends)
                {
                    if (map.TryGetValue(friend, out var games) == false) continue;
                    foreach (var (appId, progress) in games)
                    {
                        if (result.TryGetValue(appId, out var perFriend) == false) result[appId] = perFriend = new();
                        perFriend[friend] = progress;
                    }
                }
            }
            return result;
        }

        /// <summary>True if every given friend already has a result for this game that is younger than maxAgeSeconds.</summary>
        public static bool IsFresh(uint accountId, uint appId, IEnumerable<string> friends, long maxAgeSeconds)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var key = appId.ToString(CultureInfo.InvariantCulture);
            lock (Gate)
            {
                var map = Load(accountId);
                foreach (var friend in friends)
                {
                    if (map.TryGetValue(friend, out var games) == false || games.TryGetValue(key, out var p) == false || now - p.Ts > maxAgeSeconds) return false;
                }
            }
            return true;
        }

        /// <summary>Stores the results of one game, but only for friends that are still selected.</summary>
        public static void Set(uint accountId, uint appId, IReadOnlyDictionary<string, FriendGameProgress> perFriend, ICollection<string> stillSelected)
        {
            var key = appId.ToString(CultureInfo.InvariantCulture);
            lock (Gate)
            {
                var map = Load(accountId);
                foreach (var (friend, progress) in perFriend)
                {
                    if (stillSelected.Contains(friend) == false) continue;
                    if (map.TryGetValue(friend, out var games) == false) map[friend] = games = new();
                    games[key] = progress;
                }
                Json.WriteFile(PathFor(accountId), map);
            }
        }

        /// <summary>Deletes everything stored about friends that are not in <paramref name="keep"/> (and the file itself if nobody is left).</summary>
        public static void Prune(uint accountId, IEnumerable<string> keep)
        {
            var keepSet = new HashSet<string>(keep);
            lock (Gate)
            {
                var map = Load(accountId);
                var drop = map.Keys.Where(k => keepSet.Contains(k) == false).ToList();
                if (drop.Count == 0) return;
                foreach (var k in drop) map.Remove(k);
                if (map.Count == 0)
                {
                    try { File.Delete(PathFor(accountId)); } catch (IOException) { }
                }
                else
                {
                    Json.WriteFile(PathFor(accountId), map);
                }
            }
        }
    }

    /// <summary>
    /// Provides the list of all Steam apps (as in the original SAM, from gib.me). Cached locally;
    /// if it is unreachable, SAM Modern builds a limited list from locally known games.
    /// </summary>
    public static class GamesList
    {
        private const string Url = "https://gib.me/sam/games.xml";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

        static GamesList()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamAchievementManagerModern/8.0");
        }

        public static async Task<(string path, bool limited)> EnsureAsync(uint accountId)
        {
            var path = Path.Combine(AppPaths.Root, "games.xml");
            Directory.CreateDirectory(AppPaths.Root);

            bool fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(3);
            if (fresh) return (path, false);

            try
            {
                var bytes = await Http.GetByteArrayAsync(Url);
                // Plausibility check before the old list is replaced
                var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                if (doc.Root?.Name.LocalName != "games" || doc.Root.Elements("game").Any() == false)
                {
                    throw new InvalidDataException("Unexpected format of the game list");
                }

                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, path, true);
                return (path, false);
            }
            catch (Exception)
            {
                if (File.Exists(path)) return (path, false); // a stale list is better than none
                return (BuildLocalFallback(accountId), true);
            }
        }

        private static string BuildLocalFallback(uint accountId)
        {
            var ids = new HashSet<uint>();

            try
            {
                foreach (var file in Directory.EnumerateFiles(SteamEnv.StatsDir, "UserGameStatsSchema_*.bin"))
                {
                    var n = Path.GetFileNameWithoutExtension(file).Split('_').Last();
                    if (uint.TryParse(n, out var id)) ids.Add(id);
                }
            }
            catch (Exception)
            {
            }

            foreach (var id in SteamEnv.GetInstalledAppIds()) ids.Add(id);
            foreach (var id in SteamEnv.GetLastPlayed(accountId).Keys) ids.Add(id);
            ids.Add(480); // Spacewar

            var doc = new XDocument(new XElement("games", ids.OrderBy(i => i).Select(i => new XElement("game", i))));
            var path = Path.Combine(AppPaths.Root, "games-local.xml");
            doc.Save(path);
            return path;
        }
    }
}
