using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FreeSync.Internal;
using FreeSync.Json;

namespace FreeSync.Tests
{
    /// <summary>Hands out scripted in-memory sockets so tests can play the server's side of the protocol.</summary>
    internal sealed class MockServer
    {
        private readonly Channel<MockSocket> _connections = Channel.CreateUnbounded<MockSocket>();

        /// <summary>HTTP statuses to fail the next connect attempts with (null = succeed).</summary>
        public readonly ConcurrentQueue<int?> ConnectResults = new ConcurrentQueue<int?>();

        public int ConnectAttempts;
        public Dictionary<string, string>? LastHeaders;
        public Uri? LastUri;

        public ISocket Create() => new MockSocket(this);

        internal void Accepted(MockSocket s) => _connections.Writer.TryWrite(s);

        public async Task<MockSocket> NextConnection(int timeoutMs = 3000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            return await _connections.Reader.ReadAsync(cts.Token);
        }

        /// <summary>Waits for a connection and sends the welcome message.</summary>
        public async Task<MockSocket> Accept(long rev = 100, string cid = "cid1")
        {
            var s = await NextConnection();
            s.Push(new { t = "welcome", v = 1, cid, role = "public", rev, lim = new { frame = 65536, ops = 100, subs = 64 } });
            return s;
        }
    }

    internal sealed class MockSocket : ISocket
    {
        private readonly MockServer _server;
        private readonly Channel<string?> _toClient = Channel.CreateUnbounded<string?>();
        private readonly Channel<string> _fromClient = Channel.CreateUnbounded<string>();

        public MockSocket(MockServer server) => _server = server;

        public int? CloseStatus { get; private set; }

        public Task ConnectAsync(Uri uri, IDictionary<string, string> headers, TimeSpan keepAlive, CancellationToken ct)
        {
            Interlocked.Increment(ref _server.ConnectAttempts);
            _server.LastHeaders = new Dictionary<string, string>(headers);
            _server.LastUri = uri;
            if (_server.ConnectResults.TryDequeue(out var status) && status != null)
                throw new ConnectException(status, $"HTTP {status}");
            _server.Accepted(this);
            return Task.CompletedTask;
        }

        public Task SendAsync(string text, CancellationToken ct)
        {
            _fromClient.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            try
            {
                return await _toClient.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public Task CloseAsync(int code, string reason, CancellationToken ct)
        {
            Drop(code);
            return Task.CompletedTask;
        }

        public void Dispose() => _toClient.Writer.TryComplete();

        // ---- server side ----

        public void Push(object message) => _toClient.Writer.TryWrite(message as string ?? FsJson.Serialize(BuiltInJsonSerializer.ToTree(message)));

        public void Drop(int? code = null)
        {
            CloseStatus ??= code;
            _toClient.Writer.TryComplete();
        }

        /// <summary>The next frame the client sent, as a list of messages.</summary>
        public async Task<List<Dictionary<string, object?>>> NextFrame(int timeoutMs = 3000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var text = await _fromClient.Reader.ReadAsync(cts.Token);
            var parsed = FsJson.Parse(text);
            return parsed is List<object?> list ? list.Cast<Dictionary<string, object?>>().ToList() : new List<Dictionary<string, object?>> { (Dictionary<string, object?>)parsed! };
        }

        /// <summary>Reads frames until a message matching <paramref name="pred"/> arrives.</summary>
        public async Task<Dictionary<string, object?>> NextMessage(Func<Dictionary<string, object?>, bool> pred, int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) throw new TimeoutException("no matching message");
                foreach (var m in await NextFrame(remaining))
                    if (pred(m)) return m;
            }
        }

        public Task<Dictionary<string, object?>> NextOf(string type) => NextMessage(m => (string?)m["t"] == type);

        public bool HasPendingFrames => _fromClient.Reader.Count > 0;
    }
}
