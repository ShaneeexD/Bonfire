using System;
using System.Collections;
using System.Collections.Generic;

namespace FreeSync.Internal
{
    /// <summary>
    /// Operations on JSON trees (Dictionary / List / scalars). Trees handed to user code are never mutated:
    /// <see cref="SetIn"/> copies only the dictionaries along the path it changes.
    /// </summary>
    internal static class Tree
    {
        public static object? GetIn(object? root, IReadOnlyList<string> path, int from = 0)
        {
            var cur = root;
            for (var i = from; i < path.Count; i++)
            {
                if (!(cur is Dictionary<string, object?> d) || !d.TryGetValue(path[i], out cur)) return null;
            }
            return cur;
        }

        public static object? SetIn(object? root, IReadOnlyList<string> path, object? value, int from = 0)
        {
            if (from >= path.Count) return value;
            var key = path[from];
            var baseDict = root as Dictionary<string, object?>;
            baseDict ??= new Dictionary<string, object?>();
            baseDict.TryGetValue(key, out var existing);
            var child = SetIn(existing, path, value, from + 1);
            var copy = new Dictionary<string, object?>(baseDict);
            if (child == null) copy.Remove(key);
            else copy[key] = child;
            return copy.Count == 0 ? null : copy;
        }

        /// <summary>Removes null children and empty objects/arrays, like the server does.</summary>
        public static object? Normalize(object? value)
        {
            switch (value)
            {
                case null: return null;
                case Dictionary<string, object?> d:
                {
                    Dictionary<string, object?>? result = null;
                    foreach (var kv in d)
                    {
                        var err = Paths.CheckKey(kv.Key);
                        if (err != null) throw new ArgumentException(err);
                        var n = Normalize(kv.Value);
                        if (n == null) continue;
                        result ??= new Dictionary<string, object?>();
                        result[kv.Key] = n;
                    }
                    return result;
                }
                case List<object?> l:
                {
                    if (l.Count == 0) return null;
                    var result = new List<object?>(l.Count);
                    foreach (var item in l) result.Add(Normalize(item));
                    return result;
                }
                case double dbl when double.IsNaN(dbl) || double.IsInfinity(dbl):
                    throw new ArgumentException("numbers must be finite");
                default:
                    return value;
            }
        }

        /// <summary>Structural equality of two trees.</summary>
        public static bool DeepEquals(object? a, object? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a is Dictionary<string, object?> da && b is Dictionary<string, object?> db)
            {
                if (da.Count != db.Count) return false;
                foreach (var kv in da)
                    if (!db.TryGetValue(kv.Key, out var bv) || !DeepEquals(kv.Value, bv)) return false;
                return true;
            }
            if (a is IList la && b is IList lb)
            {
                if (la.Count != lb.Count) return false;
                for (var i = 0; i < la.Count; i++)
                    if (!DeepEquals(la[i], lb[i])) return false;
                return true;
            }
            if (IsNumber(a) && IsNumber(b)) return Convert.ToDouble(a).Equals(Convert.ToDouble(b));
            return a.Equals(b);
        }

        private static bool IsNumber(object o) => o is long || o is double;
    }
}
