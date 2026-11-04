# FreeSync design plan (Milestone 1, for review)

The numbers behind these decisions are in [LIMITS.md](LIMITS.md).

## 1. The constraint that drives everything

On Free, listening is almost free: an open socket costs 1 Worker request and 1 DO request, and every
update pushed to it costs nothing. The scarce resource is **SQLite rows written: 100,000/day for the
whole account**. Incoming messages (20:1) and connections come second. So the design:

- makes a typical write cost **exactly one row**,
- avoids indexes and per-write bookkeeping rows,
- never keeps the DO awake with timers, and
- has clients batch writes and back off hard.

## 2. Architecture

```
 C# client ──wss /db/<room>?v=1 (key in header)──▶ Worker (index.ts)
                                                   │ 1. route + validate key → role (reject here: no DO cost)
 HTTP GET /db/<room>/snapshot?path= ─────────────▶ │ 2. snapshot: Cache API lookup first
 HTTP GET /db/<room>/stats (write key) ──────────▶ │ 3. forward to env.ROOM.get(idFromName(room))
                                                   ▼
                                        RoomDO (SQLite-backed, one per room)
                                        ├─ ctx.acceptWebSocket(ws, [connId])   (Hibernation API)
                                        ├─ webSocketMessage / webSocketClose / webSocketError
                                        ├─ attachment: {cid, role, subs, rateBucket}  (≤16 KB)
                                        └─ SQLite: nodes, meta, ondisconnect
```

No timers, alarms, outgoing sockets or `waitUntil` in the DO. All work happens inside the handler for
the event that caused it, so the object is eligible to hibernate the moment it returns.

## 3. Storage: chunked subtrees

```sql
CREATE TABLE nodes (key TEXT PRIMARY KEY, val TEXT NOT NULL, rev INTEGER NOT NULL) WITHOUT ROWID;
CREATE TABLE meta  (k TEXT PRIMARY KEY, v TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE ondisconnect (conn TEXT, seq INTEGER, op TEXT NOT NULL, PRIMARY KEY (conn, seq)) WITHOUT ROWID;
```

- The tree is cut at a fixed **chunk depth** `D` (config `CHUNK_DEPTH`, default **2**). Every node at
  depth `D` is one row holding its whole subtree as JSON. A scalar or array *above* depth `D` is a row
  at its own path. Interior objects above `D` are implicit (they exist if any row is below them).
  Example with D=2: `players/abc` → one row `{"name":"…","x":1,"y":2}`.
- **Write below D** (`players/abc/x`): read 1 row, modify, write 1 row. **Cost: 1 row.**
- **Write at or above D** (`players` = {...}): delete the rows under the prefix and insert one row per
  depth-D child. The cost grows with the size of the write. That is fair and explained in the error
  docs.
- `WITHOUT ROWID` with the path as primary key means there is no secondary index. Index rows count
  as extra rows written, so avoiding them matters. Prefix reads use key ranges
  (`key >= 'a/b/' AND key < 'a/b0'`), not `LIKE` (patterns are limited to 50 bytes). M2 tests assert
  `cursor.rowsWritten` so we catch any hidden extra rows.
- Max chunk size is configurable (default **1 MB**, under the 2 MB row limit). A write that would
  push a chunk past it is rejected with `chunk_too_large`.
- **Arrays are opaque values.** Paths address object keys only, and a write *into* an array is
  rejected. This keeps diffs and storage simple. Firebase's array-as-object behaviour is a known source
  of confusion.
- Path rules: segments are non-empty UTF-8, may not contain `/ . # $ [ ]` or control characters. Max
  depth 32, max path 512 bytes. Values have max nesting depth 32, no non-finite numbers, and are
  normalised (empty objects/null = delete, as in Firebase).
- While the DO is awake, a small in-memory chunk cache avoids re-reading rows. It is lost on
  hibernation, which is fine because SQLite is the source of truth.

## 4. Revisions without an extra row per write

Each document has a monotonic `rev`. Every `snap`, `patch`, `ack` and snapshot `ETag` carries it.
Revisions only need to increase, not be contiguous. So the DO **reserves them in blocks**:

- On the first write after a wake, read `meta.revHi`. Use it as the next rev and write
  `revHi + 1,000,000` back **in the same transaction** as the data. That is one extra row per wake,
  not per write.
- Later writes in the same wake increment in memory. If the object is evicted mid-block, the unused
  remainder is skipped. Revs stay monotonic. 2^53 leaves room for ~9 billion wakes.
- Each `nodes` row also stores the rev that last wrote it. `meta.removeRev` records the rev of the
  last delete. Together they let the server answer "has anything under `p` changed since rev N?" on
  reconnect without tombstones: `max(row.rev under p) ≤ N && removeRev ≤ N` → `notModified`.

Within one socket, messages are ordered, so a client can't miss a patch mid-connection. The rev is
used for ordering/de-duplication, resync after reconnect, and HTTP ETags.

## 5. Subscriptions and diffs

- `sub(p)`: the server records `p` in the socket attachment and replies with `snap(p, value, rev)`.
  If the client sends `since`, it may get `snap {notModified}` instead.
- A write at `w` affects subscription `s` if `w` is at, above, or below `s`. Each accepted frame
  produces **at most one outgoing `patch` per connection**. It holds the normalised write ops, each
  **trimmed to the subscriber's subtree**. A subscriber of `players/abc` gets `["players/abc", {...}]`,
  never the whole `players` object. The ops themselves are the minimal diff; we do not compute tree
  diffs.
- Overlapping subscriptions on one socket receive each change once. The client fans it out to its
  local listeners.
- Writers get their own changes too (as a `patch`), plus an `ack`. The client applies its writes
  optimistically and reconciles on `ack`/`err`.
- Attachment budget: subscriptions are capped (default 64 paths, ≤ 8 KB total) so the attachment
  stays well under 16 KB. Exceeding the cap gives `err too_many_subs`.

## 6. Write batching / coalescing

- **Client-side (the big lever):** writes queue for `WriteInterval` (default 100 ms). Pending ops on
  the same path, or on a descendant of a newer set, are collapsed, then sent as **one frame**: a JSON
  array of ops = 1 incoming message = 1/20 of a DO request.
- **Server-side, within one frame:** all ops in a frame are applied in one `transactionSync`. Each
  touched chunk is written **once** however many ops hit it, and one patch goes to each subscriber.
- **Not across frames.** Buffering writes across messages needs a timer (blocks hibernation, costs
  duration) or an alarm (costs a DO request + a row per flush). Either way, buffered data is lost if
  the object is evicted. ⚠ **This adjusts the requirement**: server coalescing happens per frame, not
  per time window.

## 7. Wire protocol (v1) — detailed in PROTOCOL.md during M2

Text frames, JSON, short keys. A client frame is one message or an array of messages.

| Client → server | Example |
|---|---|
| `sub` | `{"t":"sub","id":1,"p":"scores","since":1200}` |
| `unsub` | `{"t":"unsub","p":"scores"}` |
| `set` | `{"t":"set","id":2,"p":"scores/p1","v":42}` |
| `upd` (multi-path merge) | `{"t":"upd","id":3,"p":"players/abc","v":{"x":1,"inv/sword":true,"hp":null}}` |
| `rm` | `{"t":"rm","id":4,"p":"chat/old"}` |
| `od` (onDisconnect) | `{"t":"od","id":5,"op":"set\|upd\|rm\|cancel","p":"presence/me","v":false}` |
| `ping` | the exact string `{"t":"ping"}`, answered by `setWebSocketAutoResponse` without waking the DO |

| Server → client | Example |
|---|---|
| `welcome` | `{"t":"welcome","v":1,"cid":"k3f9…","role":"public","rev":1240,"lim":{…}}` |
| `snap` | `{"t":"snap","id":1,"p":"scores","v":{…},"rev":1240}` / `{…,"nm":true}` |
| `patch` | `{"t":"patch","rev":1241,"ops":[["scores/p1",42],["scores/p2",null]]}` |
| `ack` | `{"t":"ack","id":2,"rev":1241}` |
| `err` | `{"t":"err","id":2,"code":"permission_denied","msg":"…"}` |
| `pong` | `{"t":"pong"}` |

- **Version:** the URL carries `?v=1` and `welcome` echoes it. An unsupported version is rejected by
  the Worker with HTTP 426.
- **`hello` is folded into the upgrade request.** The key and client token go in headers
  (`Authorization: Bearer <key>`, `X-FreeSync-Token`), with query-string fallbacks for browsers. That
  lets the Worker reject bad keys before they cost a DO request, and saves one message per
  connection. ⚠ This differs from the spec's `hello` message.
- Close codes: `4001` auth, `4008` rate-limit abuse, `4029` quota exhausted, `4400` bad protocol.
- Keep-alive: WebSocket **protocol pings** from the client (free, don't wake the DO). App `ping` is a
  fallback.

## 8. Security

- **Keys** (secrets via `wrangler secret put`): `READ_KEY` → role `public` (safe to ship in a mod),
  `WRITE_KEY` → role `admin`. The Worker compares keys in constant time and never logs them.
- **Client identity:** each client generates a random secret token once and stores it.
  `cid = base32(SHA-256(room + token))[0..16]`. Others can't claim your `cid` without your token, and
  there are no accounts. Rules can then say `players/$cid`.
- **Rules** (`RULES` JSON var, optional per-room-pattern sections). The most specific path wins:
  ```jsonc
  { "default": { "read": "public", "write": "admin" },
    "paths": [
      { "path": "players/$cid", "write": "public" },
      { "path": "chat/*",       "write": "public", "maxBytes": 512 } ] }
  ```
  Writes and `onDisconnect` registrations are both checked. A write above a protected subtree
  (for example `set players = …` by a public client) is checked against every rule beneath it.
- **Validation:** path syntax/depth, value depth, frame size (default 64 KB), chunk size, and room
  size (`ctx.storage.sql.databaseSize` vs `MAX_ROOM_BYTES`, default 50 MB).
- **Rate limiting:** a token bucket per connection in the attachment (default 10 write-ops/s,
  burst 30, 32 KB/s). Violations get `err rate_limited {retryMs}`; sustained abuse gets close `4008`.
  A **per-room daily rows-written cap** (`ROOM_DAILY_WRITE_ROWS`, default 25,000) stops one room from
  draining the account-wide budget.

## 9. onDisconnect

- Ops are validated at registration and stored in `ondisconnect` keyed by connection id (1 row each).
  The connection id is also a socket **tag**.
- `webSocketClose`/`webSocketError` run that connection's ops (atomically, one rev) and delete them.
- `webSocketClose` isn't guaranteed (abrupt DO reset). So on every new connection and every close,
  the DO also **sweeps orphans**: rows whose `conn` is no longer among `ctx.getWebSockets()` tags get
  executed. Presence therefore can't get stuck "online".

## 10. Snapshot endpoint, quota, stats

- `GET /db/<room>/snapshot?path=p` (read key). The Worker checks `caches.default`, keyed on the URL
  without the key. On a hit it returns `304` if `If-None-Match` matches the ETag. On a miss it
  forwards to the DO (with `If-None-Match`), which returns JSON or `304`. Responses carry
  `ETag: "<subtree rev>"` and `Cache-Control: public, max-age=SNAPSHOT_MAX_AGE` (default 10 s).
  - A cache hit saves a DO request and rows read, **but still costs a Worker request**.
  - Whether the Cache API stores anything on `*.workers.dev` is not documented. **Verified on a real
    deploy (2026-11-04): it works** (`cf-cache-status: HIT`). A custom domain is the fallback if that changes.
- **Quota detection:** an exception from the DO stub (DO quota) → Worker returns `503 {"code":"quota"}`.
  `SQLITE`/quota errors from `sql.exec` → `err write_quota` and the room stays read-only until 00:00 UTC.
  Worker 1027 / HTTP 429 at upgrade → the client treats it as quota.
- **Usage counters** per room per UTC day: writes, rows written (from `cursor.rowsWritten`), connects,
  messages in, quota errors. They are kept in memory and flushed alongside the rev-block row and at
  most every 100 writes, so they are **approximate by design** and don't double the write cost.
  Seven days are kept.
- `GET /db/<room>/stats` (write key): today + history, `databaseSize`, live connection count, rev.

## 11. C# client

- `FreeSyncClient(url, room, key, FreeSyncOptions?)` → `Ref(path)` → `Child`, `Parent`, `SetAsync`,
  `UpdateAsync`, `RemoveAsync`, `OnValue(cb)` (returns `IDisposable`), `OnDisconnect().Set/Update/Remove/Cancel`,
  `GetAsync()` (via snapshot endpoint or live cache), `Push()`. `DataSnapshot` has `Value`, `GetValue<T>()`,
  `Child()`, `Children`, `Exists`, `Rev`, `IsFromCache`.
- **Serializer:** `IJsonSerializer`. The built-in one parses/writes JSON to
  `Dictionary<string,object>/List<object>/string/long/double/bool/null` and maps simple POCOs by
  reflection. `adapters/NewtonsoftJsonSerializer.cs` and `adapters/SystemTextJsonSerializer.cs` are
  drop-in source files, not compiled into the assembly.
- **Connection:** `ClientWebSocket` with protocol keep-alive. Reconnect uses exponential backoff with
  full jitter (1 s → 5 min). Each reconnect resubscribes with `since=rev` and flushes queued writes.
- **Quota:** on `4029`/`503 quota`/429, the state goes to `QuotaExceeded`, the last-known data is
  kept, and the client sleeps until 00:00 UTC + random 0–15 min. An optional sparse probe (default
  every 30 min) checks earlier. Writes stay queued (bounded) and are reported via the state, not
  thrown.
- **State events:** `Connecting, Connected, Reconnecting, Offline, QuotaExceeded`, plus `LastError`.
- **Threading:** `EventDispatch = Immediate | SynchronizationContext | Queued`. With `Queued`, a mod
  calls `db.Dispatch()` from `Update()`, and all callbacks run there.
- **Cache:** latest value + rev per subscribed path. Optional `CacheDirectory` persists it to disk
  for cold starts. Cached values are delivered first with `IsFromCache = true`.

## 12. Repo layout

```
/server          src/{index.ts, room.ts, store.ts, paths.ts, rules.ts, protocol.ts, config.ts}
                 test/*.test.ts, wrangler.jsonc, vitest.config.ts, package.json
/client          FreeSync/FreeSync.csproj  (single assembly, no dependencies)
                 FreeSync.Tests/ (xUnit)   adapters/{Newtonsoft,SystemTextJson}Serializer.cs
/samples/console two-instance demo (`dotnet run -- --name alice`)
/samples/unity   FreeSyncExample.cs MonoBehaviour + README (not compiled here)
/docs            LIMITS.md, DESIGN.md, PROTOCOL.md, SECURITY.md, DEPLOY.md
README.md
```

## 13. Milestones and verification

| # | Scope | Verified by |
|---|---|---|
| 1 | Limits research + this design | Your review |
| 2 | Worker router, RoomDO, chunked store, sub/snap/patch/set/upd/rm, revisions, hibernation via attachments, PROTOCOL.md | Vitest + pool-workers: path/diff units, store tests asserting `rowsWritten`, WebSocket integration, "hibernation" by handling messages on a fresh DO instance using only attachments + SQLite |
| 3 | C# client MVP, built-in serializer, reconnect/resync, Dispatch, console sample | xUnit (paths, JSON, cache, reconnect with a mock socket) + two console clients syncing via `wrangler dev` |
| 4 | Keys/roles, `$cid`, rules, validation, rate limits, per-room cap, onDisconnect + orphan sweep, SECURITY.md | Vitest per rule/limit; console demo of presence on kill |
| 5 | Snapshot + Cache API + ETag/304, quota detection and client degradation, usage stats, adapters | Vitest + xUnit with simulated quota errors; a real `*.workers.dev` deploy to check the Cache API (needs your account) |
| 6 | Unity sample, README, DEPLOY.md (`npm i && npx wrangler deploy`), polish | Docs walkthrough; the Unity sample is compile-checked against stubs only |

Things I can't verify here: real quota exhaustion (it's simulated, so we don't burn your quota), real
hibernation timing (local dev hibernates, but the timing differs), and Unity itself.

Tooling found locally: Node 22.23, .NET SDK 10.0.302, wrangler 4.157.0. Compatibility date will be
≥ 2026-04-07, so the runtime auto-replies to Close frames.

## 14. Decisions (approved 2026-11-04)

All five open questions below were approved with the recommended answers: multi-target
`netstandard2.0;net10.0`, ephemeral paths in M4, token-derived `cid`, per-room stats only, and both
⚠ deviations accepted.

Implementation note: `@cloudflare/vitest-pool-workers` has been renamed to `@cloudflare/vitest-plugin`
(same API, adds `evictDurableObject()` which we use to test real hibernation round-trips).

### Original open questions

1. **.NET target.** Most shipped Unity games run Mono (BepInEx/MelonLoader included), which cannot
   load a `net10.0` assembly. Proposal: multi-target **`netstandard2.0;net10.0`** from one project.
   It is still one dependency-free DLL per target, and the code is the same.
2. **Ephemeral paths.** For high-frequency data such as player positions, paths marked `ephemeral` in
   rules would be broadcast to subscribers but never written to SQLite (0 rows). Late joiners only
   see values sent while the object stayed awake. Include this in M4?
3. **Client identity.** Is the token-derived `cid` (section 8) acceptable, or do you want
   server-assigned per-connection ids, or ids supplied by your own game server via the write key?
4. **Stats scope.** Per-room stats only, or also an account-wide view? Account-wide needs a registry
   DO, which costs an extra DO request + row per room per day.
5. **Accepting the two deviations** marked ⚠: server-side coalescing per frame (not per time window),
   and `hello` folded into the upgrade request.
