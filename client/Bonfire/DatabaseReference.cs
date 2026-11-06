using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bonfire.Internal;

namespace Bonfire
{
    /// <summary>
    /// A location in the room's JSON document. Cheap to create; holds no state of its own.
    /// Obtain one with <see cref="BonfireClient.Ref"/> or <see cref="Child"/>.
    /// </summary>
    public sealed class DatabaseReference
    {
        private readonly BonfireClient _client;
        internal readonly string[] Segments;

        internal DatabaseReference(BonfireClient client, string[] segments)
        {
            _client = client;
            Segments = segments;
        }

        /// <summary>The path, without leading slash ("" for the root).</summary>
        public string Path => Paths.Join(Segments);

        /// <summary>The last path segment, or null for the root.</summary>
        public string? Key => Segments.Length == 0 ? null : Segments[Segments.Length - 1];

        /// <summary>The parent location, or null for the root.</summary>
        public DatabaseReference? Parent =>
            Segments.Length == 0 ? null : new DatabaseReference(_client, Segments.Take(Segments.Length - 1).ToArray());

        /// <summary>The root of the document.</summary>
        public DatabaseReference Root => new DatabaseReference(_client, Paths.Root);

        /// <summary>A location below this one. <paramref name="path"/> may contain slashes.</summary>
        public DatabaseReference Child(string path) => new DatabaseReference(_client, Paths.Concat(Segments, Paths.Parse(path)));

        /// <summary>A child with a new unique key that sorts by creation time (for lists such as chat messages).</summary>
        public DatabaseReference Push() => Child(Ids.PushId(_client.UtcNow()));

        /// <summary>
        /// Replaces the value here (null deletes). Listeners see the change at once; the task completes when the server
        /// confirms it, with the room revision that contains it. If the server refuses, listeners revert and the task fails with
        /// <see cref="BonfireException"/>. While offline, writes queue and are sent after reconnecting.
        /// </summary>
        public Task<long> SetAsync(object? value) => _client.Write("set", Segments, _client.ToTree(value));

        /// <summary>
        /// Updates several children at once. Keys are paths relative to this location (they may contain slashes);
        /// a null value deletes that child. All of them apply together or not at all.
        /// </summary>
        public Task<long> UpdateAsync(IDictionary<string, object?> values) => _client.Write("upd", Segments, _client.UpdateTree(Segments, values));

        /// <summary>Deletes the value here and everything below it.</summary>
        public Task<long> RemoveAsync() => _client.Write("rm", Segments, null);

        /// <summary>
        /// Calls <paramref name="onValue"/> with the value here now (or as soon as it is known) and after every change.
        /// Dispose the result to stop listening. Callbacks run according to <see cref="BonfireOptions.EventDispatch"/>.
        /// </summary>
        /// <param name="onValue">Receives each new snapshot.</param>
        /// <param name="onError">Optional; receives a <see cref="BonfireException"/> if the server refuses the subscription.</param>
        public IDisposable OnValue(Action<DataSnapshot> onValue, Action<BonfireException>? onError = null) =>
            _client.Listen(Segments, onValue, onError);

        /// <summary>
        /// Reads the value once: from a live subscription when one covers this path, otherwise through the HTTP snapshot
        /// endpoint (which is cached on the server side). Falls back to cached data when the server is unreachable.
        /// </summary>
        public Task<DataSnapshot> GetAsync() => _client.Get(Segments);

        /// <summary>Writes to perform on the server when this client disconnects (presence, "player left").</summary>
        public OnDisconnectReference OnDisconnect() => new OnDisconnectReference(_client, Segments);

        /// <inheritdoc />
        public override string ToString() => "/" + Path;
    }

    /// <summary>
    /// Registers writes that the server applies when this client's connection closes, even if the game crashes.
    /// Registrations are re-sent automatically after every reconnect.
    /// </summary>
    public sealed class OnDisconnectReference
    {
        private readonly BonfireClient _client;
        private readonly string[] _path;

        internal OnDisconnectReference(BonfireClient client, string[] path)
        {
            _client = client;
            _path = path;
        }

        /// <summary>Set this location to <paramref name="value"/> on disconnect.</summary>
        public Task Set(object? value) => _client.RegisterOnDisconnect("set", _path, _client.ToTree(value));

        /// <summary>Apply a multi-path update on disconnect.</summary>
        public Task Update(IDictionary<string, object?> values) => _client.RegisterOnDisconnect("upd", _path, _client.UpdateTree(_path, values));

        /// <summary>Remove this location on disconnect.</summary>
        public Task Remove() => _client.RegisterOnDisconnect("rm", _path, null);

        /// <summary>Cancel the disconnect writes registered at or below this location.</summary>
        public Task Cancel() => _client.RegisterOnDisconnect("cancel", _path, null);
    }
}
