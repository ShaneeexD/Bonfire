using System;
using FreeSync.Json;

namespace FreeSync
{
    /// <summary>The state of the realtime connection, reported through <see cref="FreeSyncClient.StateChanged"/>.</summary>
    public enum ConnectionState
    {
        /// <summary>Opening the first connection.</summary>
        Connecting,
        /// <summary>Connected and in sync. Data is live.</summary>
        Connected,
        /// <summary>The connection dropped; retrying with backoff. Listeners keep the last known data.</summary>
        Reconnecting,
        /// <summary>
        /// The server's daily free quota is used up. Last known data stays available; the client retries rarely
        /// (see <see cref="FreeSyncOptions.QuotaProbeInterval"/>) and fully after 00:00 UTC.
        /// </summary>
        QuotaExceeded,
        /// <summary>Not connected and not trying: <see cref="FreeSyncClient.GoOffline"/> was called, the client was disposed,
        /// or the server refused the protocol version.</summary>
        Offline,
    }

    /// <summary>Where listener callbacks run.</summary>
    public enum EventDispatchMode
    {
        /// <summary>
        /// Use the <see cref="System.Threading.SynchronizationContext"/> that was current when the client was created
        /// (Unity's main thread, a UI thread...). Falls back to <see cref="Immediate"/> if there was none.
        /// </summary>
        Auto,
        /// <summary>Run callbacks on the network thread as soon as data arrives. Callbacks must be thread-safe.</summary>
        Immediate,
        /// <summary>Post callbacks to the captured <see cref="System.Threading.SynchronizationContext"/>.</summary>
        SynchronizationContext,
        /// <summary>Queue callbacks until you call <see cref="FreeSyncClient.Dispatch"/>, for example from Unity's <c>Update()</c>.</summary>
        Queued,
    }

    /// <summary>Settings for <see cref="FreeSyncClient"/>. All have sensible defaults for hobby use.</summary>
    public sealed class FreeSyncOptions
    {
        /// <summary>
        /// A secret, stable per-player token (16-256 characters). The server derives <see cref="FreeSyncClient.ClientId"/> from it,
        /// so rules like <c>players/$cid</c> work across restarts. Use <see cref="FreeSyncClient.LoadOrCreateToken"/> to keep one on disk.
        /// Without a token, each connection gets a random anonymous id.
        /// </summary>
        public string? ClientToken { get; set; }

        /// <summary>How long writes are collected before being sent as one frame. Default 100 ms. Zero sends at once.</summary>
        public TimeSpan WriteInterval { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>Where listener callbacks run. Default <see cref="EventDispatchMode.Auto"/>.</summary>
        public EventDispatchMode EventDispatch { get; set; } = EventDispatchMode.Auto;

        /// <summary>
        /// If set, the latest value of every subscribed path is saved here and shown at the next start
        /// (with <see cref="DataSnapshot.IsFromCache"/> set) before live data arrives.
        /// </summary>
        public string? CacheDirectory { get; set; }

        /// <summary>Converts your types to and from JSON. Default <see cref="BuiltInJsonSerializer"/>.</summary>
        public IJsonSerializer Serializer { get; set; } = BuiltInJsonSerializer.Instance;

        /// <summary>First reconnect delay. Doubles per failed attempt (with jitter). Default 1 second.</summary>
        public TimeSpan ReconnectMinDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Longest reconnect delay. Default 5 minutes.</summary>
        public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// While the quota is exhausted, how often to check whether it is back before 00:00 UTC.
        /// Each check costs a request. Default 30 minutes; null waits until 00:00 UTC.
        /// </summary>
        public TimeSpan? QuotaProbeInterval { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>WebSocket keep-alive ping interval (protocol pings are free on the server). Default 30 seconds.</summary>
        public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Maximum writes waiting to be sent while offline. Further writes fail. Default 1000.</summary>
        public int MaxQueuedWrites { get; set; } = 1000;

        /// <summary>Connect automatically when the client is created. Default true.</summary>
        public bool AutoConnect { get; set; } = true;

        /// <summary>Optional diagnostic log. Never receives keys or tokens.</summary>
        public Action<string>? Log { get; set; }

        /// <summary>Test hook: creates the socket implementation.</summary>
        internal Func<Internal.ISocket>? SocketFactory { get; set; }

        /// <summary>Test hook: current UTC time.</summary>
        internal Func<DateTime>? UtcNow { get; set; }
    }
}
