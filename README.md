# Bonfire

A small, free, Firebase-style **realtime JSON database** for game mods and hobby projects. It runs
entirely on Cloudflare's free Workers plan (Durable Objects with WebSocket Hibernation), with a
dependency-free C# client for Unity mods (BepInEx, MelonLoader) and .NET apps. It also comes with a
Windows **database app** for browsing and editing data, with SQL and a usage meter.

```csharp
var db = new BonfireClient("wss://bonfire.you.workers.dev", room: "global", key: "public-read-key",
    new BonfireOptions { ClientToken = BonfireClient.LoadOrCreateToken("bonfire.token") });

db.Ref("scores").OnValue(snapshot => Console.WriteLine(snapshot.ToJson()));
await db.Ref($"players/{db.ClientId}/score").SetAsync(42);
await db.Ref($"presence/{db.ClientId}").OnDisconnect().Remove();
```

**New here?** [docs/GETTING-STARTED.md](docs/GETTING-STARTED.md) walks you from an empty folder to a
working app in VS Code, a BepInEx or MelonLoader mod, or a Unity project.

**Contents:**
[Download](#download) ·
[Features](#features) ·
[How it works](#how-it-works) ·
[Free-plan limits](#free-plan-limits) ·
[Repository](#repository) ·
[Requirements](#requirements) ·
[Try it locally](#try-it-locally-no-cloudflare-account-needed) ·
[Deploy to Cloudflare](#deploy-to-cloudflare) ·
[Use the C# client](#use-the-c-client) ·
[The database app](#the-database-app-admin-editor) ·
[Samples](#samples) ·
[Files that stay on your machine](#files-that-stay-on-your-machine) ·
[Tests](#tests) ·
[Backups](#backups) ·
[Publishing a release](#publishing-a-release) ·
[Troubleshooting](#troubleshooting) ·
[Docs](#docs) ·
[Licence](#licence)

## Download

Ready-made files are on this repository's **Releases** page (right-hand side on GitHub). There's
nothing to build:

| File | What |
|---|---|
| `Bonfire-DatabaseEditor-<version>-win-x64.zip` | **Database editor** for Windows 10/11: browse and edit data, SQL, batch submit, usage meter. Unzip and run `BonfireAdmin.exe`; no .NET install needed. |
| `Bonfire-VisualDemo-<version>-win-x64.zip` | **Visual demo** for Windows: run `BonfireVisualDemo.exe` twice to watch two clients sync. |
| `Bonfire-Client-<version>.zip` | **Client library**: `Bonfire.dll` for mods (`netstandard2.0`) and .NET apps (`net10.0`), plus serializer adapters. |
| `Bonfire.<version>.nupkg` | **NuGet package** for .NET projects: `dotnet add package Bonfire`. Use this file if the package isn't on nuget.org yet; [docs/GETTING-STARTED.md](docs/GETTING-STARTED.md) shows how. |

The apps talk to **your own** Bonfire server, so [deploy one](#deploy-to-cloudflare) first (it's free).

- **First launch:** enter the server URL (`wss://<name>.<subdomain>.workers.dev`), a room and a key,
  then press **Connect**. They're remembered for next time.
- **Where settings are saved:** in `%APPDATA%\Bonfire`. Keys are encrypted for your Windows account.
- **"Windows protected your PC":** this warning appears because the exes aren't code-signed. Choose
  **More info → Run anyway**.

## Features

- **Realtime subscriptions** to any path. You get the current value, then minimal patches when
  anything at, above or below it changes.
- **Writes:** `SetAsync`, `UpdateAsync` (atomic multi-path merge), `RemoveAsync` and `Push()`.
  - They show locally at once and roll back if the server refuses them.
  - They're batched into one message per 100 ms, and queued while offline.
- **onDisconnect** writes for presence ("player left"). They still run if a close event is missed.
- **Revisions** on every update. Reconnects resync cheaply ("not modified" when nothing changed).
- **A resilient client:**
  - auto-reconnect with exponential backoff and jitter;
  - a disk cache for cold starts;
  - graceful quota handling: it keeps the last data, shows `QuotaExceeded`, and retries after
    00:00 UTC.
- **Unity-friendly threading:** `EventDispatchMode.Queued` plus `db.Dispatch()` from `Update()`.
- **Simple security:**
  - a public read key and a secret write key;
  - per-player ids and path rules (`players/$cid`);
  - optional **verified identities**: your backend signs a token and rules trust `$uid`;
  - size and depth validation;
  - per-connection rate limits and per-room daily write budgets.
- **Ephemeral paths** for high-frequency data such as positions: broadcast to clients but never stored.
- **A database app** (Windows):
  - a live tree editor and SQL (`SELECT`/`INSERT`/`UPDATE`/`DELETE`);
  - batch mode that submits everything as one atomic request;
  - a room and account usage meter.

## How it works

```
 your mod / app (C#)  ──wss://<worker>/db/<room>──▶  Cloudflare Worker  ──▶  one Durable Object per room
                      ◀── live patches ────────────  (checks keys)           (SQLite storage, hibernating WebSockets)
 database app         ──same WebSocket + HTTP──────▶
```

- **Rooms:** each room is one JSON document held by its own Durable Object.
- **Checks:** the Worker checks keys and room names before anything reaches a Durable Object.
- **Storage:** the document is stored in SQLite as one row per subtree, so a typical small write costs
  **one row**.
- **Hibernation:** connected clients don't keep the object awake, so idle rooms cost nothing.

[docs/DESIGN.md](docs/DESIGN.md) and [docs/PROTOCOL.md](docs/PROTOCOL.md) have the details.

## Free-plan limits

These are **per Cloudflare account**: every Worker and every room on the account shares them.

| Limit | Free plan |
|---|---|
| Worker requests | 100,000 / day |
| Durable Object requests | 100,000 / day (incoming WebSocket messages count 20:1) |
| SQLite rows written | **100,000 / day**, usually the first limit to hit |
| SQLite rows read | 5,000,000 / day |
| Duration | 13,000 GB-s / day |
| Storage | 5 GB total |

They reset at 00:00 UTC.

- **Listening is almost free:** a connection costs about 2 requests, and updates pushed to clients
  cost nothing.
- **Stored writes are the scarce resource.** Each room also has its own daily write budget (25,000
  rows by default), so one busy room can't use up the account.
- **When a limit is hit,** clients keep their last data and recover after the reset.

Sources and numbers: [docs/LIMITS.md](docs/LIMITS.md).

## Repository

| Folder | What |
|---|---|
| [`server/`](server) | Cloudflare Worker + Durable Object (TypeScript), `wrangler.jsonc`, Vitest tests |
| [`client/`](client) | `Bonfire` C# library (`netstandard2.0` + `net10.0`, no dependencies), xUnit tests, optional serializer [adapters](client/adapters) |
| [`samples/admin/`](samples/admin) | **Database app** (Windows): tree editor, SQL, batch submit, usage meter |
| [`samples/visual/`](samples/visual) | Windows demo: run it twice and watch players move, score and chat live |
| [`samples/console/`](samples/console) | Console demo (any OS) |
| [`samples/unity/`](samples/unity) | MonoBehaviour example with `Dispatch()` |
| [`docs/`](docs) | [DEPLOY](docs/DEPLOY.md) · [PROTOCOL](docs/PROTOCOL.md) · [SECURITY](docs/SECURITY.md) · [LIMITS](docs/LIMITS.md) · [DESIGN](docs/DESIGN.md) |

## Requirements

| For | You need |
|---|---|
| The server | [Node.js](https://nodejs.org) 18 or newer. A free [Cloudflare account](https://dash.cloudflare.com/sign-up) to deploy (not needed to try it locally). |
| The C# client and samples | [.NET SDK 10](https://dotnet.microsoft.com/download). The library itself also targets `netstandard2.0`, so it runs in Unity/Mono. |
| The database app and visual demo | Windows 10/11. The [downloads](#download) need nothing else; building them yourself needs the .NET SDK. |

## Try it locally (no Cloudflare account needed)

**1. Start a local server**, which runs the real Workers runtime on your machine:

```bash
cd server
npm install
cp .dev.vars.example .dev.vars
npm run dev
```

On Windows PowerShell, use `copy` instead of `cp`. The server listens on `ws://127.0.0.1:8787`, with
the keys `dev-read-key` and `dev-write-key`.

**2. In another terminal, open two clients and watch them sync:**

```powershell
powershell -ExecutionPolicy Bypass -File samples\visual\run-two.ps1
```

Or use the console demo on any OS, running each command in its own terminal:

```bash
dotnet run --project samples/console -- --name alice
```

```bash
dotnet run --project samples/console -- --name bob
```

**3. Open the database app**, which connects with the local write key:

```bash
dotnet run --project samples/admin -- --connect
```

## Deploy to Cloudflare

**1. Log in** (this opens your browser):

```bash
cd server
npm install
npx wrangler login
```

**2. Choose your keys.** Create `server/.deploy-keys.env` (git-ignored) containing:

```
READ_KEY=<a public key: it ships inside your mod, so anyone can see it>
WRITE_KEY=<a long random secret: only for your admin tools>
```

Any random strings work. For example, generate one with:

```bash
node -e "console.log(require('crypto').randomBytes(24).toString('base64url'))"
```

**3. Deploy.** The first deploy uploads the keys as encrypted secrets together with the code, so the
Worker is never live without keys:

```bash
npx wrangler deploy --secrets-file .deploy-keys.env
```

Wrangler prints your URL, e.g. `https://bonfire.<your-subdomain>.workers.dev`. Clients use the same
address with `wss://`.

**4. Let the samples and database app find it.** Save the URL to `server/.deployed-url` (git-ignored):

```bash
echo wss://bonfire.<your-subdomain>.workers.dev > .deployed-url
```

From then on, the visual demo connects to your live Worker with the read key, and the database app
uses the write key. Without this file they use the local `wrangler dev` server. The console demo takes
`--url` and `--key` instead.

**5. Check it:**

```bash
curl https://bonfire.<your-subdomain>.workers.dev/
curl "https://bonfire.<your-subdomain>.workers.dev/db/global/snapshot?path=&key=<READ_KEY>"
```

**Later:**
- Deploy changes with `npm run deploy`. Connected clients stay connected.
- Change a key with `npx wrangler secret put WRITE_KEY` (or `READ_KEY`).
- Remove everything with `npx wrangler delete`.

**Who may write where** is set by the `RULES` var in [`server/wrangler.jsonc`](server/wrangler.jsonc).
The example rules let each player write only `players/<their id>`, `presence/<their id>` and `chat/*`.
The same file has all the size and rate limits. See [docs/SECURITY.md](docs/SECURITY.md) and
[docs/DEPLOY.md](docs/DEPLOY.md).

## Use the C# client

[docs/GETTING-STARTED.md](docs/GETTING-STARTED.md) has step-by-step guides. In short:

| Project | How to add Bonfire |
|---|---|
| .NET app (VS Code, Visual Studio, Rider) | `dotnet add package Bonfire` |
| BepInEx or MelonLoader mod | Reference `netstandard2.0/Bonfire.dll` from the [Releases](#download) zip, and ship it next to your mod |
| Unity project | Copy `netstandard2.0/Bonfire.dll` into `Assets/Plugins/` |
| Working on Bonfire itself | Add a project reference to `client/Bonfire/Bonfire.csproj` |

It's a single DLL with no dependencies.

```csharp
using Bonfire;

var db = new BonfireClient("wss://bonfire.you.workers.dev", "global", "your-read-key", new BonfireOptions
{
    // A secret per-install token gives this player a stable id (db.ClientId), used by rules like players/$cid.
    ClientToken = BonfireClient.LoadOrCreateToken("bonfire.token"),
    CacheDirectory = "bonfire-cache",            // optional: show last known data instantly on the next start
    EventDispatch = EventDispatchMode.Queued,     // Unity: call db.Dispatch() from Update()
});

// Listen to a path: the current value now, then every change.
IDisposable sub = db.Ref("players").OnValue(snap =>
{
    foreach (var p in snap.Children)
        Console.WriteLine($"{p.Key}: {p.Child("name").GetValue<string>()} has {p.Child("score").GetValue<int>()}");
});

// Write. Listeners update immediately; the task completes when the server confirms.
var me = db.Ref($"players/{db.ClientId}");
await me.SetAsync(new { name = "Ann", score = 0 });
await me.UpdateAsync(new Dictionary<string, object?> { ["score"] = 5, ["stats/hp"] = 100 });  // atomic, multi-path
await db.Ref("chat").Push().SetAsync(new { from = "Ann", text = "hi" });                       // time-ordered key
await me.Child("stats").RemoveAsync();

// Presence: removed by the server when this client disconnects, even after a crash.
db.StateChanged += state =>
{
    if (state == ConnectionState.Connected)
    {
        _ = db.Ref($"presence/{db.ClientId}").OnDisconnect().Remove();
        _ = db.Ref($"presence/{db.ClientId}").SetAsync(true);
    }
};

// One-off read: from a live subscription if one covers the path, otherwise over HTTP.
var motd = (await db.Ref("config/motd").GetAsync()).GetValue<string>();

sub.Dispose();
db.Dispose();   // closes cleanly; the server runs onDisconnect writes
```

**Useful members:**
- `State` and `StateChanged`: `Connecting`, `Connected`, `Reconnecting`, `QuotaExceeded` or `Offline`.
- `ErrorOccurred` and `LastError`.
- `WritesBlockedUntil`: set when the daily write quota is used up.
- `PendingWriteCount`.
- `GoOffline()` and `GoOnline()`.

Refused writes throw `BonfireException` with a `Code` such as `permission_denied`, `rate_limited` or
`write_quota`.

**Verified players (optional):** if your own backend knows who a player is (a game server, server mod,
website or bot), it can sign an identity token. Rules can then trust the id with `$uid`:

```csharp
// On your backend, which knows IDENTITY_SECRET. Never put the secret in a mod.
string token = BonfireIdentity.Create(identitySecret, playerId, TimeSpan.FromHours(24));

// In the mod, using the token from your backend:
var db = new BonfireClient(url, "global", readKey, new BonfireOptions { IdentityToken = token });
// db.ClientId == playerId, and db.IsVerified is true once connected.
```

For long sessions, use `IdentityTokenProvider` to fetch a fresh token before each reconnect. Setup,
rules and other ways to issue tokens are in
[docs/SECURITY.md](docs/SECURITY.md#verified-player-identities-uid).

**Serializers:**
- **Built-in (default):** handles primitives, lists, dictionaries and plain classes.
- **Newtonsoft.Json or System.Text.Json:** copy the matching file from
  [`client/adapters`](client/adapters) into your project.

**Unity:** see [`samples/unity`](samples/unity) for a full MonoBehaviour, plus notes on IL2CPP stripping.
WebGL isn't supported.

## The database app (admin editor)

A Windows app for looking at and editing a room's data like a database. Its
[README](samples/admin/README.md) has everything in detail.

- **Download:** get `Bonfire-DatabaseEditor-…zip` from [Releases](#download), unzip it, and run
  `BonfireAdmin.exe`. Enter your server URL, the room and your **write key**, then press
  **Connect**. They're remembered, with the key encrypted for your Windows account (untick
  **Remember** to skip saving the key).
- **From source:**

  ```bash
  dotnet run --project samples/admin -- --connect
  dotnet run --project samples/admin -- --room global --connect
  ```

  Built from source in this repository, it picks up your URL and write key from
  `server/.deployed-url` and `server/.deploy-keys.env` automatically.

- **Tree (left):** the live document, updated as players write.
  - Select a node to edit its value on the right (JSON, text, number or true/false), then press
    **Save** (Ctrl+S).
  - **Add child…** adds a key under the selected node, **Rename / move…** (F2) moves a value, and
    **Delete** (Del) removes a node.
  - If someone else changes the value you're editing, you're told rather than overwritten.
- **SQL tab:** any object is a table, its children are rows (`key` is the row key) and its fields are
  columns (`pos.x` for nested ones):

  ```sql
  SHOW TABLES
  SELECT * FROM players ORDER BY score DESC LIMIT 10
  SELECT COUNT(*) FROM chat WHERE from = 'Ann'
  INSERT INTO chat (from, text) VALUES ('admin', 'Server restarting soon')
  UPDATE players SET score = COALESCE(score, 0) + 1 WHERE name LIKE 'A%'
  DELETE FROM chat WHERE text LIKE '%spam%'
  ```

  Press F5 or Ctrl+Enter to run. The **Examples** dropdown has more.
- **Batch mode (on by default):** edits and SQL changes are **staged** (shown in orange, listed under
  **Pending changes**). **Submit** sends them all as **one atomic request**: one revision, and other
  clients see the whole batch at once. Turn it off to send each change immediately.
- **Usage tab and footer:**
  - **This room:** rows written today against its daily budget and the account's 100,000; storage; an
    estimate of requests; and a 7-day history.
  - **Exact account-wide numbers:** add a Cloudflare API token as shown below, and the tab also shows
    the account's totals against every free-plan limit.
- **Backups tab:**
  - before every submit, the editor automatically saves the old values, so **Undo last submit** puts
    them back;
  - **Export** and **Import** save and restore a room or one node as a JSON file, including restoring
    into another room.
- **Log tab:** what was sent, how many requests it took, and how long the server took to confirm.

### Account-wide usage (optional)

1. Go to https://dash.cloudflare.com/profile/api-tokens → **Create Token** → **Create Custom Token**.
2. Give it one permission, **Account → Account Analytics → Read**, for your account.
3. Paste the token and your account ID (shown by `npx wrangler whoami`, or in the dashboard's URL)
   into the fields on the **Usage** tab. They're remembered, with the token encrypted.

If you run from source, you can instead save the token as the only content of
`server/.analytics-token` and the account ID in `server/.cloudflare-account`. Both files are
git-ignored.

The token can only read analytics, and the app sends it only to `api.cloudflare.com`.

## Samples

| Sample | Run | Shows |
|---|---|---|
| [Visual demo](samples/visual) (Windows) | Run the [downloaded](#download) exe twice, or `powershell -ExecutionPolicy Bypass -File samples\visual\run-two.ps1` | Two windows: moving dots (ephemeral positions), scores, chat, presence |
| [Console](samples/console) | `dotnet run --project samples/console -- --name alice` (type `say hi`, `score 5`, `move 1 2`) | The same features in a terminal, on any OS |
| [Unity](samples/unity) | Copy the script and DLL into a project | Syncing a GameObject's position with `Dispatch()` |
| [Database app](samples/admin) (Windows) | The [downloaded](#download) exe, or `dotnet run --project samples/admin -- --connect` | Editing, SQL, batch submit, usage meter |

## Files that stay on your machine

All of these are git-ignored. Never commit them.

| File | Contains | Used by |
|---|---|---|
| `server/.dev.vars` | Local dev keys (copy of `.dev.vars.example`) | `npm run dev` |
| `server/.deploy-keys.env` | Your production `READ_KEY` and `WRITE_KEY` | First deploy; the visual demo and database app |
| `server/.deployed-url` | Your Worker's `wss://` URL | Visual demo and database app |
| `server/.analytics-token` | Cloudflare API token (Account Analytics: Read) | Database app usage meter |
| `server/.cloudflare-account` | Your Cloudflare account ID | Database app usage meter |

The downloaded apps keep their settings in `%APPDATA%\Bonfire` instead (`admin-editor.json`,
`visual-demo.json`, and `visual-demo-tokens\` for player ids). Keys and the API token there are
encrypted with Windows DPAPI, so the files are useless on another PC or Windows account. Delete the
folder to forget everything.

The **read key** is not really a secret, because it ships inside your mod. Rules decide what it can
write. The **write key** and the **API token** are secrets.

## Tests

```bash
cd server && npm test                                                   # Worker + Durable Object (Vitest, real workerd runtime)
dotnet test --project client/Bonfire.Tests/Bonfire.Tests.csproj       # C# client (xUnit)
dotnet test --project samples/admin/Tests/Bonfire.AdminEditor.Tests.csproj   # SQL engine, batching, usage meter
```

## Backups

Make regular backups, because nothing else protects your data from a bad `DELETE` or a buggy mod:

```bash
cd server
npm run backup -- --room global                       # timestamped file in server/backups/global/, keeps the last 30
npm run restore -- backups/global/<file>.json --yes   # put it back (without --yes it only shows what would change)
```

- **Daily backups:** schedule `npm run backup` with Task Scheduler or cron, as shown in
  [docs/DEPLOY.md](docs/DEPLOY.md#backups).
- **The database editor** also backs up automatically before every submit, with one-click undo, and
  can export and import the same files.

## Publishing a release

The download files are built by [`scripts/publish.ps1`](scripts/publish.ps1). It runs the C# tests,
then writes the three zips to `dist/`, which is git-ignored:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 0.1.0
```

On GitHub, the [release workflow](.github/workflows/release.yml) does this for you. Push a version
tag and it runs all the tests, builds the zips, and creates a Release with them attached:

```bash
git tag v0.1.0
git push origin v0.1.0
```

Running the workflow by hand from the Actions tab builds the same files as a downloadable artifact,
without creating a release.

The release also includes the NuGet package (`Bonfire.<version>.nupkg` and its `.snupkg` symbols).
To have the workflow publish it to **nuget.org** automatically, so `dotnet add package Bonfire` just
works:

1. Sign in at https://www.nuget.org and create an API key (**API Keys → Create**) with the **Push**
   scope, limited to the glob pattern `Bonfire`.
2. In your GitHub repository, go to **Settings → Secrets and variables → Actions → New repository
   secret** and add it as `NUGET_API_KEY`.

Without the secret, that step is skipped. The first push claims the package name `Bonfire` for your
nuget.org account.

## Troubleshooting

| Problem | Fix |
|---|---|
| Clients get HTTP **401** | Wrong key. A read-key client also can't open `/stats` (403). |
| HTTP **426** / `unsupported_version` | Client and server speak different protocol versions; update both. |
| Writes fail with `permission_denied` | The `RULES` in `wrangler.jsonc` don't allow that path for the read key. Use the write key or change the rules. |
| `QuotaExceeded`, `write_quota` or `room_quota` | A daily limit was hit; everything recovers at 00:00 UTC. Check the database app's Usage tab. |
| `dotnet` restore fails with a private feed error | The repo's `nuget.config` limits restores to nuget.org; make sure you run commands inside the repo. |
| `npm install` fails with a peer-dependency error | `server/.npmrc` sets `legacy-peer-deps`; run `npm install` inside `server/`. |
| "Testing with VSTest target is no longer supported" | Run tests from the repo, where `global.json` selects the new test runner. |
| Unity WebGL can't connect | Not supported: browsers need a JavaScript WebSocket bridge. |
| "Windows protected your PC" when starting a downloaded exe | The exes aren't code-signed. Choose **More info → Run anyway**. |
| The release workflow can't create a release | In the repository's Settings → Actions → General, allow workflows **Read and write** permissions. |

## Docs

- [docs/GETTING-STARTED.md](docs/GETTING-STARTED.md): use the client in a .NET app (VS Code), a mod, or Unity.
- [docs/DEPLOY.md](docs/DEPLOY.md): deployment, every configuration var, local development, monitoring.
- [docs/SECURITY.md](docs/SECURITY.md): keys, client ids, rules, validation, what is and isn't protected.
- [docs/PROTOCOL.md](docs/PROTOCOL.md): the WebSocket wire protocol and HTTP endpoints.
- [docs/LIMITS.md](docs/LIMITS.md): Cloudflare free-plan limits, with sources.
- [docs/DESIGN.md](docs/DESIGN.md): architecture and design decisions.

## Licence

[MIT](LICENSE). You may use, change and redistribute Bonfire, including in commercial and
closed-source mods, as long as the licence notice is kept. Every release zip and the NuGet package
include it.
