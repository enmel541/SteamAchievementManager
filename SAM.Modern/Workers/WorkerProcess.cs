using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace SamModern.Workers
{
    /// <summary>
    /// A failure reported by (or about) a worker. <see cref="Code"/> is machine-readable: our own codes ("timeout",
    /// "exited", "no-schema", "steam-error", ...) or the name of the <c>ClientInitializeFailure</c> when connecting to
    /// Steam failed ("ConnectToGlobalUser", "CreateSteamPipe", ...).
    /// </summary>
    public sealed class WorkerException : Exception
    {
        public string Code { get; }
        public WorkerException(string code, string message) : base(message) { Code = code; }
    }

    /// <summary>
    /// Parent side of a worker child process (see <see cref="WorkerMain"/>): starts the process, sends commands as one JSON
    /// line each and matches the answers by id. Every pending call is failed if the process dies, so callers never hang.
    /// Dispose it to end the process; Steam stops showing "playing" at that moment.
    /// </summary>
    /// <remarks>
    /// Wire format. Parent -> worker: <c>{"id":1,"cmd":"load", ...payload}</c>.
    /// Worker -> parent: <c>{"evt":"ready"}</c> once connected, <c>{"evt":"fatal",...}</c> if connecting failed, and for
    /// every command <c>{"id":1,"ok":true,"data":...}</c> or <c>{"id":1,"ok":false,"error":"...","code":"..."}</c>.
    /// </remarks>
    public sealed class WorkerProcess : IDisposable
    {
        /// <summary>All running workers by process id, so they can all be stopped on exit or account switch.</summary>
        private static readonly ConcurrentDictionary<int, WorkerProcess> Alive = new();

        private readonly Process _process;
        /// <summary>Completes when the worker reports "ready" (connected to Steam), fails if it reports "fatal" or exits.</summary>
        private readonly TaskCompletionSource<JsonNode> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
        private readonly StringBuilder _stderr = new();
        private int _nextId;
        private bool _disposed;

        public string Mode { get; }
        public uint AppId { get; }

        private WorkerProcess(string mode, uint appId)
        {
            Mode = mode;
            AppId = appId;

            // The worker is this very executable started with "--api <mode> [appId]" (see Program.Main).
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("--api");
            psi.ArgumentList.Add(mode);
            if (mode == "game") psi.ArgumentList.Add(appId.ToString());

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) { if (_stderr.Length < 4000) _stderr.AppendLine(e.Data); } };
            _process.Exited += (_, _) => FailAll(new WorkerException("exited", "The Steam worker exited unexpectedly" + StderrSuffix()));
            _process.Start();
            _process.BeginErrorReadLine();
            Alive[_process.Id] = this;
            _ = Task.Run(ReadLoop);
        }

        /// <summary>The worker's stderr (capped at 4 KB) as an error-message suffix, to make crashes diagnosable.</summary>
        private string StderrSuffix()
        {
            lock (_stderr)
            {
                return _stderr.Length == 0 ? "" : ": " + _stderr.ToString().Trim();
            }
        }

        /// <param name="mode">"library" (no game; list games, friends, DLC ownership) or "game" (acts as <paramref name="appId"/>).</param>
        public static WorkerProcess Start(string mode, uint appId = 0) => new(mode, appId);

        /// <summary>Stops all workers that are still running (e.g. on close or before an account switch).</summary>
        public static void KillAll()
        {
            foreach (var w in Alive.Values.ToList())
            {
                w.Dispose();
            }
        }

        /// <summary>Reads the worker's stdout line by line and routes each message (ready / fatal / answer to a command).</summary>
        private async Task ReadLoop()
        {
            try
            {
                string line;
                while ((line = await _process.StandardOutput.ReadLineAsync()) != null)
                {
                    JsonNode node;
                    try { node = JsonNode.Parse(line); } catch (Exception) { continue; }
                    if (node is not JsonObject obj) continue;

                    var evt = obj["evt"]?.GetValue<string>();
                    if (evt == "ready")
                    {
                        _ready.TrySetResult(obj);
                    }
                    else if (evt == "fatal")
                    {
                        var ex = new WorkerException(
                            obj["code"]?.GetValue<string>() ?? "fatal",
                            obj["error"]?.GetValue<string>() ?? "Connection to Steam failed");
                        FailAll(ex);
                    }
                    else if (obj["id"] != null && _pending.TryRemove(obj["id"].GetValue<int>(), out var tcs))
                    {
                        if (obj["ok"]?.GetValue<bool>() == true)
                        {
                            tcs.TrySetResult(obj["data"]);
                        }
                        else
                        {
                            tcs.TrySetException(new WorkerException(
                                obj["code"]?.GetValue<string>() ?? "error",
                                obj["error"]?.GetValue<string>() ?? "Unknown error"));
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            FailAll(new WorkerException("exited", "The Steam worker has exited" + StderrSuffix()));
        }

        /// <summary>Fails the "ready" promise and every pending command with the same error (the worker is gone or unusable).</summary>
        private void FailAll(Exception ex)
        {
            _ready.TrySetException(ex);
            foreach (var key in _pending.Keys.ToList())
            {
                if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(ex);
            }
        }

        public Task WaitReadyAsync(int timeoutMs = 15000) => WithTimeout(_ready.Task, timeoutMs, "Timed out while connecting to Steam");

        /// <summary>
        /// Sends one command and waits for its answer (the <c>data</c> of the worker's reply).
        /// Throws <see cref="WorkerException"/> with code "timeout" if Steam does not answer in time.
        /// </summary>
        public async Task<JsonNode> SendAsync(string cmd, JsonObject payload = null, int timeoutMs = 30000)
        {
            await WaitReadyAsync();

            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var message = payload ?? new JsonObject();
            message["id"] = id;
            message["cmd"] = cmd;

            try
            {
                await _process.StandardInput.WriteLineAsync(message.ToJsonString());
                await _process.StandardInput.FlushAsync();
            }
            catch (Exception)
            {
                _pending.TryRemove(id, out _);
                throw new WorkerException("exited", "The Steam worker is no longer reachable" + StderrSuffix());
            }

            try
            {
                return await WithTimeout(tcs.Task, timeoutMs, "Timed out - Steam did not respond");
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private static async Task<T> WithTimeout<T>(Task<T> task, int timeoutMs, string message)
        {
            var winner = await Task.WhenAny(task, Task.Delay(timeoutMs));
            if (winner != task) throw new WorkerException("timeout", message);
            return await task;
        }

        /// <summary>Ends the worker politely (closing stdin makes it quit), and kills it if it has not exited after 1.5 s.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { Alive.TryRemove(_process.Id, out _); } catch (Exception) { }
            try
            {
                if (_process.HasExited == false)
                {
                    try { _process.StandardInput.Close(); } catch (Exception) { }
                    if (_process.WaitForExit(1500) == false)
                    {
                        _process.Kill(true);
                    }
                }
            }
            catch (Exception)
            {
            }
            try { _process.Dispose(); } catch (Exception) { }
        }
    }
}
