using System.Text.Json;
using Bonfire.Json;

namespace BonfireAdmin;

/// <summary>
/// Helpers for the plain JSON trees Bonfire hands out (Dictionary / List / string / long / double / bool / null).
/// Trees are never mutated in place: <see cref="SetIn"/> copies only the objects along the changed path.
/// </summary>
public static class JsonUtil
{
    /// <summary>Splits "a/b/c" (or "a.b.c" when <paramref name="allowDots"/>) into keys. "" is the root.</summary>
    public static string[] ParsePath(string path, bool allowDots = false)
    {
        var p = path.Trim().Trim('/');
        if (p.Length == 0) return Array.Empty<string>();
        var segs = p.Split(allowDots ? new[] { '/', '.' } : new[] { '/' });
        foreach (var s in segs)
        {
            if (s.Length == 0) throw new FormatException($"empty key in path \"{path}\"");
            if (s.IndexOfAny(new[] { '.', '#', '$', '[', ']' }) >= 0 || s.Any(char.IsControl))
                throw new FormatException($"key \"{s}\" contains a character keys cannot have (. # $ [ ])");
        }
        return segs;
    }

    public static string Join(IEnumerable<string> path) => string.Join("/", path);

    public static bool IsPrefix(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count > b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    public static object? GetIn(object? root, IReadOnlyList<string> path, int from = 0)
    {
        var cur = root;
        for (var i = from; i < path.Count; i++)
            if (cur is not Dictionary<string, object?> d || !d.TryGetValue(path[i], out cur)) return null;
        return cur;
    }

    /// <summary>Copy of <paramref name="root"/> with <paramref name="value"/> at the path; null deletes and prunes empty parents.</summary>
    public static object? SetIn(object? root, IReadOnlyList<string> path, object? value, int from = 0)
    {
        if (from >= path.Count) return Normalize(value);
        var baseDict = root as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        baseDict.TryGetValue(path[from], out var existing);
        var child = SetIn(existing, path, value, from + 1);
        var copy = new Dictionary<string, object?>(baseDict);
        if (child == null) copy.Remove(path[from]);
        else copy[path[from]] = child;
        return copy.Count == 0 ? null : copy;
    }

    /// <summary>Removes null children and empty objects/arrays, as the server does.</summary>
    public static object? Normalize(object? v)
    {
        switch (v)
        {
            case Dictionary<string, object?> d:
            {
                var r = new Dictionary<string, object?>();
                foreach (var (k, c) in d)
                {
                    var n = Normalize(c);
                    if (n != null) r[k] = n;
                }
                return r.Count == 0 ? null : r;
            }
            case List<object?> l:
                return l.Count == 0 ? null : l;
            default:
                return v;
        }
    }

    public static string Compact(object? v) => BonfireJson.Serialize(v);

    public static string Pretty(object? v)
    {
        using var doc = JsonDocument.Parse(BonfireJson.Serialize(v));
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Short one-line form for lists and grids: strings unquoted, everything else as JSON.</summary>
    public static string Cell(object? v, int max = 200)
    {
        var s = v switch
        {
            null => "",
            string str => str,
            _ => BonfireJson.Serialize(v),
        };
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    public static bool IsNumber(object? v) => v is long or double;

    public static double ToDouble(object? v) => v switch
    {
        long l => l,
        double d => d,
        _ => throw new InvalidCastException(),
    };

    public static bool DeepEquals(object? a, object? b) => BonfireJson.Serialize(a) == BonfireJson.Serialize(b);
}
