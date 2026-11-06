// Bonfire admin editor: browse and edit a room's JSON document as a tree or with SQL.
//
//   dotnet run        options: --url --room --key --path, --connect (connect at start),
//                     --sql "query" (put a query in the SQL box), --run (run it once connected)
//
// Settings, later ones winning: built-in defaults (`wrangler dev` on 127.0.0.1:8787 with dev-write-key), what you
// used last time (%APPDATA%\Bonfire\admin-editor.json; keys encrypted for your Windows account), this checkout's
// server/.deployed-url + server/.deploy-keys.env (git-ignored), then command-line options.

using BonfireSamples;

namespace BonfireAdmin;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new EditorForm(AdminSettings.FromArgs(args)));
    }
}

internal sealed class AdminSettings
{
    public string Url = "ws://127.0.0.1:8787";
    public string Room = "visual-demo";
    public string Key = "dev-write-key";
    public string RootPath = "";
    public string Source = "local wrangler dev";
    public string AccountId = "";
    public string AnalyticsToken = "";
    public bool Connect;
    public string? Sql;
    public bool RunSql;

    public static AdminSettings FromArgs(string[] args)
    {
        var s = new AdminSettings();
        var saved = SavedSettings.Load("admin-editor");
        if (saved.Exists)
        {
            s.Url = saved.Get("url") ?? s.Url;
            s.Room = saved.Get("room") ?? s.Room;
            s.RootPath = saved.Get("path") ?? s.RootPath;
            s.Key = saved.GetSecret("key") ?? s.Key;
            s.AccountId = saved.Get("accountId") ?? s.AccountId;
            s.AnalyticsToken = saved.GetSecret("analyticsToken") ?? s.AnalyticsToken;
            s.Source = "your last session";
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var keys = Path.Combine(dir.FullName, "server", ".deploy-keys.env");
            var url = Path.Combine(dir.FullName, "server", ".deployed-url");
            if (!File.Exists(keys) || !File.Exists(url)) continue;
            var writeKey = File.ReadAllLines(keys).FirstOrDefault(l => l.StartsWith("WRITE_KEY="))?["WRITE_KEY=".Length..].Trim();
            if (string.IsNullOrEmpty(writeKey)) break;
            s.Url = File.ReadAllText(url).Trim();
            s.Key = writeKey;
            s.Source = "your live deployment (write key)";
            // Optional, for the account-wide usage meter (both files are git-ignored).
            var account = Path.Combine(dir.FullName, "server", ".cloudflare-account");
            var token = Path.Combine(dir.FullName, "server", ".analytics-token");
            if (File.Exists(account)) s.AccountId = File.ReadAllText(account).Trim();
            if (File.Exists(token)) s.AnalyticsToken = File.ReadAllText(token).Trim();
            break;
        }
        string? Arg(string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        s.Url = Arg("--url") ?? s.Url;
        s.Room = Arg("--room") ?? s.Room;
        s.Key = Arg("--key") ?? s.Key;
        s.RootPath = Arg("--path") ?? s.RootPath;
        s.Connect = args.Contains("--connect") || args.Contains("--run");
        s.Sql = Arg("--sql");
        s.RunSql = args.Contains("--run");
        return s;
    }
}
