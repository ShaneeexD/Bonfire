using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BonfireSamples;

/// <summary>
/// Per-user settings for the sample apps, in %APPDATA%\Bonfire\&lt;app&gt;.json, so a downloaded exe remembers
/// its server between runs. Secrets (write key, API token) are encrypted with Windows DPAPI for the current
/// user: the file is useless on another PC or Windows account.
/// </summary>
internal sealed class SavedSettings
{
    private readonly string _file;
    private readonly Dictionary<string, string> _values;

    private SavedSettings(string file, Dictionary<string, string> values)
    {
        _file = file;
        _values = values;
    }

    public static string Folder => LazyFolder.Value;

    private static readonly Lazy<string> LazyFolder = new(() =>
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "Bonfire");
        // One-time move from the project's former name, so remembered keys, tokens and backups survive the rename.
        var legacy = Path.Combine(appData, "FreeSync");
        if (!Directory.Exists(folder) && Directory.Exists(legacy))
        {
            try
            {
                CopyTree(legacy, folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Starting fresh is acceptable; the old folder is left untouched either way.
            }
        }
        return folder;
    });

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    public static SavedSettings Load(string app)
    {
        var file = Path.Combine(Folder, app + ".json");
        try
        {
            if (File.Exists(file))
                return new SavedSettings(file, JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new());
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file just means starting from defaults.
        }
        return new SavedSettings(file, new Dictionary<string, string>());
    }

    public bool Exists => _values.Count > 0;

    public string? Get(string key) => _values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) _values.Remove(key);
        else _values[key] = value;
    }

    /// <summary>Reads a value stored with <see cref="SetSecret"/>; null if missing or not decryptable here.</summary>
    public string? GetSecret(string key)
    {
        var stored = Get(key);
        if (stored == null) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null; // copied from another PC/user, or corrupted
        }
    }

    public void SetSecret(string key, string? value) =>
        Set(key, string.IsNullOrEmpty(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)));

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(_file, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember settings is not worth interrupting the user for.
        }
    }
}
