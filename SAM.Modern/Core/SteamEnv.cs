using System.Diagnostics;
using Microsoft.Win32;

namespace SamModern.Core
{
    /// <summary>Snapshot of the local Steam client, read from the registry and the process list (no Steam API needed).</summary>
    public sealed class SteamStatus
    {
        public bool Installed { get; set; }
        public bool Running { get; set; }
        public bool LoggedIn { get; set; }
        public uint ActiveAccountId { get; set; }
        public uint RunningAppId { get; set; }
        public string InstallPath { get; set; }
    }

    /// <summary>An account Steam has saved on this computer (one entry of <c>config/loginusers.vdf</c>).</summary>
    public sealed class SteamAccount
    {
        public string SteamId { get; set; }          // SteamID64 as text (JS-safe)
        public uint AccountId { get; set; }
        public string AccountName { get; set; }
        public string PersonaName { get; set; }
        public bool RememberPassword { get; set; }
        public bool AutoLogin { get; set; }
        public long LastLogin { get; set; }
        public bool Active { get; set; }
        public bool HasAvatar { get; set; }
    }

    /// <summary>Everything SAM Modern can read locally about the Steam installation and its accounts.</summary>
    public static class SteamEnv
    {
        /// <summary>A SteamID64 of an individual account is this constant plus the 32-bit "account ID" (used in file names).</summary>
        public const ulong SteamId64Base = 76561197960265728UL;

        /// <summary>SteamID64 -> 32-bit account ID (the number used in <c>userdata/&lt;id&gt;</c> and in the stats file names).</summary>
        public static uint ToAccountId(ulong steamId64) => (uint)(steamId64 - SteamId64Base);

        public static string InstallPath => SAM.API.Steam.GetInstallPath();
        public static string StatsDir => Path.Combine(InstallPath ?? "", "appcache", "stats");
        public static string LibraryCacheDir => Path.Combine(InstallPath ?? "", "appcache", "librarycache");
        public static string LoginUsersPath => Path.Combine(InstallPath ?? "", "config", "loginusers.vdf");

        public static string SteamExe
        {
            get
            {
                var p = InstallPath;
                return p == null ? null : Path.Combine(p, "steam.exe");
            }
        }

        private static object ReadRegistry(string subKey, string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey);
                return key?.GetValue(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static uint ReadDword(string subKey, string name)
        {
            var v = ReadRegistry(subKey, name);
            return v is int i ? unchecked((uint)i) : 0;
        }

        /// <summary>
        /// Is Steam installed, running and signed in, and which account/game is active?
        /// Steam publishes this in <c>HKCU\Software\Valve\Steam\ActiveProcess</c>; the registry can still contain values
        /// of a previous session after a crash, so they only count while a steam process really exists.
        /// </summary>
        public static SteamStatus GetStatus()
        {
            var status = new SteamStatus { InstallPath = InstallPath };
            status.Installed = status.InstallPath != null && File.Exists(SteamExe);

            var pid = ReadDword(@"Software\Valve\Steam\ActiveProcess", "pid");
            var active = ReadDword(@"Software\Valve\Steam\ActiveProcess", "ActiveUser");

            bool processAlive = false;
            try
            {
                processAlive = Process.GetProcessesByName("steam").Length > 0;
                if (pid != 0 && processAlive == false)
                {
                    try { using var p = Process.GetProcessById((int)pid); processAlive = p.HasExited == false; } catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }

            status.Running = processAlive;
            status.ActiveAccountId = processAlive ? active : 0;
            status.LoggedIn = processAlive && active != 0;
            status.RunningAppId = processAlive ? ReadDword(@"Software\Valve\Steam", "RunningAppID") : 0;
            return status;
        }

        /// <summary>Path of the avatar image Steam cached for this SteamID64, or null if there is none.</summary>
        public static string AvatarPath(string steamId64)
        {
            var path = Path.Combine(InstallPath ?? "", "config", "avatarcache", steamId64 + ".png");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// All accounts Steam has saved on this computer, the active one first, then by most recent login.
        /// Older Steam versions call the auto-login flag "MostRecent", newer ones "AutoLogin" - both are understood.
        /// </summary>
        public static List<SteamAccount> GetAccounts(uint activeAccountId)
        {
            var result = new List<SteamAccount>();
            var root = Vdf.ParseFile(LoginUsersPath);
            var users = root?["users"];
            if (users == null) return result;

            foreach (var node in users.Children.Values)
            {
                if (ulong.TryParse(node.Name, out var id64) == false || node.IsBlock == false) continue;
                var accountId = ToAccountId(id64);
                long.TryParse(node.Str("timestamp", "0"), out var ts);
                result.Add(new SteamAccount
                {
                    SteamId = node.Name,
                    AccountId = accountId,
                    AccountName = node.Str("AccountName", ""),
                    PersonaName = node.Str("PersonaName", node.Str("AccountName", "")),
                    RememberPassword = node.Str("RememberPassword", "0") == "1",
                    AutoLogin = node.Str("AutoLogin", node.Str("MostRecent", "0")) == "1",
                    LastLogin = ts,
                    Active = activeAccountId != 0 && accountId == activeAccountId,
                    HasAvatar = AvatarPath(node.Name) != null,
                });
            }

            return result.OrderByDescending(a => a.Active).ThenByDescending(a => a.LastLogin).ToList();
        }

        /// <summary>App IDs that are installed in any Steam library.</summary>
        public static HashSet<uint> GetInstalledAppIds()
        {
            var set = new HashSet<uint>();
            var root = Vdf.ParseFile(Path.Combine(InstallPath ?? "", "config", "libraryfolders.vdf"));
            var folders = root?["libraryfolders"];
            if (folders == null) return set;

            foreach (var folder in folders.Children.Values)
            {
                var apps = folder["apps"];
                if (apps == null) continue;
                foreach (var key in apps.Children.Keys)
                {
                    if (uint.TryParse(key, out var id)) set.Add(id);
                }
            }
            return set;
        }

        /// <summary>Time (Unix) of the last launch per app, from the account's local Steam configuration.</summary>
        public static Dictionary<uint, long> GetLastPlayed(uint accountId)
        {
            var map = new Dictionary<uint, long>();
            if (accountId == 0) return map;

            var path = Path.Combine(InstallPath ?? "", "userdata", accountId.ToString(), "config", "localconfig.vdf");
            var root = Vdf.ParseFile(path);
            var apps = root?.Path("UserLocalConfigStore/Software/Valve/Steam/apps");
            if (apps == null) return map;

            foreach (var app in apps.Children.Values)
            {
                if (uint.TryParse(app.Name, out var id) == false) continue;
                if (long.TryParse(app.Str("LastPlayed", "0"), out var ts) && ts > 0) map[id] = ts;
            }
            return map;
        }
    }
}
