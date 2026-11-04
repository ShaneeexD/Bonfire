# Cloudflare Free-plan limits that shape FreeSync

Researched 2026-11-04 from the Cloudflare docs (page "last updated" dates Sep–Oct 2026).
Limits change; re-check the linked pages before relying on a number.

Sources:

- [DO limits]: https://developers.cloudflare.com/durable-objects/platform/limits/
- [DO pricing]: https://developers.cloudflare.com/durable-objects/platform/pricing/
- [DO WebSockets]: https://developers.cloudflare.com/durable-objects/best-practices/websockets/
- [DO state API]: https://developers.cloudflare.com/durable-objects/api/state/
- [DO base API]: https://developers.cloudflare.com/durable-objects/api/base/
- [DO SQLite API]: https://developers.cloudflare.com/durable-objects/api/sqlite-storage-api/
- [DO alarms]: https://developers.cloudflare.com/durable-objects/api/alarms/
- [Workers limits]: https://developers.cloudflare.com/workers/platform/limits/
- [Workers pricing]: https://developers.cloudflare.com/workers/platform/pricing/
- [Cache API]: https://developers.cloudflare.com/workers/runtime-apis/cache/
- [Workers Cache]: https://developers.cloudflare.com/workers/cache/

## 1. The daily budgets (Free plan, reset 00:00 UTC)

| Budget | Free limit | What consumes it | Source |
|---|---|---|---|
| **Worker requests** | 100,000 / day | Every HTTP request that reaches the Worker: WebSocket upgrades, snapshot GETs (including Cache API hits, since the Worker still runs). WebSocket *messages* routed through a Worker do **not** count. | [Workers limits], [Workers pricing] |
| **DO requests** | 100,000 / day | HTTP requests/RPC sessions into a DO, the WebSocket upgrade, **incoming** WebSocket messages at a **20:1 ratio** (100 messages = 5 requests), alarm invocations. **Outgoing** messages and protocol-level pings are **free**. | [DO pricing] footnotes 1–2 |
| **DO duration** | 13,000 GB-s / day | Wall-clock time while an object is active, billed at 128 MB per object. Objects that are idle and *eligible* for hibernation are not billed, even before they actually hibernate. | [DO pricing] footnotes 4–5 |
| **SQLite rows written** | 100,000 / day (account-wide) | Every row inserted/updated/**deleted**; every index row touched is an **extra** row; each `setAlarm()` is one row; KV-style `storage.put()` is also rows. | [DO pricing], [DO SQLite API] |
| **SQLite rows read** | 5,000,000 / day | Rows scanned by queries (incl. KV-style `get()`/`list()`). | [DO pricing] |

The pages meter Worker requests and DO requests in separate tables. Whether one Worker request that
forwards to a DO also counts against the DO pool is not stated explicitly; we assume **both count**.

When a Free limit is exceeded: "further operations of that type will fail with an error" until 00:00 UTC
([DO pricing]). For the Worker request limit Cloudflare returns **Error 1027** (fail-closed, or the
route can be set to fail-open) ([Workers limits]). The exact error surface for the DO request,
duration and rows quotas is **not documented**; we have to detect it empirically (thrown exceptions
from the DO stub / `sql.exec`).

### What this means in practice

- **Listening is cheap.** A connected client costs 1 Worker request + 1 DO request for the upgrade.
  After that, every update the server pushes to it is free.
- **Incoming messages are fairly cheap** (20 messages = 1 DO request, ~2M messages/day in theory).
- **Persisted writes are the scarce resource.** At 100k rows/day *for the whole account*, a write that
  touches 2 rows halves your budget. Storage layout and avoiding extra rows (indexes, revision
  counters, usage counters) matter more than message counts.
- **Reconnect storms are expensive** in the Worker/DO request pools: each attempt is 2 requests and
  has no value if the quota is already gone. Backoff must be aggressive.
- **Duration** is comfortable only if the object hibernates between events: 13,000 GB-s ≈ 28 hours of
  one 128 MB object being awake per day, across all rooms.

## 2. WebSocket Hibernation

| Fact | Value | Source |
|---|---|---|
| API | `ctx.acceptWebSocket(ws, tags?)`, handlers `webSocketMessage`, `webSocketClose`, `webSocketError` on the DO class. `ws.accept()` must not be called. | [DO state API], [DO base API] |
| When it hibernates | After a "short period" with no events (duration unstated). In-memory state is reset; the **constructor runs again** on the next event. | [DO WebSockets] |
| Things that block hibernation | `setTimeout`/`setInterval`, alarms and incoming requests while running, **outgoing** WebSockets (up to 15 min), pending `waitUntil` (up to 15 min). | [DO WebSockets], [DO state API] |
| Per-socket attachment | `serializeAttachment(value)` — structured-clone value, **max 16,384 bytes** serialized. Lost when either side closes the socket. | [DO WebSockets] |
| Tags | Max **10 tags** per socket, **256 chars** each. `getWebSockets(tag)` / `getTags(ws)`. | [DO state API] |
| Sockets per object | Max **32,768** hibernatable sockets (CPU/memory may limit sooner). | [DO state API] |
| Auto-response | `setWebSocketAutoResponse(new WebSocketRequestResponsePair(req, res))`, ≤ 2,048 chars each; replies without waking the object and without duration charges. Whether it still counts as a (1/20) request is not stated. | [DO state API], [DO pricing] |
| Ping frames | Protocol pings get an automatic pong, do not interrupt hibernation, are not billed, and never reach `webSocketMessage`. | [DO WebSockets], [DO pricing] |
| Close handshake | With compatibility date ≥ 2026-04-07 (`web_socket_auto_reply_to_close`) the runtime replies to Close automatically. | [DO base API] |
| `webSocketClose` on abrupt loss | Not documented as guaranteed. Design must tolerate missed close events. | [DO base API] |
| Code deploys | Hibernated sockets stay connected across deploys unless the update strategy is `immediate` or a deferred update's `max_delay` is hit. | [DO WebSockets] |
| CPU per event | 30 s default per incoming request/message (budget resets per message). | [DO limits] |
| Incoming message size | **32 MiB** max received message. Outgoing limit not stated. | [DO limits] |
| Throughput | Soft limit ~1,000 requests/s per object. | [DO limits] |

## 3. SQLite storage

| Limit | Value | Source |
|---|---|---|
| Backend on Free | **SQLite-backed DOs only** (KV-backed not available). | [DO limits], [DO pricing] |
| Total storage (account) | **5 GB** (decimal) | [DO limits] |
| Per object | Table says 10 GB; the same page's FAQ says **1 GB on Free**. We assume 1 GB. | [DO limits] |
| Row / string / BLOB size | **2 MB** max (key+value combined ≤ 2 MB for KV API) | [DO limits] |
| SQL statement length | 100 KB | [DO limits] |
| Bound parameters per query | 100 | [DO limits] |
| Columns per table | 100 | [DO limits] |
| Disk full | Writes fail with `SQLITE_FULL`; reads and DELETEs still work. | [DO limits] |
| API | `ctx.storage.sql.exec()` is **synchronous**; each event's storage ops are implicitly one transaction; `transactionSync()` for explicit atomic blocks; `ctx.storage.sql.databaseSize` gives bytes used; cursors expose `rowsRead`/`rowsWritten`. `BEGIN`/`SAVEPOINT` are not allowed. | [DO SQLite API] |

## 4. Alarms

- One alarm per object; `setAlarm()` overrides the previous one ([DO alarms]).
- An alarm invocation is a **DO request**, and each `setAlarm()` is a **row written** ([DO pricing]).
- Conclusion: alarms are too expensive to use as a write-coalescing timer on Free.

## 5. Worker (front door) limits

| Limit | Free value | Source |
|---|---|---|
| CPU per request | **10 ms** (cannot be raised on Free) | [Workers limits] |
| Memory | 128 MB per isolate | [Workers limits] |
| Subrequests per invocation | 50 (Cache API calls share this quota) | [Workers limits] |
| Env vars + secrets | 64 per Worker, 5 KB each | [Workers limits] |
| Worker size | 64 MiB uncompressed | [Workers limits] |
| Workers / DO classes per account | 100 / 100 | [Workers limits], [DO limits] |

## 6. Caching for the snapshot endpoint

- **Cache API** (`caches.default`): per-data-centre only, not replicated; max object 512 MB; up to 50
  calls per request on Free. The docs confirm it works on **custom domains**; they neither confirm nor
  deny that it stores anything on `*.workers.dev`. **Verified empirically on 2026-11-04: it works there**
  (`cf-cache-status: HIT`, cached value served until max-age). ([Cache API])
- A Cache API hit still runs the Worker, so it **saves DO requests and rows read, not Worker requests**.
- **Workers Cache** (`"cache": { "enabled": true }` in wrangler config) can answer without running the
  Worker and is documented to work on workers.dev, but hits are still billed as Worker requests and its
  Free-plan status and ETag handling are not stated ([Workers Cache]). We will not depend on it.

## 7. Design consequences (summary)

1. Store the document as **chunked subtrees** (one row per subtree at a fixed depth) in a `WITHOUT ROWID`
   table, so a typical small write costs **one row**, with no secondary indexes.
2. Do **not** write a revision/usage row on every write; reserve revision numbers in blocks and
   piggyback counters (see DESIGN.md).
3. No timers, alarms or outgoing sockets in the DO; everything happens inside message handlers so
   the object can hibernate immediately.
4. Per-connection state lives in the 16 KB attachment; anything larger (onDisconnect ops) lives in SQLite.
5. Validate auth in the Worker **before** forwarding to the DO, so rejected clients cost no DO request.
6. Use WebSocket protocol pings (free) for keep-alive, not app-level ping messages.
7. Clients batch writes into one frame, back off hard, and wait for 00:00 UTC on quota errors.
