# Bonfire wire protocol, version 1

The protocol uses JSON text frames over one WebSocket per client per room. Keys are short to keep
frames small. The server is the reference implementation:
[`server/src/room.ts`](../server/src/room.ts).

## Connecting

```
GET wss://<host>/db/<room>?v=1
Authorization: Bearer <read key | write key>     (or ?key=<key>)
X-Bonfire-Token: <client secret, 16-256 chars>  (optional, or ?token=<...>)
X-Bonfire-Identity: <signed identity token>     (optional, or ?identity=<...>)
Upgrade: websocket
```

- `room` matches `ROOM_PATTERN` (default `^[A-Za-z0-9_-]{1,64}$`).
- `v` is the protocol version. A version the server doesn't speak returns HTTP **426**
  `{"code":"unsupported_version"}`.
- The key decides the role: write key → `admin`, read key → `public`, anything else → HTTP **401**.
  All of these checks run in the Worker before any Durable Object is touched.
- **Client id (`cid`)**: with a token, `cid` is the first 16 base32 characters of
  `SHA-256(room + "\n" + token)`. It is stable across reconnects and can't be claimed without the
  token. Without a token you get a random `anon-…` id per connection. Rules can refer to it as `$cid`.
- **Verified id (`$uid`)**: with a valid identity token (an HS256 JWT signed with `IDENTITY_SECRET`; see
  [SECURITY.md](SECURITY.md#verified-player-identities-uid)), `cid` is the token's `sub` and the
  connection is **verified**.
  - An invalid token gets HTTP **401** `{"code":"bad_identity","msg":"<reason>"}`.
  - A server without `IDENTITY_SECRET` answers **400** `identity_not_configured`.

Authentication happens in the upgrade request, so there is no `hello` message. Clients that cannot
set headers (browsers) use the query-string forms.

HTTP errors from the Worker are JSON `{"code": "...", "msg": "..."}`. Status **503** with
`code: "quota"` means the room could not be reached, most likely because the daily Durable Object
quota is used up. Cloudflare's own **429 / error 1027** means the daily Worker request quota is used up.

## Frames

- Each frame holds **one message object or a JSON array of messages**, in both directions. Batching
  writes into one frame is the main way to save quota: 20 incoming frames are billed as 1 request,
  however many messages they carry.
- Limits (sent in `welcome.lim`): frame size `frame` bytes (default 64 KB), at most `ops` messages
  per frame (default 100), at most `subs` subscriptions per connection (default 64).
- Every message has a type `t`. Request messages may carry a client-chosen `id` (number or string),
  which the reply echoes. Write messages without an `id` get no `ack`; errors are still reported.

## Paths and values

- A path is a `/`-separated list of keys. Leading and trailing slashes are ignored, and `""` is the
  root. Keys are non-empty and may not contain `/ . # $ [ ]` or control characters. The maximum depth
  is 32 and the maximum length is 512 bytes.
- Values are JSON. `null` means "absent". Writing `null` deletes. Objects with no remaining children
  disappear. Empty objects and arrays count as `null`. Numbers must be finite. The maximum value
  nesting depth is 32.
- **Arrays are opaque values.** Paths can't address array elements (`a/0` reads as `null`). Writing
  below an array replaces it with an object.

## Client → server

| Message | Fields | Effect |
|---|---|---|
| `sub` | `p`, `id?`, `since?` | Subscribe to `p`. Replies with a `snap`. If `since` is a rev the client already has and nothing at or below `p` changed after it, the `snap` carries `nm:true` and no value. |
| `unsub` | `p`, `id?` | Stop receiving changes for `p`. Replies with an `ack` if `id` is given. |
| `set` | `p`, `v`, `id?` | Replace the value at `p`. |
| `upd` | `p`, `v` (object), `id?` | Multi-path merge. Each key of `v` is a path relative to `p` and may contain `/`; each value replaces that path (`null` deletes). Keys may not overlap (`a` and `a/b`). |
| `rm` | `p`, `id?` | Delete `p` (same as `set` with `null`). |
| `od` | `op` (`set`/`upd`/`rm`/`cancel`), `p`, `v?`, `id?` | Register a write to run when this connection closes, or cancel the ones registered at or below `p`. Checked against the rules at registration time. |
| `ping` | `id?` | Liveness check. The exact frame `{"t":"ping"}` is answered by the runtime itself, without waking the room. Prefer WebSocket protocol pings, which are free. |

Writes are applied in order. All writes in one frame that succeed share **one revision**, and each
stored row is written once however many writes hit it. Each write message succeeds or fails as a
unit (an `upd` is all-or-nothing).

## Server → client

| Message | Fields | Meaning |
|---|---|---|
| `welcome` | `v`, `cid`, `role`, `verified`, `rev`, `lim` | First message on every connection. `verified` is true when an identity token was accepted. |
| `snap` | `p`, `v` or `nm:true`, `rev`, `id?` | Full value at `p` (reply to `sub`). |
| `patch` | `rev`, `ops: [[path, value], ...]` | Changes affecting your subscriptions. Each op replaces the value at its path (`null` = deleted). Writes above a subscription are trimmed to the subscribed slice, so you only receive data you subscribed to. At most one `patch` per connection per incoming frame. Writers receive their own changes too. |
| `ack` | `id`, `rev` | The write (or `unsub`/`od`) with this id was applied. For writes, `rev` equals the rev of the `patch` that carried it, which is sent first. |
| `err` | `code`, `msg`, `id?`, `retryMs?` | The message with this id failed. Without `id`, the whole frame failed. |
| `pong` | `id?` | Reply to `ping`. |

### Revisions

`rev` is a per-room number that never decreases. It is **not contiguous**: the server reserves
revisions in blocks of 1,000,000 so it doesn't spend a stored row on every write, and a block can be
abandoned when the room hibernates. Use it to:

- order and de-duplicate updates (never apply a `patch` older than your snapshot),
- resubscribe after a reconnect with `since` = the newest rev you hold,
- validate the snapshot endpoint's `ETag`.

Messages on one socket are always in order, so a client can't miss a patch while connected. Gaps
can only happen across reconnects, and `sub` with `since` covers them.

### Error codes

| Code | Meaning |
|---|---|
| `bad_request` | Malformed message, unknown type, binary frame, or too many messages in a frame. |
| `bad_path` / `bad_value` | Invalid path or value (see above). |
| `permission_denied` | The rules don't allow this role/client to read or write here. |
| `rate_limited` | The connection exceeded its token bucket. `retryMs` suggests a wait. Repeated violations close the socket with `4008`. Write-key (admin) connections are not rate-limited. |
| `too_many_subs` / `too_many_on_disconnect` | Per-connection limits. |
| `frame_too_large` / `value_too_large` / `chunk_too_large` / `snapshot_too_large` | Size limits. A chunk is one stored subtree; see DESIGN.md. |
| `room_full` | Room storage limit reached. Deletes still work. |
| `room_quota` | This room's daily row-write budget is used up (until 00:00 UTC). |
| `write_quota` | The account's daily storage-write quota is used up (until 00:00 UTC). Reads still work. |
| `internal` | Unexpected server error. |

### Close codes

| Code | Meaning |
|---|---|
| `4001` | Authentication problem. |
| `4008` | Closed for repeated rate-limit violations. Back off before reconnecting. |
| `4029` | Quota exhausted. Reconnect after 00:00 UTC. |
| `4400` | Protocol error. |

## Snapshot endpoint

```
GET https://<host>/db/<room>/snapshot?path=<path>
Authorization: Bearer <key>          (or &key=<key>)
If-None-Match: "<etag>"              (optional)
```

- Returns the JSON value at the path (`null` if absent), with headers `ETag: "<rev>"`,
  `X-Bonfire-Rev`, and `Cache-Control: public, max-age=SNAPSHOT_MAX_AGE`.
- Returns **304** when `If-None-Match` matches.
- Public (read-key) responses are cached in the Cloudflare data centre for `SNAPSHOT_MAX_AGE`
  seconds, so repeated startup reads skip the Durable Object.
- Values under ephemeral paths are not included.

## Identity endpoint

`POST https://<host>/identity` with the **write key** and a JSON body:

```json
{"player": "steam-76561198000000001", "ttlSeconds": 86400, "room": "lobby-*"}
```

- **Fields:** `ttlSeconds` and `room` are optional.
- **Reply:** `{"token": "...", "player": "...", "expires": "<ISO time>"}`.
- **Lifetime:** defaults to one day, capped at `IDENTITY_MAX_DAYS`.
- **Errors:** 403 without the write key, 400 for an unusable player id, and 400
  `identity_not_configured` without `IDENTITY_SECRET`.
- **Cost:** it runs in the Worker only (no Durable Object request).

## Stats endpoint

`GET https://<host>/db/<room>/stats` with the **write key**. Returns per-UTC-day counters (approximate
by design), database size, live connection count, and the current rev.

## Example session

```
← {"t":"welcome","v":1,"cid":"k3f9q2…","role":"public","rev":1000041,"lim":{…}}
→ {"t":"sub","id":1,"p":"scores"}
← {"t":"snap","id":1,"p":"scores","v":{"p1":40},"rev":1000041}
→ [{"t":"set","id":2,"p":"scores/p1","v":42},{"t":"od","id":3,"op":"rm","p":"presence/k3f9q2…"}]
← {"t":"patch","rev":1000042,"ops":[["scores/p1",42]]}
← [{"t":"ack","id":2,"rev":1000042},{"t":"ack","id":3,"rev":1000042}]
```
