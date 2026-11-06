using System.Globalization;
using FreeSync.Json;

namespace FreeSyncAdmin;

/// <summary>
/// A backup is a list of "path = value" entries plus some metadata. Restoring it stages those entries as
/// changes. An export has one entry (the exported node); a before-submit backup has the old value of every path
/// a batch was about to change, so restoring it undoes that batch.
/// </summary>
public sealed record BackupFile(string Room, string Kind, DateTime CreatedUtc, long Rev, string Note, IReadOnlyList<(string[] Path, object? Value)> Entries)
{
    public const string Format = "freesync-backup";
    public const string KindExport = "export";
    public const string KindBeforeSubmit = "before-submit";
    public const string KindPlainJson = "json";

    public string Describe() =>
        $"{CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {Kind,-13}  room {Room}  " +
        (Entries.Count == 1 ? "/" + JsonUtil.Join(Entries[0].Path) : $"{Entries.Count} paths") +
        (Note.Length > 0 ? "  " + Note : "");

    public string ToJson() => FsJson.Serialize(new Dictionary<string, object?>
    {
        ["format"] = Format,
        ["version"] = 1L,
        ["room"] = Room,
        ["kind"] = Kind,
        ["createdAt"] = CreatedUtc.ToString("o", CultureInfo.InvariantCulture),
        ["rev"] = Rev,
        ["note"] = Note,
        ["entries"] = Entries.Select(e => (object?)new Dictionary<string, object?> { ["path"] = JsonUtil.Join(e.Path), ["value"] = e.Value }).ToList(),
    });

    /// <summary>
    /// Reads a backup file. Any other JSON (for example a snapshot saved with curl) is accepted too, as a
    /// single value to restore at <paramref name="plainJsonPath"/>.
    /// </summary>
    public static BackupFile Parse(string json, string[] plainJsonPath)
    {
        var tree = FsJson.Parse(json);
        if (tree is Dictionary<string, object?> d && d.TryGetValue("format", out var f) && f as string == Format)
        {
            var entries = new List<(string[], object?)>();
            if (d.TryGetValue("entries", out var list) && list is List<object?> items)
            {
                foreach (var item in items.OfType<Dictionary<string, object?>>())
                {
                    var path = item.TryGetValue("path", out var p) && p is string ps ? JsonUtil.ParsePath(ps) : Array.Empty<string>();
                    item.TryGetValue("value", out var v);
                    entries.Add((path, v));
                }
            }
            var created = d.TryGetValue("createdAt", out var c) && c is string cs &&
                          DateTime.TryParse(cs, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
                ? when
                : DateTime.MinValue;
            return new BackupFile(
                d.TryGetValue("room", out var r) && r is string rs ? rs : "?",
                d.TryGetValue("kind", out var k) && k is string ks ? ks : KindExport,
                created,
                d.TryGetValue("rev", out var rv) && rv is long l ? l : 0,
                d.TryGetValue("note", out var n) && n is string ns ? ns : "",
                entries);
        }
        return new BackupFile("?", KindPlainJson, DateTime.MinValue, 0, "", new[] { (plainJsonPath, tree) });
    }

    /// <summary>The current value of every path <paramref name="changes"/> would touch: restoring it is an undo.</summary>
    public static BackupFile BeforeChanges(ChangeSet changes, Func<string[], object?> currentValue, string room, long rev, string note) =>
        new(room, KindBeforeSubmit, DateTime.UtcNow, rev, note, changes.Entries.Select(e => (e.Path, currentValue(e.Path))).ToList());

    /// <summary>The entries as changes, optionally moving a single-entry backup to another path.</summary>
    public ChangeSet ToChangeSet(string[]? retarget = null)
    {
        var cs = new ChangeSet();
        if (retarget != null && Entries.Count == 1) cs.Add(retarget, Entries[0].Value);
        else foreach (var (p, v) in Entries) cs.Add(p, v);
        return cs;
    }

    // ---- files on disk -----------------------------------------------------------------------------

    /// <summary>%APPDATA%\FreeSync\backups\&lt;room&gt;: where automatic before-submit backups go.</summary>
    public static string FolderFor(string room) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeSync", "backups",
            new string(room.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray()));

    /// <summary>Writes the backup into <paramref name="folder"/> and deletes the oldest beyond <paramref name="keep"/>.</summary>
    public string SaveTo(string folder, int keep)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{Room}-{Kind}-{CreatedUtc:yyyyMMdd-HHmmss-fff}.json");
        File.WriteAllText(file, ToJson());
        foreach (var old in Directory.GetFiles(folder, "*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep))
        {
            try { File.Delete(old); } catch (IOException) { }
        }
        return file;
    }

    public static IEnumerable<(string File, BackupFile Backup)> ListFolder(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderByDescending(f => f, StringComparer.Ordinal))
        {
            BackupFile? b = null;
            try { b = Parse(File.ReadAllText(file), Array.Empty<string>()); }
            catch (Exception e) when (e is IOException or FormatException) { }
            if (b != null) yield return (file, b);
        }
    }
}
