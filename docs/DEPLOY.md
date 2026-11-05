# Deploying FreeSync

Everything here runs on Cloudflare's **free** Workers plan. You need a Cloudflare account and
Node.js 18 or newer.

## One-time setup

```bash
cd server
npm install
npx wrangler login
```

Pick two keys: a public **read key** (it ships in your mod, so treat it as public) and a long random
**write key** (keep it secret). Put them in `server/.deploy-keys.env` (git-ignored):

```
READ_KEY=<public key>
WRITE_KEY=<long random secret>
```

The first deploy uploads them together with the code, so the Worker is never live without keys:

```bash
npx wrangler deploy --secrets-file .deploy-keys.env
```

To change a key later: `npx wrangler secret put READ_KEY` (or `WRITE_KEY`).

## Deploy

```bash
npm run deploy
```

Wrangler prints the URL, e.g. `https://freesync.<your-subdomain>.workers.dev`. Clients connect to
`wss://freesync.<your-subdomain>.workers.dev`. Run the same command again to deploy changes.
Connected clients stay connected across deploys.

Save the URL to `server/.deployed-url` (git-ignored). The visual demo and the database app read it,
together with the keys in `.deploy-keys.env`, so they connect to your live Worker without any
arguments:

```bash
echo wss://freesync.<your-subdomain>.workers.dev > .deployed-url
```

Check it:

```bash
curl https://freesync.<your-subdomain>.workers.dev/
curl "https://freesync.<your-subdomain>.workers.dev/db/global/snapshot?path=&key=<READ_KEY>"
curl "https://freesync.<your-subdomain>.workers.dev/db/global/stats?key=<WRITE_KEY>"
```

## Configuration

Non-secret settings are `vars` in [`server/wrangler.jsonc`](../server/wrangler.jsonc). Change them
and redeploy. All of them are optional.

| Var | Default | Meaning |
|---|---|---|
| `RULES` | read: public, write: admin | Who may write where. See [SECURITY.md](SECURITY.md). The shipped file has example rules for `players/$cid`, `presence/$cid` and `chat/*`. |
| `CHUNK_DEPTH` | `2` | Depth at which the document is split into stored rows. A write below this depth costs 1 row. **Fixed per room by its first write.** |
| `MAX_FRAME_BYTES` | `65536` | Largest incoming WebSocket frame. |
| `MAX_CHUNK_BYTES` | `1000000` | Largest stored subtree (SQLite rows max out at 2 MB). |
| `MAX_ROOM_BYTES` | `50000000` | Room storage cap. Writes that add data fail beyond it. |
| `MAX_SNAPSHOT_BYTES` | `8000000` | Largest value sent for a subscription or snapshot. |
| `ROOM_DAILY_WRITE_ROWS` | `25000` | Stored rows each room may write per UTC day. The account-wide free limit is 100,000. |
| `SNAPSHOT_MAX_AGE` | `10` | Seconds the snapshot endpoint's responses are cached. |
| `MAX_SUBS` | `64` | Subscriptions per connection. |
| `MAX_OPS_PER_FRAME` | `100` | Messages per frame. |
| `MAX_ON_DISCONNECT` | `16` | onDisconnect ops per connection. |
| `RATE_OPS_PER_SEC` / `RATE_BURST` / `RATE_BYTES_PER_SEC` | `20` / `100` / `32768` | Per-connection token bucket. `RATE_OPS_PER_SEC=0` disables it. |
| `ROOM_PATTERN` | `^[A-Za-z0-9_-]{1,64}$` | Allowed room names. Tighten it (e.g. `^(global\|lobby-[0-9]+)$`) to stop people creating rooms at random. |

## Local development

```bash
cd server
cp .dev.vars.example .dev.vars    # dev-read-key / dev-write-key
npm run dev                       # http://127.0.0.1:8787, ws://127.0.0.1:8787
```

In two other terminals:

```bash
cd samples/console
dotnet run -- --name alice
```

```bash
cd samples/console
dotnet run -- --name bob
```

Type `say hi`, `score 5` or `move 1 2` in either window and watch the other. Local dev doesn't enforce
Cloudflare's quotas.

## Tests

```bash
cd server && npm test
cd client && dotnet test --project FreeSync.Tests/FreeSync.Tests.csproj
```

## Snapshot caching on workers.dev

The snapshot endpoint caches public responses with the Workers Cache API. Cloudflare's docs only
confirm the Cache API on custom domains. **On 2026-11-04 it was verified to work on `*.workers.dev`**:
repeated reads returned `cf-cache-status: HIT`, and served the cached value until `SNAPSHOT_MAX_AGE`
expired. Cloudflare could change this, so to re-check your deployment:

1. Read a path: `curl "https://<worker>/db/global/snapshot?path=test&key=<READ_KEY>"`.
2. Change it (e.g. with the console sample using the write key: `--key <WRITE_KEY>`).
3. Read it again within `SNAPSHOT_MAX_AGE` seconds. If you get the **old** value, caching works.

If it doesn't, everything still works; each snapshot read just costs a Durable Object request. To
get caching, add a [custom domain](https://developers.cloudflare.com/workers/configuration/routing/custom-domains/)
to the Worker (it needs a domain on Cloudflare, which is free to add).

## Watching your quota

- `GET /db/<room>/stats` (write key): per-room counters for the last 7 UTC days. Deployed rooms
  hibernate within seconds of going idle, which wipes in-memory counters. So counters are stored inside
  writes that happen anyway (first write after waking, every 20 writes, once a minute while writing)
  and when the room's last client leaves. That costs at most ~5% extra rows. Writes made since the
  last save can still be lost when the room sleeps, so the numbers undercount slightly; treat them as
  approximate. The per-room daily write budget uses the same counters.
- The Cloudflare dashboard (Workers & Pages → your Worker → Metrics, and Durable Objects) shows exact
  account-wide request, duration and storage numbers.
- The admin editor (`samples/admin`) has a Usage tab. It shows this room's meters, and with an
  analytics API token it also shows exact account-wide usage against every free-plan limit. See
  [its README](../samples/admin/README.md#whole-account-usage-optional).
- When a free limit is hit, clients switch to `QuotaExceeded` and keep showing their last data. Reads
  of cached data keep working, writes fail fast, and everything recovers after 00:00 UTC. See
  [LIMITS.md](LIMITS.md).

## Removing it

```bash
npx wrangler delete
```

This deletes the Worker. Durable Object data is deleted with it.
