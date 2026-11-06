using System.Text;
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
    /// Splits the changes into writes that each fit in one WebSocket frame, in the order they must be sent.
    /// Normally there is a single multi-path update (atomic). A value too big for one frame is sent as
    /// "replace with the first part" followed by updates adding the remaining children (recursively), which
    /// gives the same end result but is only atomic per frame.
    /// </summary>
    /// <exception cref="FormatException">A single string or array is larger than a frame and can't be split.</exception>
    public List<WriteFrame> ToFrames(int maxBytes = 56_000)
    {
        var frames = new List<WriteFrame>();
        var current = new Dictionary<string, object?>();
        var size = 0;

        void Flush()
        {
            if (current.Count == 0) return;
            frames.Add(new WriteFrame(false, null, current));
            current = new Dictionary<string, object?>();
            size = 0;
        }

        void Add(string[] path, object? value, int len)
        {
            if (path.Length == 0)
            {
                // Replacing the whole room is a set at the root, alone in its frame.
                Flush();
                frames.Add(new WriteFrame(true, value, new Dictionary<string, object?>()));
                return;
            }
            if (current.Count > 0 && size + len > maxBytes) Flush();
            current[JsonUtil.Join(path)] = value;
            size += len;
        }

        void Emit(string[] path, object? value)
        {
            var len = Size(path, value);
            if (len <= maxBytes)
            {
                Add(path, value, len);
                return;
            }
            if (value is not Dictionary<string, object?> obj)
                throw new FormatException($"the value at /{JsonUtil.Join(path)} is {len:N0} bytes and can't be split into frames (max {maxBytes:N0})");

            // First frame replaces the subtree with as many children as fit; the rest are added afterwards.
            var first = new Dictionary<string, object?>();
            var rest = new List<(string Key, object? Value)>();
            var firstSize = Size(path, null);
            foreach (var (k, child) in obj)
            {
                var childLen = Size(new[] { k }, child);
                if (rest.Count == 0 && firstSize + childLen <= maxBytes)
                {
                    first[k] = child;
                    firstSize += childLen;
                }
                else rest.Add((k, child));
            }
            Add(path, first.Count == 0 ? null : first, firstSize);
            Flush(); // children below must not share a frame with their (overlapping) parent path
            foreach (var (k, child) in rest) Emit(path.Append(k).ToArray(), child);
        }

        foreach (var (p, v) in _entries) Emit(p, v);
        Flush();
        return frames;
    }

    private static int Size(IReadOnlyList<string> path, object? value) =>
        Encoding.UTF8.GetByteCount(JsonUtil.Join(path)) + Encoding.UTF8.GetByteCount(FsJson.Serialize(value)) + 8;
}

/// <summary>One write to send: either a set at the room root, or a multi-path update (paths → values).</summary>
public sealed record WriteFrame(bool SetsRoot, object? RootValue, Dictionary<string, object?> Paths)
{
    /// <summary>Applies this frame to a tree the way the server does.</summary>
    public object? ApplyTo(object? tree)
    {
        if (SetsRoot) return JsonUtil.Normalize(RootValue);
        foreach (var (p, v) in Paths) tree = JsonUtil.SetIn(tree, JsonUtil.ParsePath(p), v);
        return tree;
    }
}
