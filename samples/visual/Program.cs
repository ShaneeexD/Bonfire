// FreeSync visual demo: run two (or more) copies and watch them sync in real time.
//
//   dotnet run                         (or run-two.ps1 to open two windows)
//   dotnet run -- --name Ann --auto    (options: --url --room --key --name --auto --autoconnect)
//
// Settings, later ones winning: built-in defaults (`wrangler dev` on 127.0.0.1:8787), what you used last time
// (%APPDATA%\FreeSync\visual-demo.json), this checkout's server/.deployed-url + server/.deploy-keys.env
// (git-ignored, created when you deployed), then command-line options.

using FreeSyncSamples;

namespace FreeSyncVisualDemo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new DemoForm(DemoSettings.FromArgs(args)));
    }
}

internal sealed class DemoSettings
{
    public string Url = "ws://127.0.0.1:8787";
    public string Room = "visual-demo";
    public string Key = "dev-read-key";
    public string Name = "Player" + Random.Shared.Next(100, 999);
    public bool Auto;
    public bool AutoConnect;
    public string Source = "local wrangler dev";

    public static DemoSettings FromArgs(string[] args)
    {
        var s = new DemoSettings();
        var saved = SavedSettings.Load("visual-demo");
        if (saved.Exists)
        {
            s.Url = saved.Get("url") ?? s.Url;
            s.Room = saved.Get("room") ?? s.Room;
            s.Key = saved.GetSecret("key") ?? s.Key;
            s.Source = "your last session";
        }
        s.LoadDeployment();
        string? Arg(string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        s.Url = Arg("--url") ?? s.Url;
        s.Room = Arg("--room") ?? s.Room;
        s.Key = Arg("--key") ?? s.Key;
        s.Name = Arg("--name") ?? s.Name;
        s.Auto = args.Contains("--auto");
        s.AutoConnect = args.Contains("--autoconnect");
        return s;
    }

    /// <summary>Uses the live deployment's URL and read key if this checkout has them (both files are git-ignored).</summary>
    private void LoadDeployment()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var keys = Path.Combine(dir.FullName, "server", ".deploy-keys.env");
            var url = Path.Combine(dir.FullName, "server", ".deployed-url");
            if (!File.Exists(keys) || !File.Exists(url)) continue;
            var readKey = File.ReadAllLines(keys).FirstOrDefault(l => l.StartsWith("READ_KEY="))?.Substring("READ_KEY=".Length).Trim();
            var deployed = File.ReadAllText(url).Trim();
            if (string.IsNullOrEmpty(readKey) || string.IsNullOrEmpty(deployed)) return;
            Url = deployed;
            Key = readKey;
            Source = "your live deployment";
            return;
        }
    }
}
