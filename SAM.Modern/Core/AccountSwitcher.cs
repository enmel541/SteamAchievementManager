using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using SamModern.Workers;

namespace SamModern.Core
{
    /// <summary>
    /// Steam is only ever logged in with ONE account, and the Steam API is bound to the logged-in account.
    /// "Switching account" therefore means: shut Steam down cleanly, mark the target account for automatic login
    /// and restart Steam (the same method common account switchers use).
    /// </summary>
    public sealed class AccountSwitcher
    {
        private readonly object _lock = new();
        private SwitchState _state = new() { State = "idle" };

        /// <summary>
        /// Progress of a switch, polled by the UI (GET /api/accounts/switch). The states run in this order:
        /// <c>stopping</c> (closing Steam) -> <c>configuring</c> (backup + auto-login) -> <c>starting</c> (launching Steam)
        /// -> <c>waiting</c> (for the sign-in to finish) -> <c>done</c> or <c>error</c>.
        /// </summary>
        public sealed class SwitchState
        {
            public string State { get; set; }       // idle | stopping | configuring | starting | waiting | done | error
            public string Message { get; set; }
            public string Target { get; set; }      // SteamID64
            public string TargetName { get; set; }
            public long StartedAt { get; set; }
            public bool Active => State is "stopping" or "configuring" or "starting" or "waiting";
        }

        /// <summary>A copy of the current state, so callers can serialise it without holding the lock.</summary>
        public SwitchState Current
        {
            get
            {
                lock (_lock)
                {
                    return new SwitchState
                    {
                        State = _state.State,
                        Message = _state.Message,
                        Target = _state.Target,
                        TargetName = _state.TargetName,
                        StartedAt = _state.StartedAt,
                    };
                }
            }
        }

        private void Set(string state, string message)
        {
            lock (_lock)
            {
                _state.State = state;
                _state.Message = message;
            }
        }

        /// <summary>Checks the preconditions and starts the switch in the background.</summary>
        public void Start(string steamId64)
        {
            var status = SteamEnv.GetStatus();
            if (status.Installed == false)
            {
                throw new ApiException(409, "steam-missing", "Steam was not found on this computer.");
            }

            var accounts = SteamEnv.GetAccounts(status.ActiveAccountId);
            var target = accounts.FirstOrDefault(a => a.SteamId == steamId64)
                ?? throw new ApiException(404, "unknown-account", "This account is not saved in Steam.");

            if (target.Active && status.Running)
            {
                throw new ApiException(409, "already-active", "This account is already active.");
            }

            if (status.Running && status.RunningAppId != 0)
            {
                throw new ApiException(409, "game-running",
                    "A game is currently running in Steam (app " + status.RunningAppId.ToString(CultureInfo.InvariantCulture) + "). Please quit it first.");
            }

            lock (_lock)
            {
                if (_state.Active)
                {
                    throw new ApiException(409, "switch-running", "An account switch is already in progress.");
                }

                _state = new SwitchState
                {
                    State = "stopping",
                    Message = status.Running ? "Shutting down Steam ..." : "Preparing Steam ...",
                    Target = target.SteamId,
                    TargetName = target.PersonaName,
                    StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
            }

            _ = Task.Run(() => RunAsync(target, status.Running));
        }

        /// <summary>The actual switch. Runs on a background thread; every failure ends up in the state as "error".</summary>
        private async Task RunAsync(SteamAccount target, bool steamRunning)
        {
            try
            {
                // Release our own workers, otherwise they stay attached to the old Steam instance.
                WorkerProcess.KillAll();

                if (steamRunning)
                {
                    Set("stopping", "Shutting down Steam ...");
                    Process.Start(new ProcessStartInfo(SteamEnv.SteamExe, "-shutdown") { UseShellExecute = false, CreateNoWindow = true });
                    if (await WaitForExitAsync(TimeSpan.FromSeconds(60)) == false)
                    {
                        throw new InvalidOperationException("Steam could not be shut down. Please close Steam manually and try again.");
                    }
                }

                Set("configuring", "Preparing the account ...");
                PrepareAutoLogin(target);

                Set("starting", "Starting Steam as \"" + target.PersonaName + "\" ...");
                Process.Start(new ProcessStartInfo(SteamEnv.SteamExe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = SteamEnv.InstallPath,
                });

                var message = target.RememberPassword
                    ? "Waiting for the sign-in in Steam ..."
                    : "Waiting for the sign-in - please enter the password in Steam.";
                Set("waiting", message);

                var deadline = DateTime.UtcNow.AddMinutes(4);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1000);
                    var status = SteamEnv.GetStatus();
                    if (status.LoggedIn && status.ActiveAccountId == target.AccountId)
                    {
                        // Steam needs a moment after sign-in before its API responds.
                        await Task.Delay(2500);
                        Set("done", "Signed in as " + target.PersonaName + ".");
                        return;
                    }
                }

                throw new TimeoutException("The sign-in in Steam was not completed. Please sign in to Steam and reload the page.");
            }
            catch (Exception ex)
            {
                Set("error", ex.Message);
            }
        }

        /// <summary>Waits until neither steam.exe nor its web helper is running any more (or the timeout passes).</summary>
        private static async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                bool any = Process.GetProcessesByName("steam").Length > 0 || Process.GetProcessesByName("steamwebhelper").Length > 0;
                if (any == false)
                {
                    await Task.Delay(1500); // Steam is still writing configuration files while shutting down
                    return true;
                }
                await Task.Delay(500);
            }
            return false;
        }

        /// <summary>
        /// Makes Steam sign in as <paramref name="target"/> on its next start. Steam decides this from two places, so both
        /// are set: the registry (<c>AutoLoginUser</c>) and the flags in loginusers.vdf. Nothing is touched before the
        /// previous values were saved to the backups folder (the newest 12 backups are kept).
        /// </summary>
        private static void PrepareAutoLogin(SteamAccount target)
        {
            // 1) Back up the login file and the previous registry values (to allow reverting manually)
            var backups = AppPaths.Sub("backups");
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var vdfPath = SteamEnv.LoginUsersPath;
            if (File.Exists(vdfPath))
            {
                File.Copy(vdfPath, Path.Combine(backups, $"loginusers_{stamp}.vdf"), true);
            }

            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                Json.WriteFile(Path.Combine(backups, $"registry_{stamp}.json"), new
                {
                    autoLoginUser = key?.GetValue("AutoLoginUser") as string,
                    rememberPassword = key?.GetValue("RememberPassword"),
                });
            }

            foreach (var old in Directory.GetFiles(backups).OrderByDescending(f => File.GetCreationTimeUtc(f)).Skip(12))
            {
                try { File.Delete(old); } catch (Exception) { }
            }

            // 2) Mark the target account for automatic login
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steam"))
            {
                key.SetValue("AutoLoginUser", target.AccountName, RegistryValueKind.String);
                key.SetValue("RememberPassword", 1, RegistryValueKind.DWord);
            }

            // 3) Auto-login flag in loginusers.vdf (only if the key exists)
            if (File.Exists(vdfPath))
            {
                var text = File.ReadAllText(vdfPath);
                var root = Vdf.Parse(text);
                var users = root["users"];
                if (users != null)
                {
                    foreach (var user in users.Children.Values)
                    {
                        var flag = user["AutoLogin"] != null ? "AutoLogin" : user["MostRecent"] != null ? "MostRecent" : null;
                        if (flag == null) continue;
                        Vdf.TrySetValueInBlock(ref text, user.Name, flag, user.Name == target.SteamId ? "1" : "0");
                    }
                    File.WriteAllText(vdfPath, text);
                }
            }
        }
    }
}
