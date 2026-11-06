using System;
using System.Collections.Generic;
using System.Linq;
using Bonfire.Internal;
using Bonfire.Json;

namespace Bonfire
{
    /// <summary>
    /// An immutable view of the data at a path at one moment. The raw <see cref="Value"/> is a JSON tree made of
    /// <c>Dictionary&lt;string, object?&gt;</c>, <c>List&lt;object?&gt;</c>, <see cref="string"/>, <see cref="long"/>,
    /// <see cref="double"/>, <see cref="bool"/> or null. Do not modify it; use <see cref="GetValue{T}"/> for your own types.
    /// </summary>
    public sealed class DataSnapshot
    {
        private readonly IJsonSerializer _serializer;
        private readonly string[] _path;

        internal DataSnapshot(string[] path, object? value, long rev, bool isFromCache, IJsonSerializer serializer)
        {
            _path = path;
            Value = value;
            Rev = rev;
            IsFromCache = isFromCache;
            _serializer = serializer;
        }

        /// <summary>The path of this snapshot, without leading slash ("" for the root).</summary>
        public string Path => Paths.Join(_path);

        /// <summary>The last path segment, or null for the root.</summary>
        public string? Key => _path.Length == 0 ? null : _path[_path.Length - 1];

        /// <summary>The raw JSON tree, or null when nothing is stored here.</summary>
        public object? Value { get; }

        /// <summary>True when a value exists at this path.</summary>
        public bool Exists => Value != null;

        /// <summary>The room revision this snapshot reflects (0 if unknown). Revisions only increase but are not contiguous.</summary>
        public long Rev { get; }

        /// <summary>True when the data came from the local cache (disk or memory) rather than a live server response.</summary>
        public bool IsFromCache { get; }

        /// <summary>Converts the value to <typeparamref name="T"/> with the configured <see cref="IJsonSerializer"/>.</summary>
        public T? GetValue<T>()
        {
            if (Value == null) return default;
            if (Value is T t && !(Value is Dictionary<string, object?>) && !(Value is List<object?>)) return t;
            if (_serializer is BuiltInJsonSerializer) return (T?)BuiltInJsonSerializer.FromTree(Value, typeof(T));
            return _serializer.Deserialize<T>(BonfireJson.Serialize(Value));
        }

        /// <summary>The snapshot of a child path (which may contain slashes).</summary>
        public DataSnapshot Child(string path)
        {
            var rel = Paths.Parse(path);
            return new DataSnapshot(Paths.Concat(_path, rel), Tree.GetIn(Value, rel), Rev, IsFromCache, _serializer);
        }

        /// <summary>Whether a child path has a value.</summary>
        public bool HasChild(string path) => Tree.GetIn(Value, Paths.Parse(path)) != null;

        /// <summary>Direct children of an object value, in ordinal key order.</summary>
        public IEnumerable<DataSnapshot> Children =>
            Value is Dictionary<string, object?> d
                ? d.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new DataSnapshot(Paths.Concat(_path, new[] { k }), d[k], Rev, IsFromCache, _serializer))
                : Enumerable.Empty<DataSnapshot>();

        /// <summary>Number of direct children of an object value.</summary>
        public int ChildrenCount => Value is Dictionary<string, object?> d ? d.Count : 0;

        /// <summary>The value as compact JSON text.</summary>
        public string ToJson() => BonfireJson.Serialize(Value);

        /// <inheritdoc />
        public override string ToString() => $"{Path}: {ToJson()}";
    }
}
