using FreeSync.Json;

namespace FreeSyncAdmin;

/// <summary>
/// Staged edits for batch mode: an ordered set of "path = value" writes (null deletes) with no path inside
/// another. A later edit inside an earlier one is merged into it; a later edit above earlier ones replaces them.
/// The whole set is then sent as one multi-path update, which the server applies atomically under one revision.
/// </summary>
public sealed class ChangeSet
{
    private readonly List<(string[] Path, object? Value)> _entries = new();

    public int Count => _entries.Count;

    public IReadOnlyList<(string[] Path, object? Value)> Entries => _entries;

    public void Add(IReadOnlyList<string> path, object? value)
    {
        value = JsonUtil.Normalize(value);
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (!JsonUtil.IsPrefix(e.Path, path)) continue;
            // Inside (or equal to) an existing change: fold it into that change's value.
            _entries[i] = (e.Path, JsonUtil.SetIn(e.Value, path, value, e.Path.Length));
            return;
        }
        _entries.RemoveAll(e => JsonUtil.IsPrefix(path, e.Path));
        _entries.Add((path.ToArray(), value));
    }

    public void AddAll(ChangeSet other)
    {
        foreach (var (p, v) in other._entries) Add(p, v);
    }

    public void RemoveAt(int index) => _entries.RemoveAt(index);

    public void Clear() => _entries.Clear();

    /// <summary>The tree with every staged change applied.</summary>
    public object? ApplyTo(object? tree)
    {
        foreach (var (p, v) in _entries) tree = JsonUtil.SetIn(tree, p, v);
        return tree;
    }

    /// <summary>Whether <paramref name="path"/> is changed by this set (directly or because an ancestor is).</summary>
    public bool Touches(IReadOnlyList<string> path) => _entries.Any(e => JsonUtil.IsPrefix(e.Path, path));

    /// <summary>Whether something below <paramref name="path"/> is changed.</summary>
    public bool ContainsBelow(IReadOnlyList<string> path) => _entries.Any(e => e.Path.Length > path.Count && JsonUtil.IsPrefix(path, e.Path));

    /// <summary>
    /// Splits the changes into multi-path updates that each fit in one WebSocket frame. Normally there is one;
    /// several are only atomic individually.
    /// </summary>
    public List<Dictionary<string, object?>> ToUpdates(int maxBytes = 56_000)
    {
        var result = new List<Dictionary<string, object?>>();
        var current = new Dictionary<string, object?>();
        var size = 0;
        foreach (var (p, v) in _entries)
        {
            var key = JsonUtil.Join(p);
            var len = key.Length + FsJson.Serialize(v).Length + 8;
            if (current.Count > 0 && size + len > maxBytes)
            {
                result.Add(current);
                current = new Dictionary<string, object?>();
                size = 0;
            }
            current[key] = v;
            size += len;
        }
        if (current.Count > 0) result.Add(current);
        return result;
    }
}
