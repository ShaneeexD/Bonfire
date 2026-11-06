using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FreeSync.Json;

namespace FreeSync.Internal
{
    /// <summary>Persists the latest value and revision of subscribed paths for cold starts.</summary>
    internal sealed class DiskCache
    {
        private const int MaxEntries = 256;

        private readonly string _file;
        private readonly Action<string> _log;
        private readonly Dictionary<string, (long Rev, object? Value, long Order)> _entries = new Dictionary<string, (long, object?, long)>();
        private long _order;

        public DiskCache(string directory, string host, string room, Action<string> log)
        {
            _log = log;
            var safe = new string((host + "_" + room).Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            _file = Path.Combine(directory, "freesync_" + safe + ".json");
            Load();
        }

        public bool Dirty { get; set; }

        public bool TryGet(string path, out long rev, out object? value)
        {
            if (_entries.TryGetValue(path, out var e))
            {
                rev = e.Rev;
                value = e.Value;
                return true;
            }
            rev = 0;
            value = null;
            return false;
        }

        public void Put(string path, long rev, object? value)
        {
            _entries[path] = (rev, value, ++_order);
            if (_entries.Count > MaxEntries)
                _entries.Remove(_entries.OrderBy(kv => kv.Value.Order).First().Key);
            Dirty = true;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_file)) return;
                if (!(FsJson.Parse(File.ReadAllText(_file)) is Dictionary<string, object?> root)) return;
                if (!(root.TryGetValue("paths", out var paths) && paths is Dictionary<string, object?> map)) return;
                foreach (var kv in map)
                {
                    if (!(kv.Value is Dictionary<string, object?> e)) continue;
                    var rev = e.TryGetValue("rev", out var r) && r is long l ? l : 0;
                    e.TryGetValue("v", out var v);
                    _entries[kv.Key] = (rev, v, ++_order);
                }
            }
            catch (Exception e)
            {
                _log("ignoring unreadable cache file: " + e.Message);
            }
        }

        public void Save()
        {
            try
            {
                var map = new Dictionary<string, object?>();
                foreach (var kv in _entries)
                    map[kv.Key] = new Dictionary<string, object?> { ["rev"] = kv.Value.Rev, ["v"] = kv.Value.Value };
                var text = FsJson.Serialize(new Dictionary<string, object?> { ["version"] = 1L, ["paths"] = map });
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(_file)) File.Delete(_file);
                File.Move(tmp, _file);
                Dirty = false;
            }
            catch (Exception e)
            {
                _log("could not save cache: " + e.Message);
            }
        }
    }
}
