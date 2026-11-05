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

The **Log** tab records what was sent: number of paths and requests, bytes, the resulting revision, and
how long the server took to confirm it.
