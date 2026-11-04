using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FreeSync.Internal;
using FreeSync.Json;

namespace FreeSync
{
    /// <summary>
    /// A connection to one FreeSync room: a realtime JSON document shared by every client in the room.
    /// </summary>
    /// <example>
    /// <code>
    /// var db = new FreeSyncClient("wss://my-worker.example.workers.dev", room: "global", key: "public-read-key");
    /// var scores = db.Ref("scores");
    /// scores.OnValue(snapshot => Console.WriteLine(snapshot.ToJson()));
    /// await scores.Child("player1").SetAsync(42);
    /// </code>
    /// </example>
    /// <remarks>
    /// All members are thread-safe. Listener callbacks run according to <see cref="FreeSyncOptions.EventDispatch"/>;
    /// with <see cref="EventDispatchMode.Queued"/> call <see cref="Dispatch"/> regularly (e.g. from Unity's <c>Update()</c>).
    /// </remarks>
    public sealed class FreeSyncClient : IDisposable
    {
        private const int ProtocolVersion = 1;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private readonly object _gate = new object();
        private readonly FreeSyncOptions _opts;
        private readonly EventDispatcher _events;
        private readonly Uri _wsUri;
        private readonly Uri _httpBase;
        private readonly string _room;
        private readonly string _key;
        private readonly Random _random = new Random();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writerWake = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _connectWake = new SemaphoreSlim(0);
        /// <summary>User callbacks collected under the lock; delivered through the dispatcher.</summary>
        private readonly List<Action> _outbox = new List<Action>();
        /// <summary>Task completions collected under the lock; run directly, never through the dispatcher,
        /// so awaiting a write never depends on <see cref="Dispatch"/> being called.</summary>
        private readonly List<Action> _completions = new List<Action>();

        private readonly Dictionary<string, Subscription> _subs = new Dictionary<string, Subscription>();
        private readonly Dictionary<long, string> _subRequests = new Dictionary<long, string>();
        private readonly List<PendingWrite> _pending = new List<PendingWrite>();
        private readonly List<OdRegistration> _od = new List<OdRegistration>();
        private readonly Dictionary<long, OdRegistration> _odRequests = new Dictionary<long, OdRegistration>();
        private readonly Dictionary<long, TaskCompletionSource<bool>> _cancelRequests = new Dictionary<long, TaskCompletionSource<bool>>();
        private readonly Dictionary<string, (string ETag, object? Value, long Rev)> _httpCache = new Dictionary<string, (string, object?, long)>();
        private readonly DiskCache? _disk;

        private object? _serverTree;
        private long _socketRev;
        private long _nextId = 1;
        private ISocket? _socket;
        private bool _ready;
        /// <summary>The post-welcome resync frame has been sent, so queued writes may follow it.</summary>
        private bool _resynced;
        private bool _everConnected;
        private DateTime _connectedAt;
        private ConnectionState _state;
        private bool _online;
        private bool _disposed;
        private DateTime? _writesBlockedUntil;
        private DateTime _firstUnsentAt;
        private DateTime _holdWritesUntil;
        private DateTime _lastDiskSave;
        private int _maxOps = 100;
        private int _maxFrame = 64 * 1024;
        private Task? _connectionLoop;
        private Task? _writerLoop;

        /// <summary>Creates a client for one room. Connects in the background unless <see cref="FreeSyncOptions.AutoConnect"/> is false.</summary>
        /// <param name="url">The Worker URL, e.g. <c>wss://freesync.you.workers.dev</c> (http/https are accepted too).</param>
        /// <param name="room">Room name: letters, digits, '-' and '_' (up to 64 characters by default).</param>
        /// <param name="key">The public read key (safe to ship in a mod) or the private write key (admin tools only).</param>
        /// <param name="options">Optional settings.</param>
        public FreeSyncClient(string url, string room, string key, FreeSyncOptions? options = null)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("url is required", nameof(url));
            if (string.IsNullOrEmpty(room)) throw new ArgumentException("room is required", nameof(room));
            _opts = options ?? new FreeSyncOptions();
            _room = room;
            _key = key ?? "";
            var baseUri = new Uri(url.TrimEnd('/') + "/");
            var wsScheme = baseUri.Scheme == "http" || baseUri.Scheme == "ws" ? "ws" : "wss";
            var httpScheme = wsScheme == "ws" ? "http" : "https";
            var roomPath = "db/" + Uri.EscapeDataString(room);
            _wsUri = new UriBuilder(baseUri) { Scheme = wsScheme, Path = baseUri.AbsolutePath + roomPath, Query = "v=" + ProtocolVersion }.Uri;
            _httpBase = new UriBuilder(baseUri) { Scheme = httpScheme, Path = baseUri.AbsolutePath + roomPath }.Uri;
            _events = new EventDispatcher(_opts.EventDispatch, Log);
            if (_opts.ClientToken != null) ClientId = Ids.ClientIdFor(room, _opts.ClientToken);
            if (_opts.CacheDirectory != null) _disk = new DiskCache(_opts.CacheDirectory, baseUri.Host, room, Log);
            _online = _opts.AutoConnect;
            _state = _online ? ConnectionState.Connecting : ConnectionState.Offline;
            _connectionLoop = Task.Run(ConnectionLoop);
            _writerLoop = Task.Run(WriterLoop);
        }

        // ---- public surface ------------------------------------------------------------------

        /// <summary>
        /// This client's id, used by rules such as <c>players/$cid</c>. Known immediately when a
        /// <see cref="FreeSyncOptions.ClientToken"/> is set; otherwise assigned by the server on connect.
        /// </summary>
        public string? ClientId { get; private set; }

        /// <summary>The role granted by the key ("public" or "admin"), once connected.</summary>
        public string? Role { get; private set; }

        /// <summary>The current connection state.</summary>
        public ConnectionState State
        {
            get { lock (_gate) return _state; }
        }

        /// <summary>
        /// When the server reported that the daily write quota is used up, the UTC time writes are expected to work again.
        /// Writes fail fast with <see cref="FreeSyncException.IsQuota"/> until then. Reads keep working.
        /// </summary>
        public DateTime? WritesBlockedUntil
        {
            get { lock (_gate) return _writesBlockedUntil > Now() ? _writesBlockedUntil : null; }
        }

        /// <summary>The last error that was not tied to a specific operation.</summary>
        public FreeSyncException? LastError { get; private set; }

        /// <summary>Number of writes not yet confirmed by the server.</summary>
        public int PendingWriteCount
        {
            get { lock (_gate) return _pending.Count; }
        }

        /// <summary>Raised when <see cref="State"/> changes. Runs like listener callbacks (see <see cref="FreeSyncOptions.EventDispatch"/>).</summary>
        public event Action<ConnectionState>? StateChanged;

        /// <summary>Raised for errors that are not tied to an operation you can await (e.g. a refused frame).</summary>
        public event Action<FreeSyncException>? ErrorOccurred;

        /// <summary>A reference to a location in the document. "" or "/" is the root.</summary>
        public DatabaseReference Ref(string path = "") => new DatabaseReference(this, Paths.Parse(path));

        /// <summary>
        /// Runs queued listener callbacks on the calling thread. Only needed with <see cref="EventDispatchMode.Queued"/>;
        /// call it from Unity's <c>Update()</c> or your game loop. Returns how many callbacks ran.
        /// </summary>
        public int Dispatch(int maxCallbacks = int.MaxValue) => _events.Drain(maxCallbacks);

        /// <summary>Disconnects and stops reconnecting until <see cref="GoOnline"/>. Data and listeners are kept.</summary>
        public void GoOffline()
        {
            ISocket? s;
            lock (_gate)
            {
                _online = false;
                s = _socket;
                SetState(ConnectionState.Offline);
            }
            FlushOutbox();
            if (s != null) _ = CloseQuietly(s);
            _connectWake.Release();
        }

        /// <summary>Connects again after <see cref="GoOffline"/>, or retries immediately while waiting to reconnect.</summary>
        public void GoOnline()
        {
            lock (_gate) _online = true;
            _connectWake.Release();
        }

        /// <summary>Loads a client token from <paramref name="filePath"/>, creating the file with a new random token if needed.</summary>
        public static string LoadOrCreateToken(string filePath)
        {
            if (File.Exists(filePath))
            {
                var existing = File.ReadAllText(filePath).Trim();
                if (existing.Length >= 16) return existing;
            }
            var token = Ids.NewToken();
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, token);
            return token;
        }

        /// <summary>Closes the connection gracefully (the server then runs onDisconnect writes) and stops the client.</summary>
        public async Task CloseAsync()
        {
            ISocket? s;
            List<PendingWrite> pending;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _online = false;
                s = _socket;
                pending = _pending.ToList();
                _pending.Clear();
                SetState(ConnectionState.Offline);
            }
            foreach (var w in pending) w.Fail(new FreeSyncException("disposed", "the client was closed before the write was confirmed"));
            SaveDisk(force: true);
            FlushOutbox();
            if (s != null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await s.CloseAsync(1000, "bye", timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Best effort.
                }
            }
            _cts.Cancel();
            _connectWake.Release();
            _writerWake.Release();
        }

        /// <summary>Same as <see cref="CloseAsync"/>, waiting at most two seconds.</summary>
        public void Dispose()
        {
            try
            {
                Task.Run(CloseAsync).Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Best effort.
            }
        }

        // ---- used by DatabaseReference ----------------------------------------------------------

        internal DateTime UtcNow() => Now();

        private DateTime Now() => _opts.UtcNow?.Invoke() ?? DateTime.UtcNow;

        internal object? ToTree(object? value)
        {
            var tree = _opts.Serializer is BuiltInJsonSerializer
                ? BuiltInJsonSerializer.ToTree(value)
                : FsJson.Parse(_opts.Serializer.Serialize(value));
            return Tree.Normalize(tree);
        }

        internal Dictionary<string, object?> UpdateTree(string[] basePath, IDictionary<string, object?> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var result = new Dictionary<string, object?>();
            var parsed = new List<string[]>();
            foreach (var kv in values)
            {
                var rel = Paths.Parse(kv.Key);
                if (rel.Length == 0) throw new ArgumentException("update keys must be non-empty paths");
                Paths.Concat(basePath, rel); // depth check
                foreach (var other in parsed)
                    if (Paths.Overlaps(other, rel)) throw new ArgumentException($"update paths \"{Paths.Join(other)}\" and \"{kv.Key}\" overlap");
                parsed.Add(rel);
                result[Paths.Join(rel)] = ToTree(kv.Value);
            }
            return result;
        }

        internal Task<long> Write(string type, string[] path, object? value)
        {
            var w = new PendingWrite(type, path, value);
            lock (_gate)
            {
                FreeSyncException? refusal = null;
                if (_disposed) refusal = new FreeSyncException("disposed", "the client is closed");
                else if (_writesBlockedUntil > Now())
                    refusal = new FreeSyncException("write_quota", $"daily write quota used up until {_writesBlockedUntil:u}");
                else if (_pending.Count >= _opts.MaxQueuedWrites)
                    refusal = new FreeSyncException("queue_full", "too many writes waiting to be sent");
                else if (FsJson.Serialize(w.ToMessage()).Length > _maxFrame)
                    refusal = new FreeSyncException("frame_too_large", "the value is larger than the server's frame limit");
                if (refusal != null)
                {
                    w.Fail(refusal);
                    return w.Task;
                }

                w.Id = _nextId++;
                if (type != "upd")
                {
                    // A set/remove supersedes unsent writes at or below the same path.
                    for (var i = _pending.Count - 1; i >= 0; i--)
                    {
                        var old = _pending[i];
                        if (old.Sent || !Paths.IsPrefix(path, old.Path)) continue;
                        _pending.RemoveAt(i);
                        w.Absorb(old);
                    }
                }
                if (!_pending.Any(p => !p.Sent)) _firstUnsentAt = Now();
                _pending.Add(w);
                Notify();
            }
            FlushOutbox();
            _writerWake.Release();
            return w.Task;
        }

        internal IDisposable Listen(string[] path, Action<DataSnapshot> onValue, Action<FreeSyncException>? onError)
        {
            if (onValue == null) throw new ArgumentNullException(nameof(onValue));
            var key = Paths.Join(path);
            var listener = new Listener(onValue, onError);
            Subscription sub;
            Dictionary<string, object?>? msg = null;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(FreeSyncClient));
                if (!_subs.TryGetValue(key, out sub!))
                {
                    sub = new Subscription(path);
                    _subs[key] = sub;
                    if (_disk != null && !_subs.Values.Any(s => s != sub && s.Live && Paths.Overlaps(s.Path, path)) &&
                        _disk.TryGet(key, out var rev, out var cached))
                    {
                        _serverTree = Tree.SetIn(_serverTree, path, cached);
                        sub.HasData = true;
                        sub.Rev = rev;
                    }
                    if (_ready) msg = SubMessage(sub);
                }
                sub.Listeners.Add(listener);
                if (sub.HasData)
                {
                    var snap = SnapshotFor(sub, View());
                    sub.LastValue = snap.Value;
                    sub.Delivered = true;
                    _outbox.Add(() => listener.OnValue(snap));
                }
            }
            FlushOutbox();
            if (msg != null) _ = SendFrame(msg);
            return new Unsubscriber(() => Unlisten(sub, listener));
        }

        private void Unlisten(Subscription sub, Listener listener)
        {
            Dictionary<string, object?>? msg = null;
            lock (_gate)
            {
                if (!sub.Listeners.Remove(listener) || sub.Listeners.Count > 0) return;
                var key = Paths.Join(sub.Path);
                if (!_subs.TryGetValue(key, out var current) || current != sub) return;
                _subs.Remove(key);
                if (sub.HasData) _disk?.Put(key, sub.Live ? _socketRev : sub.Rev, Tree.GetIn(_serverTree, sub.Path));
                if (!_subs.Values.Any(s => Paths.Overlaps(s.Path, sub.Path))) _serverTree = Tree.SetIn(_serverTree, sub.Path, null);
                if (_ready && !_disposed) msg = new Dictionary<string, object?> { ["t"] = "unsub", ["p"] = key };
            }
            if (msg != null) _ = SendFrame(msg);
        }

        internal async Task<DataSnapshot> Get(string[] path)
        {
            var key = Paths.Join(path);
            (string ETag, object? Value, long Rev) cached;
            bool haveCached;
            lock (_gate)
            {
                var live = _subs.Values.FirstOrDefault(s => s.Live && Paths.IsPrefix(s.Path, path));
                if (live != null) return new DataSnapshot(path, Tree.GetIn(View(), path), _socketRev, false, _opts.Serializer);
                haveCached = _httpCache.TryGetValue(key, out cached);
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, _httpBase + "/snapshot?path=" + Uri.EscapeDataString(key));
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
            if (haveCached) req.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            HttpResponseMessage res;
            try
            {
                res = await Http.SendAsync(req, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (!(e is OperationCanceledException) || !_cts.IsCancellationRequested)
            {
                return Fallback(path, "offline", "could not reach the server: " + e.Message);
            }
            using (res)
            {
                var status = (int)res.StatusCode;
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                long rev = 0;
                if (res.Headers.TryGetValues("X-FreeSync-Rev", out var revs)) long.TryParse(revs.FirstOrDefault(), out rev);
                if (status == 304 && haveCached) return Overlay(path, cached.Value, Math.Max(rev, cached.Rev), false);
                if (status == 200)
                {
                    var value = FsJson.Parse(body);
                    var etag = res.Headers.ETag?.ToString();
                    lock (_gate)
                    {
                        if (etag != null) _httpCache[key] = (etag, value, rev);
                    }
                    return Overlay(path, value, rev, false);
                }
                var code = status == 429 || status == 503 ? "quota" : "http_" + status;
                return Fallback(path, code, $"snapshot request failed with HTTP {status}: {body}");
            }
        }

        private DataSnapshot Overlay(string[] path, object? value, long rev, bool fromCache)
        {
            lock (_gate)
            {
                var tree = ApplyPending(Tree.SetIn(null, path, value));
                return new DataSnapshot(path, Tree.GetIn(tree, path), rev, fromCache, _opts.Serializer);
            }
        }

        private DataSnapshot Fallback(string[] path, string code, string message)
        {
            var key = Paths.Join(path);
            lock (_gate)
            {
                if (_httpCache.TryGetValue(key, out var c)) return Overlay(path, c.Value, c.Rev, true);
                var sub = _subs.Values.FirstOrDefault(s => s.HasData && Paths.IsPrefix(s.Path, path));
                if (sub != null) return new DataSnapshot(path, Tree.GetIn(View(), path), sub.Rev, true, _opts.Serializer);
                if (_disk != null && _disk.TryGet(key, out var rev, out var value)) return Overlay(path, value, rev, true);
            }
            throw new FreeSyncException(code, message);
        }

        internal Task RegisterOnDisconnect(string op, string[] path, object? value)
        {
            Dictionary<string, object?>? msg = null;
            Task task;
            lock (_gate)
            {
                if (_disposed) return Task.FromException(new FreeSyncException("disposed", "the client is closed"));
                if (op == "cancel")
                {
                    _od.RemoveAll(r => Paths.IsPrefix(path, r.Path));
                    if (!_ready) return Task.CompletedTask; // the server forgot the old connection's ops already
                    var id = _nextId++;
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _cancelRequests[id] = tcs;
                    msg = OdMessage(id, "cancel", path, null);
                    task = tcs.Task;
                }
                else
                {
                    var reg = new OdRegistration(op, path, value);
                    _od.Add(reg);
                    task = reg.Waiter.Task;
                    if (_ready)
                    {
                        var id = _nextId++;
                        _odRequests[id] = reg;
                        msg = OdMessage(id, op, path, value);
                    }
                }
            }
            if (msg != null) _ = SendFrame(msg);
            return task;
        }

        // ---- connection -------------------------------------------------------------------------

        private async Task ConnectionLoop()
        {
            var attempt = 0;
            var unavailableStreak = 0;
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                bool online;
                lock (_gate) online = _online;
                if (!online)
                {
                    await WaitConnectWake(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                    continue;
                }

                lock (_gate)
                {
                    if (_state != ConnectionState.QuotaExceeded)
                        SetState(_everConnected ? ConnectionState.Reconnecting : ConnectionState.Connecting);
                }
                FlushOutbox();

                var socket = _opts.SocketFactory?.Invoke() ?? new ClientWebSocketSocket();
                int? httpStatus = null;
                int? closeCode = null;
                var welcomed = false;
                try
                {
                    var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + _key };
                    if (_opts.ClientToken != null) headers["X-FreeSync-Token"] = _opts.ClientToken;
                    await socket.ConnectAsync(_wsUri, headers, _opts.KeepAliveInterval, ct).ConfigureAwait(false);
                    lock (_gate) _socket = socket;
                    await ReceiveLoop(socket, ct).ConfigureAwait(false);
                    closeCode = socket.CloseStatus;
                }
                catch (ConnectException e)
                {
                    httpStatus = e.HttpStatus;
                    Log($"connect failed (HTTP {e.HttpStatus?.ToString() ?? "?"}): {e.Message}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    Log("connection error: " + e.Message);
                }
                finally
                {
                    welcomed = OnSocketClosed(socket);
                    socket.Dispose();
                }
                if (ct.IsCancellationRequested) break;

                TimeSpan delay;
                lock (_gate)
                {
                    if (welcomed && Now() - _connectedAt > TimeSpan.FromSeconds(30)) attempt = 0;
                    if (welcomed) unavailableStreak = 0;
                    if (httpStatus == 503) unavailableStreak++;

                    if (!_online)
                    {
                        SetState(ConnectionState.Offline);
                        delay = TimeSpan.Zero;
                    }
                    else if (httpStatus == 426 || closeCode == 4400)
                    {
                        LastError = new FreeSyncException("unsupported_version", "the server does not speak this client's protocol version; update the client");
                        _online = false;
                        SetState(ConnectionState.Offline);
                        RaiseError(LastError);
                        delay = TimeSpan.Zero;
                    }
                    else if (httpStatus == 429 || closeCode == 4029 || unavailableStreak >= 3)
                    {
                        LastError = new FreeSyncException("quota", "the server's daily quota is used up; retrying after 00:00 UTC");
                        SetState(ConnectionState.QuotaExceeded);
                        delay = QuotaDelay();
                    }
                    else
                    {
                        attempt++;
                        delay = Backoff(attempt);
                        if (closeCode == 4008) delay = Max(delay, TimeSpan.FromSeconds(30));
                        if (httpStatus == 401 || httpStatus == 403 || closeCode == 4001)
                        {
                            LastError = new FreeSyncException("http_" + (httpStatus ?? 401), "the server refused the key");
                            RaiseError(LastError);
                            delay = _opts.ReconnectMaxDelay;
                        }
                        SetState(ConnectionState.Reconnecting);
                    }
                }
                FlushOutbox();
                Log($"reconnecting in {delay.TotalSeconds:0.#}s");
                if (delay > TimeSpan.Zero) await WaitConnectWake(delay).ConfigureAwait(false);
            }
        }

        private async Task WaitConnectWake(TimeSpan delay)
        {
            try
            {
                await _connectWake.WaitAsync(delay, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

        /// <summary>Exponential backoff with "equal jitter": between half and all of the doubled delay.</summary>
        internal TimeSpan Backoff(int attempt)
        {
            var max = _opts.ReconnectMaxDelay.TotalMilliseconds;
            var baseMs = Math.Min(max, _opts.ReconnectMinDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30)));
            double r;
            lock (_random) r = _random.NextDouble();
            return TimeSpan.FromMilliseconds(baseMs / 2 + r * baseMs / 2);
        }

        /// <summary>Until the next UTC midnight (plus up to 15 minutes of jitter), or the probe interval if shorter.</summary>
        internal TimeSpan QuotaDelay()
        {
            var now = Now();
            double r;
            lock (_random) r = _random.NextDouble();
            var untilReset = now.Date.AddDays(1) - now + TimeSpan.FromMinutes(15 * r);
            if (_opts.QuotaProbeInterval is TimeSpan probe && probe < untilReset)
                return TimeSpan.FromMilliseconds(probe.TotalMilliseconds * (0.8 + 0.4 * r));
            return untilReset;
        }

        private async Task ReceiveLoop(ISocket socket, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var text = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                if (text == null) return;
                object? parsed;
                try
                {
                    parsed = FsJson.Parse(text);
                }
                catch (FormatException e)
                {
                    Log("ignoring malformed frame: " + e.Message);
                    continue;
                }
                List<Dictionary<string, object?>>? toSend = null;
                lock (_gate)
                {
                    if (parsed is List<object?> list)
                    {
                        foreach (var item in list)
                            if (item is Dictionary<string, object?> m) Handle(socket, m, ref toSend);
                    }
                    else if (parsed is Dictionary<string, object?> m) Handle(socket, m, ref toSend);
                }
                FlushOutbox();
                if (toSend != null)
                {
                    await SendFrame(toSend.Cast<object?>().ToList(), socket).ConfigureAwait(false);
                    lock (_gate) _resynced = true;
                }
                _writerWake.Release();
            }
        }

        private void Handle(ISocket socket, Dictionary<string, object?> m, ref List<Dictionary<string, object?>>? toSend)
        {
            var type = m.TryGetValue("t", out var t) ? t as string : null;
            long? id = m.TryGetValue("id", out var idObj) && idObj is long l ? l : (long?)null;
            switch (type)
            {
                case "welcome":
                    OnWelcome(socket, m, ref toSend);
                    break;
                case "snap":
                    OnSnap(m, id);
                    break;
                case "patch":
                    OnPatch(m);
                    break;
                case "ack":
                    OnAck(id, m.TryGetValue("rev", out var r) && r is long rev ? rev : _socketRev);
                    break;
                case "err":
                    OnErr(id, m);
                    break;
            }
        }

        private void OnWelcome(ISocket socket, Dictionary<string, object?> m, ref List<Dictionary<string, object?>>? toSend)
        {
            _ready = true;
            _everConnected = true;
            _connectedAt = Now();
            _socketRev = m.TryGetValue("rev", out var r) && r is long rev ? rev : 0;
            if (m.TryGetValue("cid", out var cid) && cid is string c) ClientId = c;
            if (m.TryGetValue("role", out var role) && role is string ro) Role = ro;
            if (m.TryGetValue("lim", out var lim) && lim is Dictionary<string, object?> limits)
            {
                if (limits.TryGetValue("ops", out var ops) && ops is long o) _maxOps = (int)Math.Max(1, o);
                // A frame costs one rate-limit token per message, so never send more than the burst allows.
                if (limits.TryGetValue("burst", out var burst) && burst is long b && b > 0) _maxOps = (int)Math.Min(_maxOps, b);
                if (limits.TryGetValue("frame", out var frame) && frame is long f) _maxFrame = (int)Math.Max(256, f);
            }
            if (_state == ConnectionState.QuotaExceeded || _state != ConnectionState.Connected) LastError = null;
            SetState(ConnectionState.Connected);

            // Resync: resubscribe (with the revision we already hold), re-register onDisconnect writes, resend unconfirmed writes.
            var frame0 = new List<Dictionary<string, object?>>();
            foreach (var sub in _subs.Values) frame0.Add(SubMessage(sub));
            _odRequests.Clear();
            foreach (var reg in _od)
            {
                var odId = _nextId++;
                _odRequests[odId] = reg;
                frame0.Add(OdMessage(odId, reg.Op, reg.Path, reg.Value));
            }
            foreach (var w in _pending) w.Sent = false;
            if (_pending.Count > 0) _firstUnsentAt = DateTime.MinValue;
            if (frame0.Count > 0) toSend = frame0;
            else _resynced = true;
        }

        private Dictionary<string, object?> SubMessage(Subscription sub)
        {
            var id = _nextId++;
            _subRequests[id] = Paths.Join(sub.Path);
            var msg = new Dictionary<string, object?> { ["t"] = "sub", ["id"] = id, ["p"] = Paths.Join(sub.Path) };
            if (sub.HasData && sub.Rev > 0) msg["since"] = sub.Rev;
            return msg;
        }

        private static Dictionary<string, object?> OdMessage(long id, string op, string[] path, object? value)
        {
            var msg = new Dictionary<string, object?> { ["t"] = "od", ["id"] = id, ["op"] = op, ["p"] = Paths.Join(path) };
            if (op == "set" || op == "upd") msg["v"] = value;
            return msg;
        }

        private void OnSnap(Dictionary<string, object?> m, long? id)
        {
            if (id.HasValue) _subRequests.Remove(id.Value);
            if (!(m.TryGetValue("p", out var p) && p is string key) || !_subs.TryGetValue(key, out var sub)) return;
            var rev = m.TryGetValue("rev", out var r) && r is long l ? l : _socketRev;
            if (!(m.TryGetValue("nm", out var nm) && nm is true))
                _serverTree = Tree.SetIn(_serverTree, sub.Path, m.TryGetValue("v", out var v) ? v : null);
            sub.HasData = true;
            sub.Live = true;
            sub.Rev = rev;
            _socketRev = Math.Max(_socketRev, rev);
            if (_disk != null) _disk.Dirty = true;
            Notify();
        }

        private void OnPatch(Dictionary<string, object?> m)
        {
            if (m.TryGetValue("rev", out var r) && r is long rev) _socketRev = Math.Max(_socketRev, rev);
            if (!(m.TryGetValue("ops", out var opsObj) && opsObj is List<object?> ops)) return;
            foreach (var op in ops)
            {
                if (!(op is List<object?> pair) || pair.Count < 2 || !(pair[0] is string path)) continue;
                string[] segs;
                try
                {
                    segs = Paths.Parse(path);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                _serverTree = Tree.SetIn(_serverTree, segs, pair[1]);
            }
            if (_disk != null) _disk.Dirty = true;
            Notify();
        }

        private void OnAck(long? id, long rev)
        {
            if (id == null) return;
            var idx = _pending.FindIndex(w => w.Id == id);
            if (idx >= 0)
            {
                var w = _pending[idx];
                _pending.RemoveAt(idx);
                _completions.Add(() => w.Complete(rev));
                Notify();
                return;
            }
            if (_odRequests.TryGetValue(id.Value, out var reg))
            {
                _odRequests.Remove(id.Value);
                _completions.Add(() => reg.Waiter.TrySetResult(true));
            }
            else if (_cancelRequests.TryGetValue(id.Value, out var tcs))
            {
                _cancelRequests.Remove(id.Value);
                _completions.Add(() => tcs.TrySetResult(true));
            }
        }

        private void OnErr(long? id, Dictionary<string, object?> m)
        {
            var code = m.TryGetValue("code", out var c) && c is string cs ? cs : "unknown";
            var message = m.TryGetValue("msg", out var mm) && mm is string ms ? ms : code;
            var error = new FreeSyncException(code, message);
            if (code == "write_quota" || code == "room_quota")
            {
                _writesBlockedUntil = Now().Date.AddDays(1);
                var state = _state;
                _outbox.Add(() => StateChanged?.Invoke(state));
            }
            if (id == null)
            {
                LastError = error;
                RaiseError(error);
                return;
            }

            var idx = _pending.FindIndex(w => w.Id == id);
            if (idx >= 0)
            {
                var w = _pending[idx];
                if (code == "rate_limited")
                {
                    // Not applied; try again after the suggested wait.
                    var retry = m.TryGetValue("retryMs", out var rm) && rm is long ms2 ? ms2 : 1000;
                    w.Sent = false;
                    var until = Now().AddMilliseconds(retry);
                    if (until > _holdWritesUntil) _holdWritesUntil = until;
                    _writerWake.Release();
                    return;
                }
                _pending.RemoveAt(idx);
                _completions.Add(() => w.Fail(error));
                Notify(); // listeners revert the optimistic change
                return;
            }
            if (_subRequests.TryGetValue(id.Value, out var key))
            {
                _subRequests.Remove(id.Value);
                if (_subs.TryGetValue(key, out var sub))
                {
                    _subs.Remove(key);
                    foreach (var listener in sub.Listeners)
                        if (listener.OnError != null) _outbox.Add(() => listener.OnError(error));
                }
                return;
            }
            if (_odRequests.TryGetValue(id.Value, out var reg))
            {
                _odRequests.Remove(id.Value);
                _od.Remove(reg);
                _completions.Add(() => reg.Waiter.TrySetException(error));
                return;
            }
            if (_cancelRequests.TryGetValue(id.Value, out var tcs))
            {
                _cancelRequests.Remove(id.Value);
                _completions.Add(() => tcs.TrySetException(error));
            }
        }

        /// <summary>Marks the connection closed. Returns whether it had been welcomed.</summary>
        private bool OnSocketClosed(ISocket socket)
        {
            lock (_gate)
            {
                if (_socket != socket && _socket != null) return false;
                var wasReady = _ready;
                _ready = false;
                _resynced = false;
                _socket = null;
                foreach (var sub in _subs.Values)
                {
                    if (sub.Live) sub.Rev = Math.Max(sub.Rev, _socketRev);
                    sub.Live = false;
                }
                foreach (var w in _pending) w.Sent = false;
                _subRequests.Clear();
                foreach (var tcs in _cancelRequests.Values) _completions.Add(() => tcs.TrySetResult(true));
                _cancelRequests.Clear();
                return wasReady;
            }
        }

        // ---- writing ----------------------------------------------------------------------------

        private async Task WriterLoop()
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _writerWake.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    TimeSpan wait;
                    lock (_gate)
                    {
                        var unsent = _ready && _pending.Any(w => !w.Sent);
                        var due = Max(_firstUnsentAt + _opts.WriteInterval, _holdWritesUntil);
                        wait = unsent ? due - Now() : TimeSpan.Zero;
                    }
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                    await FlushWrites().ConfigureAwait(false);
                    SaveDisk(force: false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    Log("writer error: " + e.Message);
                }
            }
        }

        private async Task FlushWrites()
        {
            ISocket? socket;
            var frame = new List<object?>();
            lock (_gate)
            {
                if (!_ready || !_resynced || _socket == null || Now() < _holdWritesUntil) return;
                socket = _socket;
                var size = 2;
                foreach (var w in _pending)
                {
                    if (w.Sent) continue;
                    if (frame.Count >= _maxOps) break;
                    var msg = w.ToMessage();
                    var len = FsJson.Serialize(msg).Length + 1;
                    if (frame.Count > 0 && size + len > _maxFrame * 9 / 10) break;
                    size += len;
                    w.Sent = true;
                    frame.Add(msg);
                }
                if (frame.Count == 0) return;
                if (_pending.Any(w => !w.Sent))
                {
                    _firstUnsentAt = DateTime.MinValue;
                    _writerWake.Release();
                }
            }
            await SendFrame(frame, socket).ConfigureAwait(false);
        }

        private Task SendFrame(Dictionary<string, object?> msg) => SendFrame(new List<object?> { msg }, null);

        private async Task SendFrame(List<object?> messages, ISocket? socket)
        {
            lock (_gate) socket ??= _socket;
            if (socket == null || messages.Count == 0) return;
            var text = FsJson.Serialize(messages.Count == 1 ? messages[0] : messages);
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await socket.SendAsync(text, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // The receive loop notices the broken connection and reconnects; unconfirmed writes are resent.
                Log("send failed: " + e.Message);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ---- local state ------------------------------------------------------------------------

        private object? ApplyPending(object? tree)
        {
            foreach (var w in _pending) tree = w.ApplyTo(tree);
            return tree;
        }

        /// <summary>Confirmed server data with this client's unconfirmed writes applied on top.</summary>
        private object? View() => ApplyPending(_serverTree);

        private DataSnapshot SnapshotFor(Subscription sub, object? view) =>
            new DataSnapshot(sub.Path, Tree.GetIn(view, sub.Path), sub.Live ? _socketRev : sub.Rev, !sub.Live, _opts.Serializer);

        /// <summary>Queues callbacks for every subscription whose visible value changed. Call with the lock held.</summary>
        private void Notify()
        {
            if (_subs.Count == 0) return;
            var view = View();
            foreach (var sub in _subs.Values)
            {
                if (!sub.HasData || sub.Listeners.Count == 0) continue;
                var snap = SnapshotFor(sub, view);
                if (sub.Delivered && Tree.DeepEquals(snap.Value, sub.LastValue)) continue;
                sub.Delivered = true;
                sub.LastValue = snap.Value;
                foreach (var listener in sub.Listeners.ToArray()) _outbox.Add(() => listener.OnValue(snap));
            }
        }

        private void SetState(ConnectionState state)
        {
            if (_state == state) return;
            _state = state;
            _outbox.Add(() => StateChanged?.Invoke(state));
        }

        private void RaiseError(FreeSyncException e) => _outbox.Add(() => ErrorOccurred?.Invoke(e));

        /// <summary>Hands queued callbacks to the dispatcher. Never call with the lock held.</summary>
        private void FlushOutbox()
        {
            Action[] actions, completions;
            lock (_gate)
            {
                if (_outbox.Count == 0 && _completions.Count == 0) return;
                actions = _outbox.ToArray();
                _outbox.Clear();
                completions = _completions.ToArray();
                _completions.Clear();
            }
            // Listener callbacks first, so a write's listeners have seen its effect when its task completes.
            foreach (var a in actions) _events.Post(a);
            foreach (var c in completions) c();
        }

        private void SaveDisk(bool force)
        {
            if (_disk == null) return;
            lock (_gate)
            {
                foreach (var sub in _subs.Values)
                    if (sub.HasData) _disk.Put(Paths.Join(sub.Path), sub.Live ? _socketRev : sub.Rev, Tree.GetIn(_serverTree, sub.Path));
                if (!_disk.Dirty || (!force && Now() - _lastDiskSave < TimeSpan.FromSeconds(5))) return;
                _lastDiskSave = Now();
                _disk.Save();
            }
        }

        private static async Task CloseQuietly(ISocket s)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await s.CloseAsync(1000, "offline", timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best effort.
            }
        }

        private void Log(string message) => _opts.Log?.Invoke("[FreeSync] " + message);

        // ---- small types ------------------------------------------------------------------------

        private sealed class Subscription
        {
            public readonly string[] Path;
            public readonly List<Listener> Listeners = new List<Listener>();
            /// <summary>Some value is known (live, from the last connection, or from disk).</summary>
            public bool HasData;
            /// <summary>Loaded on the current connection and kept current by patches.</summary>
            public bool Live;
            /// <summary>Revision up to which the data is known to be current (used as `since` on resubscribe).</summary>
            public long Rev;
            public bool Delivered;
            public object? LastValue;

            public Subscription(string[] path) => Path = path;
        }

        private sealed class Listener
        {
            public readonly Action<DataSnapshot> OnValue;
            public readonly Action<FreeSyncException>? OnError;

            public Listener(Action<DataSnapshot> onValue, Action<FreeSyncException>? onError)
            {
                OnValue = onValue;
                OnError = onError;
            }
        }

        private sealed class OdRegistration
        {
            public readonly string Op;
            public readonly string[] Path;
            public readonly object? Value;
            public readonly TaskCompletionSource<bool> Waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public OdRegistration(string op, string[] path, object? value)
            {
                Op = op;
                Path = path;
                Value = value;
            }
        }

        private sealed class Unsubscriber : IDisposable
        {
            private Action? _action;
            public Unsubscriber(Action action) => _action = action;
            public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
        }
    }

    internal sealed class PendingWrite
    {
        public long Id;
        public readonly string Type;
        public readonly string[] Path;
        public readonly object? Value;
        public bool Sent;
        private readonly TaskCompletionSource<long> _tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        private List<PendingWrite>? _absorbed;

        public PendingWrite(string type, string[] path, object? value)
        {
            Type = type;
            Path = path;
            Value = value;
        }

        public Task<long> Task => _tcs.Task;

        /// <summary>Takes over an older unsent write that this one supersedes; it completes together with this one.</summary>
        public void Absorb(PendingWrite older)
        {
            _absorbed ??= new List<PendingWrite>();
            _absorbed.Add(older);
        }

        public void Complete(long rev)
        {
            _tcs.TrySetResult(rev);
            if (_absorbed != null) foreach (var a in _absorbed) a.Complete(rev);
        }

        public void Fail(Exception e)
        {
            _tcs.TrySetException(e);
            if (_absorbed != null) foreach (var a in _absorbed) a.Fail(e);
        }

        public object? ApplyTo(object? tree)
        {
            switch (Type)
            {
                case "set": return Tree.SetIn(tree, Path, Value);
                case "rm": return Tree.SetIn(tree, Path, null);
                default:
                    if (Value is Dictionary<string, object?> values)
                        foreach (var kv in values) tree = Tree.SetIn(tree, Paths.Concat(Path, Paths.Parse(kv.Key)), kv.Value);
                    return tree;
            }
        }

        public Dictionary<string, object?> ToMessage()
        {
            var msg = new Dictionary<string, object?> { ["t"] = Type, ["id"] = Id, ["p"] = Paths.Join(Path) };
            if (Type != "rm") msg["v"] = Value;
            return msg;
        }
    }
}
