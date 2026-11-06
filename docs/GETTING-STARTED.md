# Getting started with the FreeSync client

This guide takes you from an empty folder to a working program in a few minutes. Pick your path:

- [A .NET app in VS Code](#a-net-app-in-vs-code) (console, tool or game server)
- [A BepInEx or MelonLoader mod](#a-bepinex-or-melonloader-mod)
- [A Unity project](#a-unity-project)
- [Verified players (optional)](#verified-players-optional), if your backend should vouch for players

## Before you start: a server

The client talks to **your own** FreeSync server. You need its URL and a key:

- **To try things out:** run a local server. Clone this repository, then:

  ```bash
  cd server
  npm install
  cp .dev.vars.example .dev.vars
  npm run dev
  ```

  The URL is `ws://127.0.0.1:8787`, with the read key `dev-read-key` and the write key
  `dev-write-key`.
- **For real use:** deploy it to Cloudflare for free (about 5 minutes). See
  [Deploy to Cloudflare](../README.md#deploy-to-cloudflare). The URL looks like
  `wss://freesync.<you>.workers.dev`. Use the **read key** in anything you give to players.

With the read key, the example server rules let each player write only under their own
`players/<id>` and `presence/<id>`, plus `chat/`. The examples below stick to those paths.

## A .NET app in VS Code

**1. Install the tools:**
- the [.NET SDK](https://dotnet.microsoft.com/download), version 8 or newer;
- [VS Code](https://code.visualstudio.com) with the **C# Dev Kit** extension.

**2. Create a project and add FreeSync:**

```bash
dotnet new console -n MyFreeSyncApp
cd MyFreeSyncApp
dotnet add package FreeSync
code .
```

If FreeSync isn't on nuget.org yet, use the `.nupkg` from the repository's **Releases** page
instead. Put it in a `packages` folder **beside** the `MyFreeSyncApp` folder. Then, inside
`MyFreeSyncApp`, register that folder as a package source so every machine that builds the project
can find it:

```bash
dotnet new nugetconfig
dotnet nuget add source ../packages --name local --configfile nuget.config
dotnet add package FreeSync
```

**3. Replace `Program.cs` with:**

```csharp
using FreeSync;

// Use your server's URL and READ key. These are the local `npm run dev` defaults.
const string Url = "ws://127.0.0.1:8787";
const string ReadKey = "dev-read-key";

Console.Write("Your name: ");
var name = Console.ReadLine();
if (string.IsNullOrWhiteSpace(name)) name = "guest";

var db = new FreeSyncClient(Url, room: "getting-started", key: ReadKey, new FreeSyncOptions
{
    // A secret token saved on disk gives this user the same id (db.ClientId) every time.
    ClientToken = FreeSyncClient.LoadOrCreateToken($"{name}.token"),
});

db.StateChanged += state => Console.WriteLine($"[{state}]");

// Show who's online, live. The server deletes our entry when we disconnect (see OnDisconnect below).
db.Ref("presence").OnValue(snap =>
    Console.WriteLine("online: " + string.Join(", ", snap.Children.Select(c => c.GetValue<string>()))));

// Print each new chat message.
db.Ref("chat").OnValue(snap =>
{
    var last = snap.Children.LastOrDefault();
    if (last != null) Console.WriteLine($"<{last.Child("from").GetValue<string>()}> {last.Child("text").GetValue<string>()}");
});

// Announce ourselves, and have the server remove us again when we leave (even if we crash).
var me = db.Ref($"presence/{db.ClientId}");
await me.OnDisconnect().Remove();
await me.SetAsync(name);

Console.WriteLine("Type messages and press Enter. An empty line quits.");
while (Console.ReadLine() is { Length: > 0 } text)
{
    // Push() makes a new time-ordered key, so messages sort in order.
    await db.Ref("chat").Push().SetAsync(new { from = name, text });
}

await db.CloseAsync();
```

**4. Run it:**

```bash
dotnet run
```

You can also press **F5** in VS Code to run it under the debugger. Start it again in a second
terminal with another name, and the two will chat in real time.

Next steps:
- **API overview:** [Use the C# client](../README.md#use-the-c-client) in the main README.
- **Your own types:** `SetAsync(myObject)` and `snapshot.GetValue<MyType>()`.
- **Batching:** `UpdateAsync` changes several paths in one atomic write.

## A BepInEx or MelonLoader mod

Mods run inside the game's Mono runtime, so use the **netstandard2.0** build of `FreeSync.dll`. It's a
single file with no dependencies.

**1. Get the DLL.** Use either of these:
- **Download it:** get `FreeSync-Client-<version>.zip` from the repository's **Releases** page and
  take `netstandard2.0/FreeSync.dll`. Put it in a `lib` folder in your mod project, and add this to
  your mod's `.csproj`:

  ```xml
  <ItemGroup>
    <Reference Include="FreeSync">
      <HintPath>lib\FreeSync.dll</HintPath>
    </Reference>
  </ItemGroup>
  ```

- **Use NuGet:** `dotnet add package FreeSync`. Class libraries don't copy NuGet DLLs to their
  output by default, so also add `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` to
  the `<PropertyGroup>` if you want `FreeSync.dll` next to your mod's DLL after building.

**2. Ship it.** Put `FreeSync.dll` next to your mod's DLL:
- BepInEx: `BepInEx/plugins/YourMod/`
- MelonLoader: `UserLibs/` (or `Mods/`)

**3. Use it from your plugin.** Unity APIs only work on the main thread, so queue callbacks and run
them from `Update()`:

```csharp
using BepInEx;
using FreeSync;
using UnityEngine;

[BepInPlugin("com.you.freesyncmod", "FreeSync Mod", "1.0.0")]
public class FreeSyncMod : BaseUnityPlugin
{
    private FreeSyncClient? _db;

    private void Awake()
    {
        var token = FreeSyncClient.LoadOrCreateToken(System.IO.Path.Combine(Paths.ConfigPath, "freesync.token"));
        _db = new FreeSyncClient("wss://freesync.you.workers.dev", "global", "your-read-key", new FreeSyncOptions
        {
            ClientToken = token,
            EventDispatch = EventDispatchMode.Queued,  // callbacks wait for Dispatch() below
            Log = msg => Logger.LogDebug(msg),
        });

        _db.Ref("players").OnValue(snap => Logger.LogInfo($"{snap.ChildrenCount} players"));
        _db.StateChanged += state => Logger.LogInfo($"FreeSync: {state}");
        _ = _db.Ref($"players/{_db.ClientId}/name").SetAsync(SystemInfo.deviceName);
    }

    private void Update() => _db?.Dispatch();   // runs FreeSync callbacks on the main thread

    private void OnDestroy() => _db?.Dispose();
}
```

For MelonLoader, the same code goes in a `MelonMod`: create the client in `OnInitializeMelon()`,
call `Dispatch()` in `OnUpdate()`, and `Dispose()` in `OnDeinitializeMelon()`.

Tips:
- **Read key only:** put the **read** key in your mod. Anyone can extract it, which is fine because
  the server rules decide what it can write. Never ship the write key.
- **Logging:** pass `Log = ...` to see connection details in the game's log.
- **Stripped games:** if the game is built with IL2CPP and strips code, keep your data classes from
  being stripped, or write dictionaries and anonymous objects instead.

## Verified players (optional)

By default a player's id comes from a token on their own PC, so it's stable but self-chosen. If you
run a backend that knows who players are, it can sign an **identity token**: a game server, an SPT
server mod, a website with logins, or a Discord bot. The server then treats the id as proven.

1. **Choose a secret** and give it to both sides. On the Worker, put `IDENTITY_SECRET=...` in
   `server/.deploy-keys.env` and deploy with `--secrets-file` (locally, it's already in `.dev.vars`).
   Your backend keeps the same secret, and **never** your mod.
2. **Issue a token on your backend** when a player logs in:

   ```csharp
   string token = FreeSyncIdentity.Create(identitySecret, playerId, TimeSpan.FromHours(24));
   ```

   Backends in other languages can use any HS256 JWT library with `{"sub": playerId}` and an expiry,
   or call `POST /identity` with the write key. For testing: `npm run identity -- --dev --player alice`.
3. **Pass it to the client** in the mod:

   ```csharp
   var db = new FreeSyncClient(url, room, readKey, new FreeSyncOptions
   {
       IdentityTokenProvider = () => MyBackend.GetFreeSyncTokenAsync(),  // fresh token on every (re)connect
   });
   ```

4. **Use `$uid` in rules** for data only the proven owner may write:

   ```jsonc
   { "path": "profiles/$uid", "write": "verified" }
   ```

`db.IsVerified` tells you whether the server accepted the token. The details are in
[SECURITY.md](SECURITY.md#verified-player-identities-uid).

## A Unity project

1. Copy `netstandard2.0/FreeSync.dll` (from the Releases zip) into `Assets/Plugins/`.
2. Add a component that creates the client with `EventDispatch = EventDispatchMode.Queued` and calls
   `db.Dispatch()` in `Update()`.

[`samples/unity/FreeSyncExample.cs`](../samples/unity/FreeSyncExample.cs) is a complete
MonoBehaviour that shares a GameObject's position with everyone in a room. Its
[README](../samples/unity/README.md) has notes on IL2CPP and platforms. WebGL isn't supported.

## Troubleshooting

| Problem | Fix |
|---|---|
| Stuck on `Connecting` / `Reconnecting` | Check the URL: `ws://` for local, `wss://` for Cloudflare, with no path after the host. Is the server running? |
| `[Reconnecting]` and HTTP 401 in the log | Wrong key. |
| A write throws `FreeSyncException` with `permission_denied` | The server rules don't allow that path for the read key (see `RULES` in `server/wrangler.jsonc`). |
| Callbacks never run in Unity or a mod | With `EventDispatchMode.Queued` you must call `db.Dispatch()` every frame. |
| `dotnet add package FreeSync` can't find the package | It isn't on nuget.org yet: use the `.nupkg` from Releases as shown above. |
