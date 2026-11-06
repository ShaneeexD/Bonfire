# Security model

FreeSync is built for hobby projects and game mods. It aims for "not trivially vandalised", not
bank-grade security. Read this page before you ship a mod with a key in it.

## Keys and roles

| Secret | Role | Where it lives | Can do |
|---|---|---|---|
| `READ_KEY` | `public` | Shipped inside your mod. **Assume everyone has it.** | Read everything that rules allow; write only where rules say `"public"`. |
| `WRITE_KEY` | `admin` | Only in your own admin tools or game servers. Never in a mod. | Everything: bypasses all rules and limits on paths, reads stats. |

- Set both with `npx wrangler secret put READ_KEY` / `WRITE_KEY`. Secrets are not shown in the
  dashboard or logs, and the server never logs them.
- Keys are compared in constant time in the Worker, **before** the request reaches a Durable Object.
  Requests with bad keys cost one Worker request and nothing else.
- With no `READ_KEY` set, anyone may connect as `public`. That is fine for local testing and a bad
  idea when deployed.
- To rotate a key, put a new secret and redeploy. Old clients get HTTP 401 and back off (they retry
  every few minutes).

## Client identity (`$cid`)

Each client may send a secret token (16–256 characters). The server computes
`cid = base32(SHA-256(room + "\n" + token))[0..16]`:

- the same token gives the same `cid` on every reconnect, so a player keeps their `players/$cid` data;
- nobody can claim your `cid` without your token, and the token is never stored or echoed by the server;
- a player who deletes their token file gets a new identity. There are no accounts or recovery.

Clients without a token get a random `anon-…` id per connection.

This doesn't stop someone from making many identities: anyone with the read key can generate tokens.
Rules limit *where* each identity can write, and rate limits and per-room budgets limit *how much*.

## Verified player identities (`$uid`)

A `$cid` says "same player as last time", not "who you are". If you have a backend that knows who a
player is, it can vouch for them with a **signed identity token**. Examples: a game server, an SPT
server mod, a website with logins, or a Discord bot.

- **The token:** a standard **JWT signed with HS256** using `IDENTITY_SECRET`, a secret only the Worker
  and your backend know. Claims:
  - `sub` (the player id, which must be a valid key: no `/ . # $ [ ]`);
  - `exp` (expiry, Unix seconds);
  - optionally `room` (a room name or glob such as `lobby-*`) to limit where it works.
- **The client** sends it in `X-FreeSync-Identity` (`FreeSyncOptions.IdentityToken`, or
  `IdentityTokenProvider` to fetch a fresh one before every reconnect).
- **The Worker checks it** before anything reaches the room. A bad signature, any algorithm other
  than HS256, an expired token, one valid for longer than `IDENTITY_MAX_DAYS` (30) or one for another
  room gets HTTP 401 `bad_identity`.
- **A valid token** makes the connection's `cid` the token's `sub`, marks it **verified**, and the
  `welcome` message says `"verified": true`.
- **Rules can then trust it:**
  - `$uid` matches **only** a verified player's id. An unverified client whose self-chosen `$cid`
    happens to be identical still doesn't match.
  - `"read"` or `"write": "verified"` lets any verified player in, and nobody else with the read key.

```jsonc
{ "path": "profiles/$uid",    "write": "verified", "maxBytes": 4096 },  // only the proven owner
{ "path": "lounge/*",         "write": "verified" },                    // no anonymous spam
{ "path": "leaderboard",      "write": "admin" }                        // only your backend (write key)
```

**Issuing tokens:**
- **C# backends:**

  ```csharp
  var token = FreeSyncIdentity.Create(secret, playerId, TimeSpan.FromHours(24), room: null);
  ```

- **Any JWT library:** for example Node's `jsonwebtoken`:

  ```js
  jwt.sign({ sub: playerId }, secret, { algorithm: "HS256", expiresIn: "24h" })
  ```

- **No crypto at all:** `POST /identity` with the **write key** and `{"player": "...", "ttlSeconds":
  86400}`.
- **For testing:** `npm run identity -- --player alice`.

**Rules for keeping it safe:**
- **The secret** must stay on your backend. Never put it in a client mod: anyone holding it can sign a
  token for any player.
- **Revocation:** tokens can't be revoked individually. Keep lifetimes short (hours or days) and use
  `IdentityTokenProvider` to refresh them. Changing `IDENTITY_SECRET` invalidates every token at once.
- **Expiry and open connections:** a token is checked when the connection opens. A connection that is
  already open stays verified after the token expires, until it reconnects.

## Rules

Rules come from the `RULES` var (a JSON object in `wrangler.jsonc`, or a JSON string):

```jsonc
{
  "default": { "read": "public", "write": "admin" },   // applies everywhere unless overridden
  "paths": [
    { "path": "players/$cid",     "write": "public", "maxBytes": 4096 },
    { "path": "players/$cid/pos", "ephemeral": true },
    { "path": "presence/$cid",    "write": "public", "maxBytes": 256 },
    { "path": "chat/*",           "write": "public", "maxBytes": 1024 },
    { "path": "secrets",          "read": "admin" }
  ],
  "rooms": [
    { "match": "sandbox-*", "default": { "write": "public" } }   // per-room overrides (glob on room name)
  ]
}
```

- Pattern segments are literal keys, `*` (any one key), `$cid` (the connecting client's id) or `$uid`
  (the player's **verified** id from an identity token; it never matches unverified clients).
- Access levels are `"public"` (read key), `"verified"` (read key plus a valid identity token) and
  `"admin"` (write key, which bypasses all rules).
- A rule covers its path **and everything below it**. For each setting (`read`, `write`, `maxBytes`,
  `ephemeral`), the most specific matching rule that sets it wins: deeper beats shallower, literal
  beats wildcard, later beats earlier.
- **Writing at a path replaces everything below it**, so a write must also be allowed by every rule
  beneath it. A public client can't `set chat = {}` to wipe a `chat/pinned` that only admins may write.
- Likewise a **read** (subscription or snapshot) is refused if any rule beneath it forbids reading.
  Subscribe to a narrower path instead.
- `maxBytes` limits the JSON size of a single public write at or below the path.
- `ephemeral: true` marks data that is broadcast but never stored. It costs no storage writes, is lost
  when the room sleeps, and is not included in HTTP snapshots. Use it for positions and other
  high-frequency, short-lived values.
- `onDisconnect` writes are checked against the rules **when they are registered**, using the
  registering client's role and `cid`.

### Known gaps in the example rules

- `chat/*` lets any public client overwrite or delete any chat message. For an append-only chat you
  need a relay you control (an admin tool with the write key) or a stricter design.
- `players/$cid` lets a player write any values into their own subtree, including made-up scores.
  `$uid` proves *who* wrote something, not that the value is honest.
  FreeSync can't validate game logic. If cheating matters, have your own server write scores with the
  write key and make that path admin-only.

## Validation and limits

All limits are configurable vars (see DEPLOY.md):

| Check | Default | Error |
|---|---|---|
| Path syntax: keys without `/ . # $ [ ]` or control chars, depth ≤ 32, ≤ 512 bytes | fixed | `bad_path` |
| Value: JSON only, finite numbers, nesting ≤ 32 | fixed | `bad_value` |
| Incoming frame size | 64 KB | `frame_too_large` |
| Messages per frame | 100 | `bad_request` |
| Stored chunk size (one subtree at the chunk depth) | 1 MB | `chunk_too_large` |
| Room storage | 50 MB | `room_full` (deletes still work) |
| Subscriptions / onDisconnect ops per connection | 64 / 16 | `too_many_subs` / `too_many_on_disconnect` |
| Rate limit per connection (token bucket; the write key is exempt, e.g. for restoring backups) | 20 msgs/s, burst 100, 32 KB/s | `rate_limited` + `retryMs`; repeated abuse closes the socket with 4008 |
| Stored rows written per room per UTC day | 25,000 | `room_quota` |

The per-room daily budget matters most. Free-plan storage writes (100,000 rows/day) are shared by
**every room on your account**, so this budget stops one busy or abused room from taking the whole
day's allowance.

## What this does not protect against

- **Reading:** anything readable by `public` is readable by anyone who extracts the read key from
  your mod. Never put secrets in a public-readable path.
- **Denial of service:** someone with the read key can open many connections and burn your daily
  *request* quota (each connection costs 1 Worker request + 1 DO request). Rate limits slow down a
  single connection, not many. If that happens, rotate the read key or put Cloudflare WAF rate-limiting
  rules on a custom domain.
- **Transport:** always use `wss://` / `https://` in production. Keys travel in headers, or in the
  query string for browsers, where they may show up in proxy logs.
- **Cheating:** see "Known gaps in the example rules" above.

## Reporting

This is a hobby project. If you find a way to bypass rules or keys, open an issue without the exploit
details and the maintainer will get in touch.
