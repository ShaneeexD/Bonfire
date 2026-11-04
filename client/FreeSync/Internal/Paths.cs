using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FreeSync.Internal
{
    /// <summary>Path parsing and comparison, mirroring the server's rules.</summary>
    internal static class Paths
    {
        public const int MaxDepth = 32;
        public const int MaxBytes = 512;

        public static readonly string[] Root = new string[0];

        /// <summary>Parses and validates a path. Throws <see cref="ArgumentException"/> when invalid.</summary>
        public static string[] Parse(string? path)
        {
            if (path == null) return Root;
            if (Encoding.UTF8.GetByteCount(path) > MaxBytes) throw new ArgumentException($"path longer than {MaxBytes} bytes: {path}");
            var segs = path.Trim('/');
            if (segs.Length == 0) return Root;
            var parts = segs.Split('/');
            if (parts.Length > MaxDepth) throw new ArgumentException($"path deeper than {MaxDepth} levels: {path}");
            foreach (var p in parts)
            {
                var err = CheckKey(p);
                if (err != null) throw new ArgumentException($"invalid path \"{path}\": {err}");
            }
            return parts;
        }

        public static string? CheckKey(string key)
        {
            if (key.Length == 0) return "empty key";
            foreach (var c in key)
                if (c == '/' || c == '.' || c == '#' || c == '$' || c == '[' || c == ']' || c < 0x20 || c == 0x7f)
                    return $"key \"{key}\" contains a forbidden character (/ . # $ [ ] or control)";
            return null;
        }

        public static string Join(IReadOnlyList<string> path) => string.Join("/", path);

        public static string[] Concat(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var r = new string[a.Count + b.Count];
            for (var i = 0; i < a.Count; i++) r[i] = a[i];
            for (var i = 0; i < b.Count; i++) r[a.Count + i] = b[i];
            if (r.Length > MaxDepth) throw new ArgumentException($"path deeper than {MaxDepth} levels");
            return r;
        }

        /// <summary>True when <paramref name="a"/> equals <paramref name="b"/> or is an ancestor of it.</summary>
        public static bool IsPrefix(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count > b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        public static bool Overlaps(IReadOnlyList<string> a, IReadOnlyList<string> b) => IsPrefix(a, b) || IsPrefix(b, a);

        public static string[] Slice(IReadOnlyList<string> p, int from) => p.Skip(from).ToArray();
    }
}
