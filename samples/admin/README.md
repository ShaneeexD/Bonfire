# Admin editor (Windows)

A Windows Forms tool for looking at and editing a room's JSON document like a database: a live tree
you can edit by hand, plus SQL. Every change it makes shows up instantly in connected clients (try it next
to the [visual demo](../visual)).

```bash
dotnet run --project samples/admin
dotnet run --project samples/admin -- --room visual-demo --connect
dotnet run --project samples/admin -- --run --sql "SELECT * FROM players ORDER BY score DESC"
```

Options: `--url --room --key --path` set the top-bar fields; `--connect` connects at start; `--sql "…"` fills
the SQL box; `--run` connects and runs it once the data has loaded.

If this checkout has `server/.deploy-keys.env` and `server/.deployed-url` (both git-ignored, created when
you deployed), it defaults to your live Worker with the **write key** (admin: rules don't apply). Otherwise
it uses `wrangler dev` on `127.0.0.1:8787` with `dev-write-key`. With the read key you can browse, but
most writes are refused by the rules.

## Editing by hand

- **Tree:** the whole room, or only the part under **Path**. It updates live as other clients write.
  Changed nodes are orange with a `*`.
- **Select a node** to see its value on the right. Edit the JSON (or plain text, a number or true/false
  with **Type**) and press **Save** (Ctrl+S).
- **Add child…** adds a key under the selected node, and can generate a time-ordered key like `Push()`.
  **Rename / move…** (F2) moves a value to a new path. **Delete** (Del) removes a node and everything
  below it.

## SQL

Any object in the document is a table: its children are rows, the row key is the `key` column, and the
row's fields are columns (nested fields use dots: `pos.x`). `value` is the whole row value, which is
useful for tables of plain values like `settings`. Table names can be paths: `rooms/lobby/players`, or
quoted `"rooms/lobby/players"`.

```sql
SHOW TABLES                                     -- or SHOW TABLES IN rooms/lobby
SELECT * FROM players ORDER BY score DESC LIMIT 10
SELECT key, name, score * 2 AS double FROM players WHERE score > 5 AND name LIKE 'A%'
SELECT COUNT(*) FROM chat WHERE from IN ('Ann', 'Bob')
INSERT INTO players (key, name, score, pos.x) VALUES ('p1', 'Ann', 0, 10)
INSERT INTO chat (from, text) VALUES ('admin', 'Server restarting soon')   -- no key: a push id is generated
INSERT INTO settings (key, value) VALUES ('motd', 'Welcome!'), ('maxPlayers', 16)
UPDATE players SET score = COALESCE(score, 0) + 1, banned = NULL WHERE name = 'Ann'   -- NULL deletes a field
DELETE FROM chat WHERE text LIKE '%spam%'
```

- **Operators:** `= != <> < <= > >=`, `AND OR NOT`, `[NOT] LIKE` (`%` `_`), `IS [NOT] NULL`,
  `[NOT] IN (…)`, `+ - * / %`, and `||` to concatenate.
- **Functions:** `COALESCE`/`IFNULL`, `LOWER`, `UPPER`, `LENGTH`, `ABS`, `ROUND(x[, digits])`, and
  `NOW()` (Unix milliseconds).
- **NULL** behaves as in SQL: a missing field is NULL, and `NULL + 1` is NULL, so use
  `COALESCE(score, 0) + 1`.
- **Running:** separate statements with `;`. **Run** (F5 or Ctrl+Enter) runs the selected text, or
  everything if nothing is selected. Later statements see earlier ones' changes. The last SELECT's
  result appears in the grid.
- **Writes:** UPDATE writes only the fields that actually change. INSERT refuses an existing key; use
  UPDATE for that. If any statement fails, nothing from that run is staged.
- **Not supported:** there are no joins or GROUP BY. This is a convenience over a JSON tree, not a
  relational engine.

## Batch mode (on by default)

With **Batch mode** ticked, nothing is sent while you work:

- Hand edits and SQL changes are **staged**. The tree shows the result in orange, and the
  **Pending changes** tab lists every change; double-click one to find it in the tree, or remove it.
- **Submit** sends all of them as **one multi-path update**. The server applies it **atomically
  under one revision**, so other clients see the whole batch at once and it costs one message. Changes
  to the same row are merged first, so 50 edits to one player are still written once.
- If the batch is bigger than one WebSocket frame (64 KB), it is split into the fewest frame-sized
  updates. Each part is atomic, but not the batch as a whole; the Log tab says when this happened.
- **Discard** drops everything staged.

With batch mode **off**, every save, add, rename and delete is sent immediately, and so is each SQL
statement (as one request per statement).

## Usage meter

The bar at the bottom always shows **rows written today against the room's daily budget**. Click it, or
open the **Usage** tab, for the full picture:

| Meter | Against | Why it matters |
|---|---|---|
| Rows written today | the room's `ROOM_DAILY_WRITE_ROWS` (25,000) | When it's full, writes to this room fail with `room_quota` until 00:00 UTC |
| Share of the account's rows written | the free plan's 100,000/day | That allowance is shared by **every room** on your account |
| Room storage | `MAX_ROOM_BYTES` (50 MB) | When it's full, writes that add data fail with `room_full` |
| ≈ Durable Object requests | the free plan's 100,000/day | Estimated as connections + incoming messages ÷ 20 |

The tab also shows today's write operations, connections, messages, live connections and refused
operations, plus a 7-day history.

- **Source:** the numbers come from the room's `GET /db/<room>/stats` endpoint, so the meter needs the
  **write key**.
- **Cost:** it refreshes on connect, after each Submit, and every 60 seconds while the editor is the
  active window. Each refresh costs one Worker request and one Durable Object request.
- **Accuracy:** the counters are approximate. They can undercount when a room sleeps between writes.
  Exact account-wide totals are in the Cloudflare dashboard (Workers & Pages → your Worker → Metrics).
- Bars turn orange at 70% and red at 90%.

### Whole-account usage (optional)

With a Cloudflare API token, the Usage tab also shows **exact account-wide** numbers for today. They
come from Cloudflare's GraphQL Analytics API and cover all rooms together, measured against the free
plan's limits:

- rows written: 100k/day, the usual first limit to hit
- Durable Object requests: 100k/day, with WebSocket messages counted 20:1 as billed
- Worker requests: 100k/day
- rows read: 5M/day
- duration: 13,000 GB-s/day
- SQLite storage: 5 GB in total

The footer then shows the account's rows-written bar next to the room's.

**To set it up:**

1. Go to https://dash.cloudflare.com/profile/api-tokens and choose **Create Token** → **Create Custom
   Token**.
2. Give it one permission, **Account → Account Analytics → Read**, limited to your account.
3. Save the token as the only content of `server/.analytics-token`, and your account ID (the 32
   characters shown by `npx wrangler whoami`) in `server/.cloudflare-account`. Both files are
   git-ignored, and the editor reads them at start-up. You can also paste the values into the fields
   on the Usage tab for one session.

**Good to know:**

- The token only reads analytics, so it can't change your account. The editor sends it to
  `api.cloudflare.com` only; the Worker never sees it.
- Analytics run a few minutes behind. Cloudflare's billing is authoritative.
- Field names come from the API's own schema, because Cloudflare's docs don't list them:
  - `durableObjectsPeriodicGroups` → `sum { rowsWritten rowsRead duration }` (duration is in GB-s)
  - `durableObjectsInvocationsAdaptiveGroups` → `sum { requests }`, by `type` (`hibernation` = WebSocket messages)
  - `workersInvocationsAdaptive` → `sum { requests }`
  - `durableObjectsSqlStorageGroups` → `max { storedBytes }`

The **Log** tab records what was sent: number of paths and requests, bytes, the resulting revision, and
how long the server took to confirm it.
