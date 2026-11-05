# FreeSync

A small, free, Firebase-style **realtime JSON database** for game mods and hobby projects. It runs
entirely on Cloudflare's free Workers plan (Durable Objects with WebSocket Hibernation), with a
dependency-free C# client for Unity mods (BepInEx, MelonLoader) and .NET apps.

```csharp
var db = new FreeSyncClient("wss://freesync.you.workers.dev", room: "global", key: "public-read-key",
    new FreeSyncOptions { ClientToken = FreeSyncClient.LoadOrCreateToken("freesync.token") });

db.Ref("scores").OnValue(snapshot => Console.WriteLine(snapshot.ToJson()));
await db.Ref($"players/{db.ClientId}/score").SetAsync(42);
await db.Ref($"presence/{db.ClientId}").OnDisconnect().Remove();
```

## Features

- **Realtime subscriptions** to any path. You get the current value, then minimal patches when
  anything at, above or below it changes.
- **Writes:** `SetAsync`, `UpdateAsync` (atomic multi-path merge), `RemoveAsync`, `Push()`. Writes
  apply locally at once and roll back if refused. They are batched into one frame per 100 ms and
  queued while offline.
- **onDisconnect** writes for presence ("player left"). They still run if a close event is missed.
- **Revisions** on every update. Reconnects resync cheaply ("not modified" when nothing changed).
- **Resilient client:** auto-reconnect with exponential backoff and jitter, a disk cache for cold
  starts, and graceful quota handling (keeps last data, shows `QuotaExceeded`, retries after
  00:00 UTC).
- **Unity-friendly threading:** `EventDispatchMode.Queued` + `db.Dispatch()` from `Update()`.
- **Simple security:** a public read key, a secret write key, per-player ids, path rules
  (`players/$cid`), size and depth validation, per-connection rate limits, and per-room daily write
  budgets.
- **Ephemeral paths** for high-frequency data such as positions: broadcast but never stored.
- **Snapshot endpoint** (`GET /db/<room>/snapshot?path=`) with ETag and short caching, plus an admin
  stats endpoint.

## Free-plan reality check

The free plan allows 100k Worker requests, 100k Durable Object requests and **100k stored row
writes per day for the whole account**. Listening is almost free: each connection costs about 2
requests, and updates pushed to clients cost nothing. **Stored writes are the scarce resource.**
FreeSync is designed so a typical small write costs one row. See [docs/LIMITS.md](docs/LIMITS.md) for
the numbers and sources.

## Repository

| Folder | What |
|---|---|
| [`server/`](server) | Cloudflare Worker + Durable Object (TypeScript), `wrangler.jsonc`, Vitest tests |
| [`client/`](client) | `FreeSync` C# library (`netstandard2.0` + `net10.0`, no dependencies), xUnit tests, optional serializer [adapters](client/adapters) |
| [`samples/console/`](samples/console) | Two-instance console demo |
| [`samples/unity/`](samples/unity) | MonoBehaviour example with `Dispatch()` |
| [`samples/visual/`](samples/visual) | Windows demo: run it twice and watch players move, score and chat live |
| [`samples/admin/`](samples/admin) | Windows admin editor: live tree editing, SQL (`SELECT`/`INSERT`/`UPDATE`/`DELETE`), batch submit as one atomic request |
| [`docs/`](docs) | [DEPLOY](docs/DEPLOY.md) · [PROTOCOL](docs/PROTOCOL.md) · [SECURITY](docs/SECURITY.md) · [LIMITS](docs/LIMITS.md) · [DESIGN](docs/DESIGN.md) |

## Quick start

```bash
cd server
npm install
npx wrangler login
npx wrangler secret put READ_KEY     # public, ships in your mod
npx wrangler secret put WRITE_KEY    # private, admin tools only
npm run deploy
```

Then reference `client/FreeSync` (or its built DLL) and connect to the printed URL with `wss://`.
Full instructions, configuration and local development are in [docs/DEPLOY.md](docs/DEPLOY.md).

## Development

```bash
cd server && npm install && npm test                                   # Worker + DO tests (Vitest, real workerd)
cd client && dotnet test --project FreeSync.Tests/FreeSync.Tests.csproj # client tests (xUnit)
cd server && npm run dev                                               # local server on :8787
cd samples/console && dotnet run -- --name alice                       # and again with --name bob
```
