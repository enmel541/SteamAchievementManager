using System.Globalization;
using APITypes = SAM.API.Types;

namespace SamModern.Core
{
    /// <summary>
    /// One achievement as described by Steam's local schema file (<c>UserGameStatsSchema_&lt;appId&gt;.bin</c>).
    /// In Steam's data model an achievement is a single BIT inside a 32-bit "stat" field; <see cref="StatId"/> and
    /// <see cref="Bit"/> say where the user's unlock state lives in <c>UserGameStats_&lt;account&gt;_&lt;appId&gt;.bin</c>.
    /// </summary>
    public sealed class AchievementDef
    {
        public string Id;
        public string Name;
        public string Description;
        public string IconNormal;
        public string IconLocked;
        public bool Hidden;
        public int Permission;
        public string StatId;   // stat that holds this bit
        public int Bit;         // bit position (0..31)
        public int GroupId;
        public string GroupName;      // achievement group as the game defines it, e.g. "Scandinavia Map Expansion Achievements"
        public uint GroupDlcAppId;    // Steam's own link from that group to a DLC (0 = none)

        /// <summary>
        /// As in the original SAM: (permission &amp; 3) != 0 means the achievement is set by the game/server
        /// and can NOT be changed with the Achievement Manager.
        /// </summary>
        public bool Protected => (Permission & 3) != 0;
    }

    /// <summary>A numeric game statistic (integer, float or average-rate) from the schema, with its limits.</summary>
    public sealed class StatDef
    {
        public string Id;
        public string DisplayName;
        public string Kind;           // "int" | "float"
        public double Min;
        public double Max;
        public double MaxChange;
        public double Default;
        public bool IncrementOnly;
        public bool SetByTrustedGameServer;
        public int Permission;
        public bool Protected => (Permission & 2) != 0;
    }

    /// <summary>
    /// Achievement progress of one game for one account, as shown on the library cards. The nullable counts mean
    /// "unknown": the schema says how many achievements exist (<see cref="Total"/>), but there is no user data yet.
    /// </summary>
    public sealed class ProgressInfo
    {
        public int Total { get; set; }
        public int? Unlocked { get; set; }      // null = unknown (schema present, but no user data)
        public int ProtTotal { get; set; }
        public int? ProtLocked { get; set; }
        public int? DlcLocked { get; set; }     // locked achievements that need a DLC the account does not own
        public long Ts { get; set; }            // Unix time of the data
        public string Src { get; set; }         // "cache" | "live"
        public bool Groups { get; set; }        // DlcLocked was derived from the game's own achievement groups (current ownership data)
    }

    /// <summary>
    /// Reader for the schema files Steam keeps in <c>appcache/stats</c>. They are binary KeyValues files that describe
    /// every achievement and statistic of a game (texts in all languages, icons, permission flags, optional DLC groups).
    /// Steam refreshes a file whenever the game is opened or updated, so it can be stale for games not played lately.
    /// </summary>
    public static class StatsSchema
    {
        /// <summary>Game description: which achievements and stats exist.</summary>
        public static string SchemaPath(uint appId) =>
            Path.Combine(SteamEnv.StatsDir, string.Create(CultureInfo.InvariantCulture, $"UserGameStatsSchema_{appId}.bin"));

        /// <summary>The account's data for that game: which achievement bits are set and the stat values.</summary>
        public static string UserStatsPath(uint accountId, uint appId) =>
            Path.Combine(SteamEnv.StatsDir, string.Create(CultureInfo.InvariantCulture, $"UserGameStats_{accountId}_{appId}.bin"));

        /// <summary>Picks a text in the wanted language, falling back to English, then to the plain value, then to <paramref name="fallback"/>.</summary>
        private static string Localized(KeyValue kv, string language, string fallback)
        {
            var name = kv[language].AsString("");
            if (string.IsNullOrEmpty(name) == false) return name;

            if (language != "english")
            {
                name = kv["english"].AsString("");
                if (string.IsNullOrEmpty(name) == false) return name;
            }

            name = kv.AsString("");
            return string.IsNullOrEmpty(name) == false ? name : fallback;
        }

        private static readonly Dictionary<uint, (long stamp, List<AchievementDef> defs)> EnglishCache = new();

        /// <summary>The achievements of a game (English texts) from the local schema file, cached per file version. Null if there is no schema. Callers must not modify the list.</summary>
        public static List<AchievementDef> CachedAchievements(uint appId)
        {
            long stamp;
            try
            {
                var path = SchemaPath(appId);
                if (File.Exists(path) == false) return null;
                stamp = File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception)
            {
                return null;
            }

            lock (EnglishCache)
            {
                if (EnglishCache.TryGetValue(appId, out var hit) && hit.stamp == stamp) return hit.defs;
            }

            var defs = TryLoad(appId, "english", out var achievements, out _) ? achievements : null;
            lock (EnglishCache) { EnglishCache[appId] = (stamp, defs); }
            return defs;
        }

        /// <summary>The DLC app IDs that this game's achievement groups point to (empty for games without such groups).</summary>
        public static IReadOnlyCollection<uint> GroupDlcIds(uint appId)
        {
            var defs = CachedAchievements(appId);
            if (defs == null) return Array.Empty<uint>();
            return defs.Where(a => a.GroupDlcAppId != 0 && a.GroupDlcAppId != appId).Select(a => a.GroupDlcAppId).Distinct().ToArray();
        }

        /// <summary>
        /// Reads UserGameStatsSchema_{appId}.bin (logic from SAM.Game/Manager.cs, LoadUserGameStatsSchema).
        /// The file layout is: <c>&lt;appId&gt; { stats { &lt;n&gt; { type, name, display, bits { ... } } } groups { ... } }</c>.
        /// A "stats" entry is either a numeric stat or an ACHIEVEMENTS block whose "bits" are the achievements.
        /// Returns false if the file is missing or unreadable; never throws.
        /// </summary>
        public static bool TryLoad(uint appId, string language, out List<AchievementDef> achievements, out List<StatDef> stats)
        {
            achievements = new List<AchievementDef>();
            stats = new List<StatDef>();

            try
            {
                var path = SchemaPath(appId);
                if (File.Exists(path) == false) return false;

                var kv = KeyValue.LoadAsBinary(path);
                if (kv == null) return false;

                var node = kv[appId.ToString(CultureInfo.InvariantCulture)]["stats"];
                if (node.Valid == false || node.Children == null) return false;

                // Achievement groups. Some games assign each group to a DLC ("dlcappid") - that is Steam's own,
                // exact record of which achievements belong to which DLC.
                var groups = new Dictionary<int, (string name, uint dlc)>();
                var groupNode = kv[appId.ToString(CultureInfo.InvariantCulture)]["groups"];
                if (groupNode.Valid && groupNode.Children != null)
                {
                    foreach (var g in groupNode.Children)
                    {
                        if (int.TryParse(g.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var groupKey) == false) continue;
                        int dlc = g["dlcappid"].AsInteger(0);
                        groups[groupKey] = (Localized(g["name"], language, ""), dlc > 0 ? (uint)dlc : 0u);
                    }
                }

                foreach (var stat in node.Children)
                {
                    if (stat.Valid == false) continue;

                    // The type is stored as text in current schema files ("INT", "ACHIEVEMENTS", ...) and as a number
                    // ("type_int") in older ones - both formats are accepted.
                    APITypes.UserStatType type;
                    var typeNode = stat["type"];
                    if (typeNode.Valid && typeNode.Type == KeyValueType.String &&
                        Enum.TryParse((string)typeNode.Value, true, out type))
                    {
                        // new format (text)
                    }
                    else
                    {
                        type = APITypes.UserStatType.Invalid;
                    }

                    if (type == APITypes.UserStatType.Invalid)
                    {
                        var typeInt = stat["type_int"];
                        type = (APITypes.UserStatType)(typeInt.Valid ? typeInt.AsInteger(0) : typeNode.AsInteger(0));
                    }

                    switch (type)
                    {
                        case APITypes.UserStatType.Integer:
                        {
                            var id = stat["name"].AsString("");
                            stats.Add(new StatDef
                            {
                                Id = id,
                                DisplayName = Localized(stat["display"]["name"], language, id),
                                Kind = "int",
                                Min = stat["min"].AsInteger(int.MinValue),
                                Max = stat["max"].AsInteger(int.MaxValue),
                                MaxChange = stat["maxchange"].AsInteger(0),
                                Default = stat["default"].AsInteger(0),
                                IncrementOnly = stat["incrementonly"].AsBoolean(false),
                                SetByTrustedGameServer = stat["bSetByTrustedGS"].AsBoolean(false),
                                Permission = stat["permission"].AsInteger(0),
                            });
                            break;
                        }

                        case APITypes.UserStatType.Float:
                        case APITypes.UserStatType.AverageRate:
                        {
                            var id = stat["name"].AsString("");
                            stats.Add(new StatDef
                            {
                                Id = id,
                                DisplayName = Localized(stat["display"]["name"], language, id),
                                Kind = "float",
                                Min = stat["min"].AsFloat(float.MinValue),
                                Max = stat["max"].AsFloat(float.MaxValue),
                                MaxChange = stat["maxchange"].AsFloat(0f),
                                Default = stat["default"].AsFloat(0f),
                                IncrementOnly = stat["incrementonly"].AsBoolean(false),
                                Permission = stat["permission"].AsInteger(0),
                            });
                            break;
                        }

                        case APITypes.UserStatType.Achievements:
                        case APITypes.UserStatType.GroupAchievements:
                        {
                            if (stat.Children == null) break;

                            foreach (var bits in stat.Children.Where(b => string.Equals(b.Name, "bits", StringComparison.OrdinalIgnoreCase)))
                            {
                                if (bits.Valid == false || bits.Children == null) continue;

                                foreach (var bit in bits.Children)
                                {
                                    if (int.TryParse(bit.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitIndex) == false)
                                    {
                                        continue;
                                    }

                                    var id = bit["name"].AsString("");
                                    var iconNormal = bit["display"]["icon"].AsString("");
                                    var iconLocked = bit["display"]["icon_gray"].AsString("");
                                    int groupId = bit["groupid"].AsInteger(0);
                                    groups.TryGetValue(groupId, out var group);
                                    achievements.Add(new AchievementDef
                                    {
                                        Id = id,
                                        Name = Localized(bit["display"]["name"], language, id),
                                        Description = Localized(bit["display"]["desc"], language, ""),
                                        IconNormal = string.IsNullOrEmpty(iconNormal) ? null : iconNormal,
                                        IconLocked = string.IsNullOrEmpty(iconLocked) ? (string.IsNullOrEmpty(iconNormal) ? null : iconNormal) : iconLocked,
                                        Hidden = bit["display"]["hidden"].AsBoolean(false),
                                        Permission = bit["permission"].AsInteger(0),
                                        StatId = stat.Name,
                                        Bit = bitIndex,
                                        GroupId = groupId,
                                        GroupName = group.name,
                                        GroupDlcAppId = group.dlc,
                                    });
                                }
                            }
                            break;
                        }
                    }
                }

                return true;
            }
            catch (Exception)
            {
                achievements = new List<AchievementDef>();
                stats = new List<StatDef>();
                return false;
            }
        }
    }

    /// <summary>
    /// Computes the achievement progress of a game purely from the local Steam cache files
    /// (appcache/stats). It is available instantly and does not show up as "playing" for friends.
    /// </summary>
    public static class LocalProgress
    {
        /// <summary>
        /// Counts unlocked, protected and DLC-blocked achievements of one game from the two local files.
        /// <list type="bullet">
        /// <item><b>unlocked</b>: the achievement's bit is set in the user's stats file.</item>
        /// <item><b>protected</b> (locked): set by the game itself; SAM cannot change it.</item>
        /// <item><b>DLC-blocked</b> (locked): belongs to a DLC the account cannot use. Decided from Steam's own
        /// group-to-DLC link when the ownership of that DLC is known (<paramref name="ownership"/>); otherwise from the
        /// ids a live load of the game recorded (<paramref name="dlcGated"/>).</item>
        /// </list>
        /// Returns null if the game has no schema; <c>Unlocked == null</c> if the schema exists but the user has no data yet.
        /// </summary>
        public static ProgressInfo Compute(uint accountId, uint appId, HashSet<string> dlcGated = null, DlcOwnershipSnapshot ownership = null)
        {
            var achievements = StatsSchema.CachedAchievements(appId);
            if (achievements == null)
            {
                return null;
            }

            achievements = achievements.Where(a => string.IsNullOrEmpty(a.Id) == false).ToList();
            var info = new ProgressInfo
            {
                Total = achievements.Count,
                ProtTotal = achievements.Count(a => a.Protected),
                Src = "cache",
            };

            if (achievements.Count == 0)
            {
                info.Unlocked = 0;
                info.ProtLocked = 0;
                return info;
            }

            var userPath = StatsSchema.UserStatsPath(accountId, appId);
            var user = File.Exists(userPath) ? KeyValue.LoadAsBinary(userPath) : null;
            if (user == null)
            {
                return info; // total known, progress unknown
            }

            try
            {
                info.Ts = new DateTimeOffset(File.GetLastWriteTimeUtc(userPath)).ToUnixTimeSeconds();
            }
            catch (Exception)
            {
            }

            // The user's unlock state: each stat field under "cache" holds up to 32 achievements as bits.
            var cache = user["cache"];
            var achievedIds = new HashSet<string>();
            foreach (var def in achievements)
            {
                uint data = unchecked((uint)cache[def.StatId]["data"].AsInteger(0));
                if (((data >> def.Bit) & 1u) != 0) achievedIds.Add(def.Id);
            }

            // DLC whose achievements are already (partly) unlocked evidently do not block anything
            var usable = new HashSet<uint>();
            foreach (var def in achievements)
            {
                if (def.GroupDlcAppId != 0 && achievedIds.Contains(def.Id)) usable.Add(def.GroupDlcAppId);
            }

            int unlocked = 0, protLocked = 0, dlcLocked = 0;
            foreach (var def in achievements)
            {
                if (achievedIds.Contains(def.Id)) { unlocked++; continue; }
                if (def.Protected) { protLocked++; continue; }

                // Official link (achievement group -> DLC) when the ownership of that DLC is known, else what a live load recorded
                bool groupLinked = def.GroupDlcAppId != 0 && def.GroupDlcAppId != appId && ownership != null && ownership.Checked.Contains(def.GroupDlcAppId);
                if (groupLinked)
                {
                    info.Groups = true;
                    if (ownership.Owned.Contains(def.GroupDlcAppId) == false && usable.Contains(def.GroupDlcAppId) == false) dlcLocked++;
                }
                else if (dlcGated != null && dlcGated.Contains(def.Id)) dlcLocked++;
            }

            info.Unlocked = unlocked;
            info.ProtLocked = protLocked;
            info.DlcLocked = dlcLocked;
            return info;
        }

        /// <summary>Computes the progress of many games; games without a schema are left out of the result.</summary>
        public static Dictionary<uint, ProgressInfo> ComputeAll(uint accountId, IEnumerable<uint> appIds, IReadOnlyDictionary<uint, HashSet<string>> dlcGated = null, DlcOwnershipSnapshot ownership = null)
        {
            var map = new Dictionary<uint, ProgressInfo>();
            foreach (var id in appIds)
            {
                HashSet<string> gated = null;
                dlcGated?.TryGetValue(id, out gated);
                var p = Compute(accountId, id, gated, ownership);
                if (p != null) map[id] = p;
            }
            return map;
        }
    }
}
