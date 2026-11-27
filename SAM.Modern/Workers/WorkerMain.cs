using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SAM.API;
using SamModern.Core;
using APITypes = SAM.API.Types;

namespace SamModern.Workers
{
    /// <summary>
    /// Child process that is the only one talking to the Steam API. The Steam app ID is set at startup through an
    /// environment variable, so every game needs its own process (exactly like the original SAM).
    /// Protocol: one JSON line per message over stdin/stdout (see <see cref="WorkerProcess"/>).
    /// <para>
    /// Two modes: <c>library</c> (app id 0 - several of these may run next to a game) and <c>game &lt;appId&gt;</c>
    /// (the process becomes that game as far as Steam is concerned; Steam allows only ONE such process per user).
    /// </para>
    /// <para>
    /// Commands: <c>library</c> (which of the known apps are owned), <c>owned</c> (ownership of a list of DLC ids),
    /// <c>friends-list</c>, <c>friends</c> (friends' achievements for this game), <c>load</c> (achievements, stats and DLC
    /// info of this game), <c>apply</c> (unlock/lock/set stats, then store), <c>reset</c>, <c>quit</c>.
    /// </para>
    /// <para>
    /// Steam delivers results through callbacks, which only fire while <c>RunCallbacks</c> is called. That is why waiting
    /// is done with <see cref="Pump"/> instead of blocking, and why the main loop also pumps while idle.
    /// </para>
    /// </summary>
    internal static class WorkerMain
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        };

        private static StreamWriter _out;
        private static Client _client;
        private static uint _appId;

        /// <summary>The latest "stats received" / "stats stored" callback. Callers reset it to null, send a request, then wait for it.</summary>
        private static APITypes.UserStatsReceived? _statsReceived;
        private static APITypes.UserStatsStored? _statsStored;

        // Steam only accepts writes after this user's stats have been requested in this process.
        private static bool _statsLoaded;

        /// <summary>Writes one JSON line to the parent. Locked because replies may come from more than one thread.</summary>
        private static void Send(object message)
        {
            lock (_out)
            {
                _out.WriteLine(JsonSerializer.Serialize(message, JsonOptions));
            }
        }

        /// <summary>Worker entry point: connect to Steam, announce "ready", then serve commands until stdin closes.</summary>
        /// <returns>Process exit code: 0 normal, 2 if the connection to Steam failed.</returns>
        public static int Run(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : "";
            _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            // stdin is read on its own thread so that the main loop can keep pumping Steam's callbacks while it waits.
            var inbox = new BlockingCollection<string>();
            new Thread(() =>
            {
                try
                {
                    using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        inbox.Add(line);
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    inbox.CompleteAdding(); // stdin closed = parent process gone -> we exit
                }
            })
            { IsBackground = true }.Start();

            try
            {
                long appId = 0;
                if (mode == "game")
                {
                    if (args.Length < 2 || uint.TryParse(args[1], out _appId) == false)
                    {
                        throw new ArgumentException("App ID is missing");
                    }
                    appId = _appId;
                }
                else if (mode != "library")
                {
                    throw new ArgumentException("Unknown mode: " + mode);
                }

                _client = new Client();
                _client.Initialize(appId);

                if (mode == "game")
                {
                    _client.CreateAndRegisterCallback<SAM.API.Callbacks.UserStatsReceived>().OnRun += p => _statsReceived = p;
                    _client.CreateAndRegisterCallback<SAM.API.Callbacks.UserStatsStored>().OnRun += p => _statsStored = p;
                }
            }
            catch (Exception ex)
            {
                Send(new
                {
                    evt = "fatal",
                    error = ex.Message,
                    code = (ex as ClientInitializeException)?.Failure.ToString() ?? "Unknown",
                    detail = ex.ToString(),
                });
                return 2;
            }

            Send(new { evt = "ready", mode, appId = _appId });

            // Main loop: pump callbacks, take the next command (waits at most 50 ms), run it, send the answer.
            while (inbox.IsCompleted == false)
            {
                _client.RunCallbacks(false);
                if (inbox.TryTake(out var line, 50) == false)
                {
                    continue;
                }

                JsonNode id = null;
                try
                {
                    var cmd = JsonNode.Parse(line)?.AsObject();
                    id = cmd?["id"]?.DeepClone();
                    var name = cmd?["cmd"]?.GetValue<string>();
                    if (name == "quit")
                    {
                        Send(new { id, ok = true });
                        break;
                    }

                    object data = name switch
                    {
                        "library" => HandleLibrary(cmd),
                        "owned" => HandleOwned(cmd),
                        "friends-list" => HandleFriendList(),
                        "friends" => HandleFriends(cmd),                        "load" => HandleLoad(cmd),
                        "apply" => HandleApply(cmd),
                        "reset" => HandleReset(cmd),
                        _ => throw new InvalidOperationException("Unknown command: " + name),
                    };
                    Send(new { id, ok = true, data });
                }
                catch (WorkerException ex)
                {
                    Send(new { id, ok = false, error = ex.Message, code = ex.Code });
                }
                catch (Exception ex)
                {
                    Send(new { id, ok = false, error = ex.Message, code = "exception" });
                }
            }

            try { _client.Dispose(); } catch (Exception) { }
            return 0;
        }

        /// <summary>Runs Steam's callbacks until <paramref name="done"/> is true. Returns false if the timeout passes first.</summary>
        private static bool Pump(Func<bool> done, int timeoutMs)
        {
            long end = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                _client.RunCallbacks(false);
                if (done()) return true;
                if (Environment.TickCount64 > end) return false;
                Thread.Sleep(20);
            }
        }

        // ------------------------------------------------------------------ Library

        /// <summary>
        /// Which of the known Steam apps does this account own? <c>games</c> is the path of the app list (see GamesList);
        /// every app is checked with IsSubscribedApp (a local lookup, no network) and only the owned ones are named.
        /// </summary>
        private static object HandleLibrary(JsonObject cmd)
        {
            var path = cmd["games"]?.GetValue<string>() ?? throw new WorkerException("args", "Path to the game list is missing");

            var candidates = new List<(uint id, string type)>();
            var doc = XDocument.Load(path);
            foreach (var el in doc.Root.Elements("game"))
            {
                if (uint.TryParse(el.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) == false) continue;
                var type = (string)el.Attribute("type");
                candidates.Add((id, string.IsNullOrEmpty(type) ? "normal" : type));
            }

            var owned = new List<(uint id, string type)>();
            foreach (var c in candidates)
            {
                if (_client.SteamApps008.IsSubscribedApp(c.id)) owned.Add(c);
            }

            var names = new Dictionary<uint, string>();
            foreach (var c in owned)
            {
                names[c.id] = _client.SteamApps001.GetAppData(c.id, "name");
            }

            // Steam loads some app info lazily; wait briefly and ask again.
            long deadline = Environment.TickCount64 + 4000;
            while (Environment.TickCount64 < deadline)
            {
                var missing = names.Where(kv => string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key).ToList();
                if (missing.Count == 0) break;

                for (int i = 0; i < 5; i++)
                {
                    _client.RunCallbacks(false);
                    Thread.Sleep(100);
                }
                foreach (var id in missing)
                {
                    names[id] = _client.SteamApps001.GetAppData(id, "name");
                }
            }

            return new
            {
                steamId = _client.SteamUser.GetSteamId().ToString(CultureInfo.InvariantCulture),
                language = _client.SteamApps008.GetCurrentGameLanguage(),
                candidates = candidates.Count,
                games = owned.Select(c => new { id = c.id, type = c.type, name = names[c.id] }).ToList(),
            };
        }

        /// <summary>Which of the given app IDs (DLCs) does the signed-in account own? Pure local lookup in the Steam client.</summary>
        private static object HandleOwned(JsonObject cmd)
        {
            if (cmd["ids"] is not JsonArray ids) throw new WorkerException("args", "No app IDs given");
            var owned = new List<uint>();
            foreach (var node in ids.Take(5000))
            {
                var id = node?.GetValue<uint>() ?? 0;
                if (id != 0 && _client.SteamApps008.IsSubscribedApp(id)) owned.Add(id);
            }
            return new { steamId = _client.SteamUser.GetSteamId().ToString(CultureInfo.InvariantCulture), owned };
        }

        // ------------------------------------------------------------------ Friends (only on explicit request)

        /// <summary>Reads the friends list from the running Steam client. No data of the friends is queried.</summary>
        private static object HandleFriendList()
        {
            var friends = _client.GetFriends()
                ?? throw new WorkerException("no-friends-api", "This Steam version does not provide the friends list");

            int count = friends.GetFriendCount(SAM.API.Wrappers.SteamFriends015.FlagImmediate);
            var ids = new List<ulong>();
            for (int i = 0; i < count; i++)
            {
                var id = friends.GetFriendByIndex(i, SAM.API.Wrappers.SteamFriends015.FlagImmediate);
                if (id != 0) ids.Add(id);
            }

            var names = new Dictionary<ulong, string>();
            foreach (var id in ids) names[id] = friends.GetFriendPersonaName(id);

            // Some profile names are not in the cache yet shortly after startup.
            long deadline = Environment.TickCount64 + 2500;
            while (Environment.TickCount64 < deadline)
            {
                var missing = names.Where(kv => string.IsNullOrWhiteSpace(kv.Value) || kv.Value == "[unknown]").Select(kv => kv.Key).ToList();
                if (missing.Count == 0) break;

                for (int i = 0; i < 5; i++)
                {
                    _client.RunCallbacks(false);
                    Thread.Sleep(100);
                }
                foreach (var id in missing) names[id] = friends.GetFriendPersonaName(id);
            }

            return new
            {
                friends = ids.Select(id => new
                {
                    steamId = id.ToString(CultureInfo.InvariantCulture),
                    accountId = SteamEnv.ToAccountId(id),
                    name = string.IsNullOrWhiteSpace(names[id]) || names[id] == "[unknown]" ? null : names[id],
                }).ToList(),
            };
        }

        /// <summary>Requests this user's own stats for the game (once per process), without building a full "load" answer.</summary>
        private static void EnsureOwnStats(int waitMs)
        {
            if (_statsLoaded) return;

            var self = _client.SteamUser.GetSteamId();
            _statsReceived = null;
            if (_client.SteamUserStats.RequestUserStats(self) == CallHandle.Invalid) return;
            if (Pump(() => AnswerFor(self), waitMs) == false) return;
            if (_statsReceived.Value.Result == 1) _statsLoaded = true;
        }

        /// <summary>
        /// True once a UserStatsReceived answer about <paramref name="steamId"/> has arrived. Answers about anybody else are
        /// discarded. This matters because Steam answers ONE request for a user's stats with SEVERAL callbacks (measured:
        /// two, ~0.2 s apart, for the user's own stats). Taking the first callback that shows up as "the" answer let the
        /// late duplicate of our own request pose as the answer for the next one, so a friend's achievements were read
        /// before Steam had delivered them - and reported as "no data".
        /// </summary>
        private static bool AnswerFor(ulong steamId)
        {
            if (_statsReceived is { } received && received.SteamIdUser != steamId) _statsReceived = null;
            return _statsReceived != null;
        }

        /// <summary>
        /// Queries the achievements of EXACTLY the given Steam IDs for the game of this worker. Steam itself applies
        /// the privacy settings of the friends - whatever Steam does not hand out stays invisible.
        /// </summary>
        private static object HandleFriends(JsonObject cmd)
        {
            if (cmd["steamIds"] is not JsonArray ids || ids.Count == 0)
            {
                throw new WorkerException("args", "No friends specified");
            }
            if (ids.Count > 25)
            {
                throw new WorkerException("args", "Too many friends at once");
            }

            // How long to wait for Steam's answer per friend (a batch over the whole library uses a short wait)
            int waitMs = Math.Clamp(cmd["timeoutMs"]?.GetValue<int>() ?? 15000, 2000, 30000);

            var language = _client.SteamApps008.GetCurrentGameLanguage();
            if (StatsSchema.TryLoad(_appId, language, out var defs, out _) == false)
            {
                throw new WorkerException("no-schema", "This game has no achievements");
            }
            defs = defs.Where(d => string.IsNullOrEmpty(d.Id) == false).ToList();

            // Steam only hands out a friend's achievements once the game's own stats were requested in this process
            // (that is what loads the game's definitions into the client). The game window always does this first;
            // a bare library scan has to do it here.
            EnsureOwnStats(Math.Max(waitMs, 8000));

            var results = new List<object>();
            foreach (var node in ids)
            {
                var text = node?.GetValue<string>();
                if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var friendId) == false) continue;

                _statsReceived = null;
                if (_client.SteamUserStats.RequestUserStats(friendId) == CallHandle.Invalid)
                {
                    results.Add(new { steamId = text, ok = false, reason = "request" });
                    continue;
                }

                if (Pump(() => AnswerFor(friendId), waitMs) == false)
                {
                    results.Add(new { steamId = text, ok = false, reason = "timeout" });
                    continue;
                }

                if (_statsReceived.Value.Result != 1)
                {
                    results.Add(new { steamId = text, ok = false, reason = "hidden", code = _statsReceived.Value.Result });
                    continue;
                }

                var achieved = new Dictionary<string, long>();
                int readable = 0;
                foreach (var def in defs)
                {
                    if (_client.SteamUserStats.GetUserAchievementAndUnlockTime(friendId, def.Id, out bool isAchieved, out uint unlockTime) == false)
                    {
                        continue;
                    }

                    readable++;
                    if (isAchieved) achieved[def.Id] = unlockTime;
                }

                if (readable == 0)
                {
                    // Steam did respond but handed out nothing (profile/game details not visible, or game not owned)
                    results.Add(new { steamId = text, ok = false, reason = "no-data" });
                    continue;
                }

                results.Add(new { steamId = text, ok = true, total = defs.Count, unlocked = achieved.Count, achieved });
            }

            return new { appId = _appId, results };
        }

        // ------------------------------------------------------------------ Game

        /// <summary>Turns the result code of a failed stats request into something a user can understand.</summary>
        private static string TranslateError(int result) => result switch
        {
            2 => "Generic error - this usually means this account does not own the game or it has no statistics",
            _ => "Steam error code " + result.ToString(CultureInfo.InvariantCulture),
        };

        // ------------------------------------------------------------------ DLC detection
        //
        // Goal: tell which locked achievements can NOT be unlocked because they need a DLC the account does not have.
        // Steam never says so itself (SetAchievement just fails), so two sources are combined:
        //   1. OFFICIAL  - the game's schema assigns achievements to groups and groups to a DLC (see LoadGroupDlcs).
        //   2. FALLBACK  - for games without such groups, guess from the achievement text ("requires X DLC", see MatchDlc),
        //                  using the DLC id list the host looked up in the public store.
        // Ownership of a DLC is IsSubscribedApp(dlcId), corrected by one observation: if an achievement of that DLC is
        // already unlocked, the DLC evidently works for this account ("inferred accessible").

        /// <summary>A DLC of the open game, with everything needed to display and match it.</summary>
        private sealed class DlcInfo
        {
            public uint AppId;
            public string Name;
            public string Label;   // name without the game's name prefix, for display
            public string Key;     // normalized distinctive part, used to match achievement texts
            public bool Owned;
            public bool Inferred;   // counted as accessible because achievements of this DLC are already unlocked
            public bool Available;
        }

        /// <summary>Lower-case, letters and digits only, single spaces: makes names and achievement texts comparable.</summary>
        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>Words at the end of a DLC name that carry no meaning for matching ("... Scandinavia DLC").</summary>
        private static readonly HashSet<string> GenericTail = new() { "dlc", "pack", "expansion", "addon", "content", "bundle" };

        /// <summary>The DLC one achievement belongs to, as reported to the UI.</summary>
        private sealed class DlcMatch
        {
            public uint AppId;
            public string Name;
            public string Label;
            public bool Owned;
            public bool Inferred;
            public string Source;   // "steam" = Steam's own achievement group -> DLC link, "text" = guessed from the achievement text
        }

        /// <summary>Asks the Steam client for app names; apps it does not know yet are fetched by the client, so we ask again for a moment.</summary>
        private static void ResolveNames(Dictionary<uint, string> names, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                foreach (var id in names.Keys.ToList())
                {
                    if (string.IsNullOrWhiteSpace(names[id])) names[id] = _client.SteamApps001.GetAppData(id, "name");
                }
                if (names.Values.All(n => string.IsNullOrWhiteSpace(n) == false) || Environment.TickCount64 > deadline) break;
                for (int i = 0; i < 4; i++) { _client.RunCallbacks(false); Thread.Sleep(100); }
            }
        }

        /// <summary>Owned according to Steam, or evidently usable because an achievement of that DLC is already unlocked.</summary>
        private static bool DlcAccessible(uint dlcId, List<AchievementDef> defs)
        {
            if (_client.SteamApps008.IsSubscribedApp(dlcId)) return true;
            foreach (var d in defs)
            {
                if (d.GroupDlcAppId == dlcId && string.IsNullOrEmpty(d.Id) == false
                    && _client.SteamUserStats.GetAchievementAndUnlockTime(d.Id, out bool done, out _) && done) return true;
            }
            return false;
        }

        /// <summary>
        /// The official link: many games assign each achievement to a "group" and the group to a DLC (dlcappid) in their
        /// stats schema. Ownership of that DLC comes from IsSubscribedApp. Returns DLC app id -> info.
        /// </summary>
        private static Dictionary<uint, DlcInfo> LoadGroupDlcs(List<AchievementDef> defs)
        {
            var result = new Dictionary<uint, DlcInfo>();
            try
            {
                var groupNames = defs
                    .Where(d => d.GroupDlcAppId != 0 && d.GroupDlcAppId != _appId)
                    .GroupBy(d => d.GroupDlcAppId)
                    .ToDictionary(g => g.Key, g => g.First().GroupName);
                if (groupNames.Count == 0) return result;

                var gameName = _client.SteamApps001.GetAppData(_appId, "name") ?? "";
                var gameKey = Norm(gameName);
                var names = groupNames.ToDictionary(kv => kv.Key, kv => (string)null);
                ResolveNames(names, 5000);

                foreach (var kv in groupNames)
                {
                    var name = names[kv.Key];
                    if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(kv.Value) ? "DLC " + kv.Key.ToString(CultureInfo.InvariantCulture) : kv.Value;
                    result[kv.Key] = MakeDlc(kv.Key, name, gameName, gameKey, false);
                }
            }
            catch (Exception)
            {
                // DLC info is a bonus - never let it break loading achievements
            }
            return result;
        }

        /// <summary>Name + description of an achievement, normalised and padded with spaces so whole words can be searched with Contains.</summary>
        private static string AchText(AchievementDef def) => " " + Norm((def.Name ?? "") + " " + (def.Description ?? "")) + " ";

        /// <summary>Does the (normalized, space-padded) text of an achievement talk about a DLC or an expansion?</summary>
        private static bool IsGatedText(string text) =>
            text.Contains(" dlc ") || text.Contains(" requires ") || text.Contains(" require ")
            || text.Contains(" expansion ") || text.Contains(" addon ") || text.Contains(" add on ");

        /// <summary>
        /// Builds a <see cref="DlcInfo"/>. <c>Key</c> is the distinctive part of the name used for text matching (without the
        /// game's own name and generic tails); <c>Label</c> is the short name shown in the UI.
        /// </summary>
        private static DlcInfo MakeDlc(uint dlcId, string name, string gameName, string gameKey, bool available)
        {
            var key = Norm(name);
            if (gameKey.Length > 0 && key.StartsWith(gameKey + " ", StringComparison.Ordinal)) key = key.Substring(gameKey.Length + 1);
            var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 1 && GenericTail.Contains(words[^1])) words.RemoveAt(words.Count - 1);

            var label = name;
            if (gameName.Length > 0 && label.StartsWith(gameName, StringComparison.OrdinalIgnoreCase))
            {
                label = label.Substring(gameName.Length).TrimStart(' ', '-', '–', '—', ':');
            }
            if (string.IsNullOrWhiteSpace(label)) label = name;

            return new DlcInfo
            {
                AppId = dlcId,
                Name = name,
                Label = label,
                Key = string.Join(' ', words),
                Available = available,
                Owned = _client.SteamApps008.IsSubscribedApp(dlcId),
            };
        }

        /// <summary>
        /// All DLCs we can find for this game. Steam itself only hands out the DLCs this account already has access to
        /// (GetDLCCount) and a list capped at 64 entries ("listofdlc"); the host adds the IDs it looked up in the public store.
        /// For every ID the NAME comes from the Steam client (it fetches apps it does not know yet) and the OWNERSHIP from
        /// IsSubscribedApp - so DLCs the account does not own are found too.
        /// </summary>
        private static List<DlcInfo> LoadDlcs(IReadOnlyCollection<uint> extraIds)
        {
            var result = new List<DlcInfo>();
            try
            {
                var gameName = _client.SteamApps001.GetAppData(_appId, "name") ?? "";
                var gameKey = Norm(gameName);
                var found = new Dictionary<uint, (string name, bool available)>();

                int count = _client.SteamApps008.GetDLCCount();
                for (int i = 0; i < count && i < 500; i++)
                {
                    if (_client.SteamApps008.GetDLCDataByIndex(i, out uint id, out bool available, out string name) == false) continue;
                    found[id] = (name, available);
                }

                var listed = _client.SteamApps001.GetAppData(_appId, "listofdlc");
                if (string.IsNullOrEmpty(listed) == false)
                {
                    foreach (var part in listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && found.ContainsKey(id) == false) found[id] = (null, false);
                    }
                }

                if (extraIds != null)
                {
                    foreach (var id in extraIds)
                    {
                        if (found.ContainsKey(id) == false) found[id] = (null, false);
                    }
                }

                // Names: ask the client, let it fetch what it does not know yet, ask again.
                var names = found.ToDictionary(kv => kv.Key, kv => kv.Value.name);
                ResolveNames(names, 6000);

                foreach (var kv in found)
                {
                    var name = names[kv.Key];
                    if (string.IsNullOrWhiteSpace(name)) continue;   // unknown DLC: cannot be matched to an achievement text
                    result.Add(MakeDlc(kv.Key, name, gameName, gameKey, kv.Value.available));
                }
            }
            catch (Exception)
            {
                // DLC info is a bonus - never let it break loading achievements
            }
            return result;
        }

        /// <summary>
        /// Steam does not say which achievement belongs to which DLC, but many games state it in the text
        /// (for example "requires Scandinavia DLC"). We link an achievement to a DLC when its text mentions the
        /// DLC's distinctive name together with a DLC/requires keyword.
        /// </summary>
        private static DlcMatch MatchDlc(AchievementDef def, List<DlcInfo> dlcs)
        {
            if (dlcs.Count == 0) return null;
            var text = AchText(def);
            if (IsGatedText(text) == false) return null;

            var hits = dlcs.Where(d => d.Key.Length >= 4 && text.Contains(" " + d.Key + " ")).ToList();
            // keep only the most specific names ("Heavy Cargo" inside "Heavy Cargo Transport")
            hits = hits.Where(h => hits.Any(o => o != h && o.Key.Length > h.Key.Length && o.Key.Contains(h.Key)) == false).ToList();
            if (hits.Count == 0) return null;

            // "requires Scandinavia and High Power Cargo Pack DLC" needs BOTH - it is blocked if any of them is missing
            var missing = hits.Where(h => h.Owned == false).ToList();
            var shown = missing.Count > 0 ? missing : hits;
            return new DlcMatch
            {
                AppId = shown[0].AppId,
                Name = shown[0].Name,
                Label = string.Join(" + ", shown.Select(s => s.Label)),
                Owned = missing.Count == 0,
                Source = "text",
            };
        }

        /// <summary>
        /// Loads everything about the open game for the signed-in user: achievements (with texts, icons, unlock state,
        /// DLC link), statistics, DLC list and a summary. This is also what makes Steam show "playing", and it must run
        /// before anything is written (Steam rejects writes until the user's stats were requested in this process).
        /// Optional <c>dlcIds</c> in the command: DLC ids the host looked up in the store (text-matching fallback only).
        /// </summary>
        private static object HandleLoad(JsonObject cmd = null)
        {
            _statsReceived = null;
            var steamId = _client.SteamUser.GetSteamId();
            var handle = _client.SteamUserStats.RequestUserStats(steamId);
            if (handle == CallHandle.Invalid)
            {
                throw new WorkerException("request-failed", "The statistics could not be requested");
            }

            if (Pump(() => AnswerFor(steamId), 20000) == false)
            {
                throw new WorkerException("timeout", "Timed out while loading the statistics");
            }

            if (_statsReceived.Value.Result != 1)
            {
                throw new WorkerException("steam-error", TranslateError(_statsReceived.Value.Result));
            }

            _statsLoaded = true;

            var language = _client.SteamApps008.GetCurrentGameLanguage();
            if (StatsSchema.TryLoad(_appId, language, out var defs, out var statDefs) == false)
            {
                throw new WorkerException("no-schema", "This game has no achievements or statistics");
            }

            // 1) Official: the schema's achievement groups name the DLC. Exact, needs no store lookup.
            // 2) Fallback for games without that: guess from the achievement texts ("requires X DLC").
            var groupDlcs = LoadGroupDlcs(defs);
            bool official = groupDlcs.Count > 0;
            int gatedTexts = official ? 0 : defs.Count(d => string.IsNullOrEmpty(d.Id) == false && IsGatedText(AchText(d)));
            var extraDlcIds = (cmd?["dlcIds"] as JsonArray)?.Select(n => n.GetValue<uint>()).ToList();
            var dlcs = official ? groupDlcs.Values.ToList() : gatedTexts > 0 ? LoadDlcs(extraDlcIds) : new List<DlcInfo>();
            var states = new Dictionary<string, (bool achieved, uint time)>();
            foreach (var def in defs)
            {
                if (string.IsNullOrEmpty(def.Id)) continue;
                if (_client.SteamUserStats.GetAchievementAndUnlockTime(def.Id, out bool got, out uint at)) states[def.Id] = (got, at);
            }

            if (official)
            {
                // "Not subscribed" is not the whole truth: an account can have access to a DLC in other ways. If an
                // achievement of a DLC's group is already unlocked, that DLC evidently does not block anything.
                foreach (var def in defs)
                {
                    if (def.GroupDlcAppId != 0 && states.TryGetValue(def.Id ?? "", out var st) && st.achieved
                        && groupDlcs.TryGetValue(def.GroupDlcAppId, out var info) && info.Owned == false)
                    {
                        info.Owned = true;
                        info.Inferred = true;
                    }
                }
            }

            var achievements = new List<object>();
            int unlocked = 0, protTotal = 0, protLocked = 0, dlcLocked = 0;
            foreach (var def in defs)
            {
                if (string.IsNullOrEmpty(def.Id) || states.TryGetValue(def.Id, out var state) == false) continue;
                bool achieved = state.achieved;
                uint unlockTime = state.time;

                if (achieved) unlocked++;
                if (def.Protected)
                {
                    protTotal++;
                    if (achieved == false) protLocked++;
                }

                DlcMatch dlc;
                if (official)
                {
                    dlc = def.GroupDlcAppId != 0 && groupDlcs.TryGetValue(def.GroupDlcAppId, out var gd)
                        ? new DlcMatch { AppId = gd.AppId, Name = gd.Name, Label = gd.Label, Owned = gd.Owned, Inferred = gd.Inferred, Source = "steam" }
                        : null;
                }
                else
                {
                    dlc = MatchDlc(def, dlcs);
                }
                if (dlc != null && dlc.Owned == false && def.Protected == false && achieved == false) dlcLocked++;

                bool localizedMissing = def.Name != null && def.Name.StartsWith('#');
                achievements.Add(new
                {
                    id = def.Id,
                    name = localizedMissing ? def.Id : def.Name,
                    description = localizedMissing ? "" : def.Description,
                    iconNormal = def.IconNormal,
                    iconLocked = def.IconLocked,
                    hidden = def.Hidden,
                    permission = def.Permission,
                    @protected = def.Protected,
                    achieved,
                    unlockTime = achieved && unlockTime > 0 ? (long?)unlockTime : null,
                    group = string.IsNullOrWhiteSpace(def.GroupName) ? null : def.GroupName,
                    dlc = dlc == null ? null : new { appId = dlc.AppId, name = dlc.Name, label = dlc.Label, owned = dlc.Owned, inferred = dlc.Inferred, source = dlc.Source },
                });
            }

            var stats = new List<object>();
            foreach (var def in statDefs)
            {
                if (string.IsNullOrEmpty(def.Id)) continue;

                object value;
                if (def.Kind == "int")
                {
                    if (_client.SteamUserStats.GetStatValue(def.Id, out int v) == false) continue;
                    value = v;
                }
                else
                {
                    if (_client.SteamUserStats.GetStatValue(def.Id, out float v) == false) continue;
                    value = v;
                }

                stats.Add(new
                {
                    id = def.Id,
                    name = string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName,
                    kind = def.Kind,
                    value,
                    incrementOnly = def.IncrementOnly,
                    @protected = def.Protected,
                    permission = def.Permission,
                    min = def.Min,
                    max = def.Max,
                });
            }

            var name = _client.SteamApps001.GetAppData(_appId, "name");
            return new
            {
                appId = _appId,
                name,
                steamId = steamId.ToString(CultureInfo.InvariantCulture),
                language,
                achievements,
                stats,
                dlcs = dlcs.Select(d => new { appId = d.AppId, name = d.Name, label = d.Label, owned = d.Owned, inferred = d.Inferred, available = d.Available }).ToList(),
                dlcInfo = new { gated = gatedTexts, listed = dlcs.Count, extraProvided = extraDlcIds != null, source = official ? "steam" : "text" },
                summary = new { total = achievements.Count, unlocked, protTotal, protLocked, dlcLocked },
            };
        }

        /// <summary>
        /// Applies changes: <c>unlock</c> / <c>lock</c> (achievement ids) and <c>stats</c> ({id, value} list), then stores them
        /// and returns the freshly loaded game. Nothing is guessed: every id is checked against the schema, achievements and
        /// statistics the game marks as protected are refused, and each failure is reported with a REASON
        /// (<c>unknown</c>, <c>protected</c>, <c>invalid</c>, <c>dlc</c> + DLC name, or <c>steam</c> = refused without a reason).
        /// </summary>
        private static object HandleApply(JsonObject cmd)
        {
            // A freshly started worker (e.g. after a timeout) must load the stats before it may write anything.
            if (_statsLoaded == false) HandleLoad();

            var language = _client.SteamApps008.GetCurrentGameLanguage();
            if (StatsSchema.TryLoad(_appId, language, out var defs, out var statDefs) == false)
            {
                throw new WorkerException("no-schema", "Schema not available");
            }

            var byId = defs.Where(d => string.IsNullOrEmpty(d.Id) == false).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
            var statById = statDefs.Where(d => string.IsNullOrEmpty(d.Id) == false).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());

            var failed = new List<object>();
            int changed = 0;

            void SetAch(JsonNode list, bool state)
            {
                if (list is not JsonArray arr) return;
                foreach (var item in arr)
                {
                    var id = item?.GetValue<string>();
                    if (id == null) continue;

                    if (byId.TryGetValue(id, out var def) == false)
                    {
                        failed.Add(new { id, reason = "unknown" });
                        continue;
                    }
                    if (def.Protected)
                    {
                        failed.Add(new { id, reason = "protected" });
                        continue;
                    }
                    if (_client.SteamUserStats.SetAchievement(id, state) == false)
                    {
                        // Steam does not say why. If the achievement belongs to a DLC this account does not own, that is the reason.
                        if (def.GroupDlcAppId != 0 && def.GroupDlcAppId != _appId && DlcAccessible(def.GroupDlcAppId, defs) == false)
                        {
                            var dlcName = _client.SteamApps001.GetAppData(def.GroupDlcAppId, "name");
                            if (string.IsNullOrWhiteSpace(dlcName)) dlcName = string.IsNullOrWhiteSpace(def.GroupName) ? "DLC " + def.GroupDlcAppId.ToString(CultureInfo.InvariantCulture) : def.GroupName;
                            failed.Add(new { id, reason = "dlc", detail = dlcName });
                        }
                        else
                        {
                            failed.Add(new { id, reason = "steam", detail = (string)null });
                        }
                        continue;
                    }
                    changed++;
                }
            }

            SetAch(cmd["unlock"], true);
            SetAch(cmd["lock"], false);

            if (cmd["stats"] is JsonArray statArr)
            {
                foreach (var item in statArr)
                {
                    var id = item?["id"]?.GetValue<string>();
                    var raw = item?["value"]?.ToString();
                    if (id == null || raw == null) continue;

                    if (statById.TryGetValue(id, out var def) == false)
                    {
                        failed.Add(new { id, reason = "unknown" });
                        continue;
                    }

                    bool ok;
                    if (def.Kind == "int")
                    {
                        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv) == false)
                        {
                            failed.Add(new { id, reason = "invalid" });
                            continue;
                        }
                        if (def.Protected && _client.SteamUserStats.GetStatValue(id, out int current) && current != iv)
                        {
                            failed.Add(new { id, reason = "protected" });
                            continue;
                        }
                        ok = _client.SteamUserStats.SetStatValue(id, iv);
                    }
                    else
                    {
                        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv) == false)
                        {
                            failed.Add(new { id, reason = "invalid" });
                            continue;
                        }
                        if (def.Protected && _client.SteamUserStats.GetStatValue(id, out float current) && current.Equals(fv) == false)
                        {
                            failed.Add(new { id, reason = "protected" });
                            continue;
                        }
                        ok = _client.SteamUserStats.SetStatValue(id, fv);
                    }

                    if (ok) changed++;
                    else failed.Add(new { id, reason = "steam" });
                }
            }

            bool stored = false;
            if (changed > 0)
            {
                _statsStored = null;
                if (_client.SteamUserStats.StoreStats() == false)
                {
                    throw new WorkerException("store-failed", "Steam rejected the store request");
                }

                stored = Pump(() => _statsStored != null, 10000) && _statsStored.Value.Result == 1;
            }

            return new { changed, failed, stored, data = HandleLoad(cmd) };
        }

        /// <summary>Resets all statistics of the game (and the achievements too if <c>achievements</c> is true). Destructive - the UI asks twice.</summary>
        private static object HandleReset(JsonObject cmd)
        {
            bool withAchievements = cmd["achievements"]?.GetValue<bool>() ?? false;
            if (_statsLoaded == false) HandleLoad();
            if (_client.SteamUserStats.ResetAllStats(withAchievements) == false)
            {
                throw new WorkerException("reset-failed", "Reset failed");
            }

            return new { data = HandleLoad(cmd) };
        }
    }
}
