// FreeSync console demo: run two copies and watch them sync.
//
//   dotnet run -- --name alice
//   dotnet run -- --name bob
//
// Commands: say <text> | score <n> | move <x> <y> | quit
// Options:  --url ws://127.0.0.1:8787  --room demo  --key dev-read-key  --script (run a fixed scenario and exit)

using FreeSync;

var name = Arg("--name") ?? "player";
var url = Arg("--url") ?? "ws://127.0.0.1:8787";
var room = Arg("--room") ?? "demo";
var key = Arg("--key") ?? "dev-read-key";
var script = args.Contains("--script");

// A stable secret token per player name, so the client id survives restarts.
var tokenFile = Path.Combine(Path.GetTempPath(), "freesync-demo", name + ".token");
var db = new FreeSyncClient(url, room, key, new FreeSyncOptions
{
    ClientToken = FreeSyncClient.LoadOrCreateToken(tokenFile),
    EventDispatch = EventDispatchMode.Queued, // callbacks run on our loop below, like Unity's Update()
    Log = msg => { if (args.Contains("--verbose")) Console.WriteLine(msg); },
});
var me = db.ClientId!;
Print($"I am {name} (client id {me}) in room '{room}'");

db.StateChanged += state =>
{
    Print($"connection: {state}");
    // Presence is removed by the server when we disconnect, so set it again on every (re)connect.
    if (state == ConnectionState.Connected) _ = SetPresence();
};
db.ErrorOccurred += e => Print($"error: {e.Code} {e.Message}");

db.Ref("presence").OnValue(snap =>
    Print("online: " + string.Join(", ", snap.Children.Select(c => c.GetValue<string>()))));
db.Ref("players").OnValue(snap =>
{
    foreach (var p in snap.Children)
        Print($"player {p.Child("name").GetValue<string>()}: score={p.Child("score").GetValue<int>()} pos={p.Child("pos").ToJson()}");
}, e => Print("players: " + e.Message));
db.Ref("chat").OnValue(snap =>
{
    var last = snap.Children.LastOrDefault();
    if (last != null) Print($"chat <{last.Child("from").GetValue<string>()}> {last.Child("text").GetValue<string>()}");
});

async Task SetPresence()
{
    try
    {
        await db.Ref($"presence/{me}").OnDisconnect().Remove();
        await db.Ref($"presence/{me}").SetAsync(name);
        await db.Ref($"players/{me}/name").SetAsync(name);
    }
    catch (FreeSyncException e)
    {
        Print($"presence failed: {e.Code} {e.Message}");
    }
}

async Task Run(string line)
{
    var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return;
    try
    {
        switch (parts[0])
        {
            case "say":
                await db.Ref("chat").Push().SetAsync(new Dictionary<string, object?> { ["from"] = name, ["text"] = parts.ElementAtOrDefault(1) ?? "" });
                break;
            case "score":
                await db.Ref($"players/{me}/score").SetAsync(int.Parse(parts[1]));
                break;
            case "move":
                var xy = parts[1].Split(' ');
                // Ephemeral path: broadcast to others but never stored, so it costs no storage writes.
                await db.Ref($"players/{me}/pos").SetAsync(new { x = double.Parse(xy[0]), y = double.Parse(xy[1]) });
                break;
            default:
                Print("commands: say <text> | score <n> | move <x> <y> | quit");
                break;
        }
    }
    catch (FreeSyncException e)
    {
        Print($"refused: {e.Code} {e.Message}");
    }
}

if (script)
{
    // Fixed scenario for automated end-to-end checks.
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (db.State != ConnectionState.Connected && DateTime.UtcNow < deadline) { db.Dispatch(); await Task.Delay(20); }
    await Task.Delay(300);
    foreach (var cmd in new[] { $"say hello from {name}", "score 7", "move 1.5 2", "score 9" })
    {
        Print($"> {cmd}");
        await Run(cmd);
        for (var i = 0; i < 15; i++) { db.Dispatch(); await Task.Delay(20); }
    }
    // Writing outside our own subtree must be refused by the server's rules.
    await Run("noop");
    try { await db.Ref("players/someone-else/score").SetAsync(1); }
    catch (FreeSyncException e) { Print($"as expected, writing another player's data was refused: {e.Code}"); }
    for (var i = 0; i < 25; i++) { db.Dispatch(); await Task.Delay(20); }
    await db.CloseAsync();
    db.Dispatch();
    return;
}

// Interactive mode: read commands on a background thread, dispatch callbacks on this one.
var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
var quit = false;
_ = Task.Run(() =>
{
    string? l;
    while ((l = Console.ReadLine()) != null && l != "quit") lines.Enqueue(l);
    quit = true;
});
while (!quit)
{
    db.Dispatch();
    while (lines.TryDequeue(out var l)) _ = Run(l);
    await Task.Delay(16);
}
await db.CloseAsync();

string? Arg(string flag)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

void Print(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{name}] {s}");
