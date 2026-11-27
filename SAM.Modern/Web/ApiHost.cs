using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SamModern.Core;
using SamModern.Workers;

namespace SamModern.Web
{
    /// <summary>
    /// Local web server for the user interface. It only listens on 127.0.0.1, requires a random token
    /// (header X-SAM-Token or ?t=) and checks the Host header. This way no web page in a browser can
    /// unlock achievements or restart Steam via localhost.
    /// <para>
    /// It serves two things: the static web UI from <c>wwwroot</c> and a small JSON API under <c>/api</c> (plus
    /// <c>/img</c> and <c>/avatar</c> for pictures). The API is a thin layer: every route validates its input, enforces
    /// the rules (opt-in friends, confirmations, "only one batch job at a time") and delegates to a service in
    /// <c>Core/Services.cs</c>.
    /// </para>
    /// </summary>
    public sealed class ApiHost : IAsyncDisposable
    {
        private WebApplication _app;

        /// <summary>Secret that every API request must carry. Random per start; it travels in the URL fragment, which is never sent over the network.</summary>
        public string Token { get; private set; }
        public int Port { get; private set; }
        public string Url => $"http://127.0.0.1:{Port}/";
        public string UrlWithToken => $"{Url}#t={Token}";

        // One instance of each service for the life of the app (they hold the state of running jobs).
        private readonly LibraryService _library = new();
        private readonly GameSessions _sessions = new();
        private readonly ScanService _scan = new();
        private readonly FriendScanService _friendScan = new();
        private readonly RarityService _rarity = new();
        private readonly ImageService _images = new();
        private readonly FriendService _friends = new();
        private readonly AutoService _auto = new();
        private readonly AccountSwitcher _switcher = new();

        /// <param name="port">0 = let the OS choose a free port (normal app). A fixed port is used by the dev server.</param>
        /// <param name="token">Fixed token for the dev server; null = generate a random one.</param>
        public static async Task<ApiHost> StartAsync(int port = 0, string token = null)
        {
            var host = new ApiHost { Token = token ?? NewToken() };
            await host.RunAsync(port);
            return host;
        }

        /// <summary>192 random bits as hex.</summary>
        private static string NewToken()
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        }

        private async Task RunAsync(int port)
        {
            var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = webRoot,
            });

            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.AddServerHeader = false;
                o.Listen(IPAddress.Loopback, port);   // loopback only: unreachable from other computers
            });
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
            });

            var app = builder.Build();
            _app = app;

            app.Use(Guard);

            // The UI files change with every release; "no-cache" makes the browser re-validate them instead of
            // showing a stale copy after an update.
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
            });

            MapRoutes(app);

            await app.StartAsync();
            var address = app.Urls.First();
            Port = new Uri(address).Port;
        }

        // ------------------------------------------------------------------ Protection & error handling

        /// <summary>
        /// Middleware in front of everything. Three protections, in this order:
        /// <list type="number">
        /// <item><b>Host check</b> (421): only 127.0.0.1 / localhost - defeats DNS-rebinding attacks, where a hostile
        /// web page makes the browser talk to this server under its own domain name.</item>
        /// <item><b>Token</b> (401) for /api, /img and /avatar - a page in another browser tab cannot know it.</item>
        /// <item><b>Origin check</b> (403) - a script from a foreign site cannot call the API even if it had the token.</item>
        /// </list>
        /// It also turns exceptions into clean JSON errors: <c>{ "error": message, "code": code }</c>.
        /// </summary>
        private async Task Guard(HttpContext ctx, Func<Task> next)
        {
            var host = ctx.Request.Host.Host;
            if (host != "127.0.0.1" && host != "localhost")
            {
                ctx.Response.StatusCode = 421;
                return;
            }

            var path = ctx.Request.Path;
            bool protectedPath = path.StartsWithSegments("/api") || path.StartsWithSegments("/img") || path.StartsWithSegments("/avatar");
            if (protectedPath)
            {
                // The query-string form exists for <img src> tags, which cannot send headers. Compared in constant time.
                var supplied = ctx.Request.Headers["X-SAM-Token"].FirstOrDefault() ?? ctx.Request.Query["t"].FirstOrDefault() ?? "";
                if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(Token)) == false)
                {
                    ctx.Response.StatusCode = 401;
                    return;
                }

                // A browser script from a foreign page must never reach this interface
                var origin = ctx.Request.Headers.Origin.FirstOrDefault();
                if (string.IsNullOrEmpty(origin) == false && new Uri(origin).Host != host)
                {
                    ctx.Response.StatusCode = 403;
                    return;
                }
            }

            if (path.StartsWithSegments("/api"))
            {
                ctx.Response.Headers.CacheControl = "no-store";
            }

            try
            {
                await next();
            }
            catch (ApiException ex)
            {
                await WriteError(ctx, ex.Status, ex.Code, ex.Message);
            }
            catch (WorkerException ex)
            {
                // 502: the problem is on Steam's side of the worker, not in the request
                int status = ex.Code is "steam-not-running" ? 409 : 502;
                await WriteError(ctx, status, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                await WriteError(ctx, 500, "exception", ex.Message);
            }
        }

        private static Task WriteError(HttpContext ctx, int status, string code, string message)
        {
            if (ctx.Response.HasStarted) return Task.CompletedTask;
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            return ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = message, code }, Json.Options));
        }

        /// <summary>Passes a worker's JSON answer through to the browser unchanged.</summary>
        private static IResult Raw(JsonNode node) => Results.Content(node?.ToJsonString() ?? "null", "application/json; charset=utf-8");

        // ------------------------------------------------------------------ Routes

        /// <summary>
        /// The whole HTTP API. Conventions: GET reads, POST changes something; failures are thrown as
        /// <see cref="ApiException"/> and rendered by <see cref="Guard"/>; anything that talks to a game takes the
        /// Steam app id in the path. Long jobs (scan, auto-unlock, friends' progress) are started with POST .../start and
        /// observed with GET .../status?cursor=N (the UI polls it).
        /// </summary>
        private void MapRoutes(IEndpointRouteBuilder app)
        {
            // ---- Status: Steam installed/running/signed in, saved accounts, progress of an account switch
            app.MapGet("/api/status", () =>
            {
                var status = SteamEnv.GetStatus();
                return Results.Json(new
                {
                    version = "8.0.0",
                    steam = new
                    {
                        installed = status.Installed,
                        running = status.Running,
                        loggedIn = status.LoggedIn,
                        activeAccountId = status.ActiveAccountId,
                        runningAppId = status.RunningAppId,
                    },
                    accounts = status.Installed ? SteamEnv.GetAccounts(status.ActiveAccountId) : new List<SteamAccount>(),
                    @switch = _switcher.Current,
                }, Json.Options);
            });

            // ---- Accounts. Switching restarts Steam, so every Steam connection of ours is released first.
            app.MapPost("/api/accounts/switch", async (JsonObject body) =>
            {
                var id = body?["steamId"]?.GetValue<string>() ?? throw new ApiException(400, "bad-request", "steamId is missing");
                if (_auto.IsRunning) throw new ApiException(409, "auto-running", "An auto-unlock run is in progress. Please stop it before switching accounts.");
                await _sessions.CloseAsync();
                _scan.Cancel();
                _friendScan.Cancel();
                _switcher.Start(id);
                return Results.Json(_switcher.Current, Json.Options);
            });

            app.MapGet("/api/accounts/switch", () => Results.Json(_switcher.Current, Json.Options));

            app.MapPost("/api/steam/start", () =>
            {
                var status = SteamEnv.GetStatus();
                if (status.Installed == false) throw new ApiException(409, "steam-missing", "Steam was not found.");
                if (status.Running) throw new ApiException(409, "already-running", "Steam is already running.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SteamEnv.SteamExe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = SteamEnv.InstallPath,
                });
                return Results.Json(new { ok = true }, Json.Options);
            });

            // ---- Library
            app.MapGet("/api/library", async (bool? refresh) =>
                Results.Json(await _library.GetAsync(refresh == true), Json.Options));

            // ---- Deep scan
            app.MapPost("/api/scan/start", async (JsonObject body) =>
            {
                var (_, accountId) = LibraryService.RequireSteam();
                var ids = (body?["ids"] as JsonArray)?.Select(n => n.GetValue<uint>()).Distinct().ToList()
                    ?? throw new ApiException(400, "bad-request", "ids is missing");
                if (ids.Count == 0) throw new ApiException(400, "bad-request", "No games selected");

                RequireNoBatchExcept(_scan);
                await _sessions.CloseAsync();
                _scan.Start(accountId, ids);
                return Results.Json(_scan.Status(0), Json.Options);
            });

            app.MapGet("/api/scan/status", (int? cursor) => Results.Json(_scan.Status(cursor ?? 0), Json.Options));

            app.MapPost("/api/scan/cancel", () =>
            {
                _scan.Cancel();
                return Results.Json(new { ok = true }, Json.Options);
            });

            // ---- Auto-unlock (batch over several games)
            app.MapPost("/api/auto/start", async (JsonObject body) =>
            {
                var (_, accountId) = LibraryService.RequireSteam();
                RequireNoBatchExcept(_auto);

                // Changing achievements in bulk needs an explicit confirmation from the UI; locking needs a typed word on top.
                var mode = body?["mode"]?.GetValue<string>();
                if (mode is not ("unlock" or "lock")) throw new ApiException(400, "bad-request", "mode must be \"unlock\" or \"lock\"");
                if (body["confirm"]?.GetValue<bool>() != true) throw new ApiException(400, "not-confirmed", "The run was not confirmed.");
                if (mode == "lock" && body["confirmText"]?.GetValue<string>() != "LOCK") throw new ApiException(400, "not-confirmed", "Type LOCK to confirm locking achievements.");

                var ids = (body["ids"] as JsonArray)?.Select(n => n.GetValue<uint>()).Distinct().Take(5000).ToList()
                    ?? throw new ApiException(400, "bad-request", "ids is missing");
                if (ids.Count == 0) throw new ApiException(400, "bad-request", "No games selected");

                await _sessions.CloseAsync();
                _auto.Start(accountId, mode, ids);
                return Results.Json(_auto.Status(accountId, 0), Json.Options);
            });

            app.MapPost("/api/auto/undo", async () =>
            {
                var (_, accountId) = LibraryService.RequireSteam();
                RequireNoBatchExcept(_auto);
                await _sessions.CloseAsync();
                _auto.StartUndo(accountId);
                return Results.Json(_auto.Status(accountId, 0), Json.Options);
            });

            app.MapGet("/api/auto/status", (int? cursor) =>
            {
                var status = SteamEnv.GetStatus();
                return Results.Json(_auto.Status(status.ActiveAccountId, cursor ?? 0), Json.Options);
            });

            app.MapPost("/api/auto/cancel", () =>
            {
                _auto.Cancel();
                return Results.Json(new { ok = true }, Json.Options);
            });

            // ---- Single game
            app.MapPost("/api/game/{id:long}/open", async (long id) =>
            {
                RequireNoScan();
                var data = await _sessions.LoadAsync(CheckId(id));
                AttachNews(data, (uint)id);
                return Raw(data);
            });

            app.MapPost("/api/game/{id:long}/apply", async (long id, JsonObject body) =>
            {
                RequireNoScan();
                return Raw(await _sessions.ApplyAsync(CheckId(id), body ?? new JsonObject()));
            });

            app.MapPost("/api/game/{id:long}/reset", async (long id, JsonObject body) =>
            {
                RequireNoScan();
                bool withAchievements = body?["achievements"]?.GetValue<bool>() ?? false;
                return Raw(await _sessions.ResetAsync(CheckId(id), withAchievements));
            });

            app.MapPost("/api/game/{id:long}/close", async (long id) =>
            {
                await _sessions.CloseAsync(CheckId(id));
                return Results.Json(new { ok = true }, Json.Options);
            });

            app.MapGet("/api/game/{id:long}/rarity", async (long id) =>
                Results.Json(await _rarity.GetAsync(CheckId(id)), Json.Options));

            // ---- Friends comparison (opt-in, OFF by default)
            app.MapGet("/api/friends/settings", () =>
            {
                var (_, accountId) = LibraryService.RequireSteam();
                return Results.Json(FriendsStore.Get(accountId), Json.Options);
            });

            app.MapPost("/api/friends/settings", (JsonObject body) =>
            {
                var (_, accountId) = LibraryService.RequireSteam();
                bool enabled = body?["enabled"]?.GetValue<bool>() ?? false;
                bool auto = body?["auto"]?.GetValue<bool>() ?? false;
                var selected = (body?["selected"] as JsonArray)?.Select(n => n?.GetValue<string>()).Where(s => s != null).ToList();
                return Results.Json(FriendsStore.Set(accountId, enabled, auto, selected), Json.Options);
            });

            // What "Load friends' progress" found so far (counts only, selected friends only). Reading it queries nothing.
            app.MapGet("/api/friends/progress", () =>
            {
                var (_, accountId) = LibraryService.RequireSteam();
                var settings = FriendsStore.Get(accountId);
                var games = settings.Enabled ? FriendProgressStore.Snapshot(accountId, settings.Selected) : new();
                return Results.Json(new { enabled = settings.Enabled, selected = settings.Selected, games }, Json.Options);
            });

            app.MapPost("/api/friends/scan/start", async (JsonObject body) =>
            {
                var (_, accountId) = LibraryService.RequireSteam();
                RequireNoBatchExcept(_friendScan);
                // Friends are only ever queried after an explicit confirmation (you appear as "playing" to them meanwhile).
                if (body?["confirm"]?.GetValue<bool>() != true) throw new ApiException(400, "not-confirmed", "Loading the friends' progress was not confirmed.");

                var ids = (body["ids"] as JsonArray)?.Select(n => n.GetValue<uint>()).Distinct().Take(5000).ToList()
                    ?? throw new ApiException(400, "bad-request", "ids is missing");
                if (ids.Count == 0) throw new ApiException(400, "bad-request", "No games selected");

                await _sessions.CloseAsync();
                _friendScan.Start(accountId, ids, body["force"]?.GetValue<bool>() == true);
                return Results.Json(_friendScan.Status(0), Json.Options);
            });

            app.MapGet("/api/friends/scan/status", (int? cursor) => Results.Json(_friendScan.Status(cursor ?? 0), Json.Options));

            app.MapPost("/api/friends/scan/cancel", () =>
            {
                _friendScan.Cancel();
                return Results.Json(new { ok = true }, Json.Options);
            });

            app.MapGet("/api/friends/list", async () =>
            {
                var (_, accountId) = LibraryService.RequireSteam();
                FriendService.RequireEnabled(accountId);
                return Raw(await _friends.GetListAsync(accountId));
            });

            app.MapPost("/api/game/{id:long}/friends", async (long id, JsonObject body) =>
            {
                RequireNoScan();
                var (_, accountId) = LibraryService.RequireSteam();
                var settings = FriendService.RequireEnabled(accountId);

                var requested = (body?["steamIds"] as JsonArray)?.Select(n => n?.GetValue<string>()).Where(s => s != null).ToList()
                    ?? throw new ApiException(400, "bad-request", "steamIds is missing");

                // Only what the user has explicitly selected is ever queried.
                var notSelected = requested.Where(r => settings.Selected.Contains(r) == false).ToList();
                if (notSelected.Count > 0)
                {
                    throw new ApiException(403, "friend-not-selected", "This friend was not selected for the comparison.");
                }
                if (requested.Count == 0) throw new ApiException(400, "bad-request", "No friends specified");

                return Raw(await _sessions.FriendsAsync(CheckId(id), requested));
            });

            // ---- "New" markers and UI preferences
            app.MapPost("/api/seen", () =>
            {
                _library.AckAll();
                return Results.Json(new { ok = true }, Json.Options);
            });

            app.MapGet("/api/prefs", () => Results.Content(PrefsStore.ReadRaw(), "application/json; charset=utf-8"));

            app.MapPost("/api/prefs", (JsonObject body) =>
            {
                PrefsStore.WriteRaw(body?.ToJsonString() ?? "{}");
                return Results.Json(new { ok = true }, Json.Options);
            });

            // ---- Images (cover art from Steam's local cache / CDN) and avatars (from Steam's local avatar cache)
            app.MapGet("/img/{id:long}", async (long id, string kind) =>
            {
                var image = await _images.GetAsync(CheckId(id), kind ?? "header");
                if (image == null) return Results.NotFound();
                return Results.File(image.Value.path, image.Value.contentType, enableRangeProcessing: false,
                    lastModified: null, entityTag: null);
            });

            app.MapGet("/avatar/{steamId}", (string steamId) =>
            {
                if (ulong.TryParse(steamId, out _) == false) return Results.NotFound();
                var path = SteamEnv.AvatarPath(steamId);
                return path == null ? Results.NotFound() : Results.File(path, "image/png");
            });
        }

        /// <summary>
        /// Steam lets only ONE process act as a game per user, and all three batch jobs (scan, auto-unlock, friends'
        /// progress) start one game worker after another. So only one of them may run at a time - and a batch job may
        /// not start while another job is running. Pass the job that is about to start (it guards itself).
        /// </summary>
        private void RequireNoBatchExcept(object self)
        {
            if (ReferenceEquals(self, _scan) == false && _scan.IsRunning)
                throw new ApiException(409, "scan-running", "A progress check is running. Please wait for it to finish.");
            if (ReferenceEquals(self, _auto) == false && _auto.IsRunning)
                throw new ApiException(409, "auto-running", "An auto-unlock run is in progress. Please wait for it to finish.");
            if (ReferenceEquals(self, _friendScan) == false && _friendScan.IsRunning)
                throw new ApiException(409, "friend-scan-running", "Loading the friends' progress is running. Please wait for it to finish.");
        }

        /// <summary>A game cannot be opened (= become the active "game" in Steam) while any batch job is running.</summary>
        private void RequireNoScan()
        {
            if (_scan.IsRunning || _auto.IsRunning || _friendScan.IsRunning)
            {
                throw new ApiException(409, "batch-running", "No game can be opened while a scan, an auto-unlock run or the loading of friends' progress is in progress. Please stop it or wait for it to finish.");
            }
        }

        /// <summary>
        /// Opening a game counts as "seen": tell the UI what was new (for a hint on this visit), then clear the marker.
        /// </summary>
        private static void AttachNews(JsonNode data, uint appId)
        {
            try
            {
                if (data is not JsonObject obj || ulong.TryParse(obj["steamId"]?.GetValue<string>(), out var id64) == false) return;

                var accountId = SteamEnv.ToAccountId(id64);
                int total = obj["summary"]["total"].GetValue<int>();
                var seen = SeenStore.Load(accountId);
                var key = appId.ToString(System.Globalization.CultureInfo.InvariantCulture);

                bool isNew = seen.Initialized && seen.Known.ContainsKey(key) == false;
                int newAch = 0;
                if (seen.Initialized && seen.Known.TryGetValue(key, out var baseline) && baseline != null && total > baseline)
                {
                    newAch = total - baseline.Value;
                }

                obj["news"] = new JsonObject { ["isNew"] = isNew, ["newAch"] = newAch };
                SeenStore.Ack(accountId, appId, total);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Route parameters arrive as long; Steam app ids are positive 32-bit numbers.</summary>
        private static uint CheckId(long id)
        {
            if (id <= 0 || id > uint.MaxValue) throw new ApiException(400, "bad-request", "Invalid app ID");
            return (uint)id;
        }

        public async ValueTask DisposeAsync()
        {
            WorkerProcess.KillAll();
            if (_app != null)
            {
                try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
                await _app.DisposeAsync();
            }
        }
    }
}
