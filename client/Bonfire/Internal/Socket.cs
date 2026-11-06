using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bonfire.Internal
{
    /// <summary>Minimal text-WebSocket abstraction so the client logic can be tested without a network.</summary>
    internal interface ISocket : IDisposable
    {
        /// <summary>Opens the connection. Throws <see cref="ConnectException"/> on an HTTP refusal.</summary>
        Task ConnectAsync(Uri uri, IDictionary<string, string> headers, TimeSpan keepAlive, CancellationToken ct);

        Task SendAsync(string text, CancellationToken ct);

        /// <summary>Returns the next text message, or null when the connection closed.</summary>
        Task<string?> ReceiveAsync(CancellationToken ct);

        /// <summary>Close code sent by the server, if any.</summary>
        int? CloseStatus { get; }

        Task CloseAsync(int code, string reason, CancellationToken ct);
    }

    /// <summary>The server answered the upgrade request with an HTTP error.</summary>
    internal sealed class ConnectException : Exception
    {
        public int? HttpStatus { get; }

        public ConnectException(int? status, string message, Exception? inner = null) : base(message, inner) => HttpStatus = status;
    }

    internal sealed class ClientWebSocketSocket : ISocket
    {
        private readonly ClientWebSocket _ws = new ClientWebSocket();

        public int? CloseStatus => (int?)_ws.CloseStatus;

        public async Task ConnectAsync(Uri uri, IDictionary<string, string> headers, TimeSpan keepAlive, CancellationToken ct)
        {
            foreach (var h in headers) _ws.Options.SetRequestHeader(h.Key, h.Value);
            _ws.Options.KeepAliveInterval = keepAlive;
#if NET7_0_OR_GREATER
            _ws.Options.CollectHttpResponseDetails = true;
#endif
#if NET9_0_OR_GREATER
            // Send real pings and drop the connection if the server stops answering.
            if (keepAlive > TimeSpan.Zero) _ws.Options.KeepAliveTimeout = keepAlive + keepAlive;
#endif
            try
            {
                await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            }
            catch (WebSocketException e)
            {
                int? status = null;
#if NET7_0_OR_GREATER
                if (_ws.HttpStatusCode != 0) status = (int)_ws.HttpStatusCode;
#endif
                if (status == null)
                {
                    // Older runtimes only put the status in the message text.
                    var m = Regex.Match(e.ToString(), @"\b(401|403|404|426|429|503)\b");
                    if (m.Success) status = int.Parse(m.Value);
                }
                throw new ConnectException(status, e.Message, e);
            }
        }

        public Task SendAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var ms = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult r;
                try
                {
                    r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    return null;
                }
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, r.Count);
                if (r.EndOfMessage) break;
            }
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }

        public async Task CloseAsync(int code, string reason, CancellationToken ct)
        {
            if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                await _ws.CloseAsync((WebSocketCloseStatus)code, reason, ct).ConfigureAwait(false);
        }

        public void Dispose() => _ws.Dispose();
    }
}
