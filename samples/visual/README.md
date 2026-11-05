# Visual demo (Windows)

A small Windows Forms app: every window is one FreeSync client, and every connected player is a coloured
dot on a shared board. Open it twice to watch the two windows update each other live.

**Download:** `FreeSync-VisualDemo-<version>-win-x64.zip` from the repository's Releases page. Run
`FreeSyncVisualDemo.exe` twice. In each window, enter your server URL, a room and the **read key**, give
the window its own name, and press **Connect**. Settings are remembered in
`%APPDATA%\FreeSync\visual-demo.json`, with the key encrypted.

**From source**, this opens two windows that connect straight away:

```powershell
powershell -ExecutionPolicy Bypass -File run-two.ps1
```

This opens **Ann** (you steer) and **Bob** (walks in a circle by himself). Or start copies by hand:

```bash
dotnet run -- --name Ann --autoconnect
dotnet run -- --name Bob --autoconnect --auto
```

## What to try

| Do this in one window | See this in the other |
|---|---|
| Click the board, or hold arrow keys / WASD | The dot glides to the same place (positions are an **ephemeral** path: broadcast, never stored) |
| Press **+1 score** | The scoreboard updates (stored at `players/<id>/score`) |
| Type in the chat box and press Enter | The message appears (stored under `chat/`, keyed with `Push()`) |
| Press **Disconnect** or close the window | The dot disappears and the player shows ○: the server runs the `OnDisconnect().Remove()` on `presence/<id>` |
| Reconnect, or restart with the same name | Same colour, id and score: the name's token is kept in `%APPDATA%\FreeSync\visual-demo-tokens` |

The bottom of the side panel shows the client id, how long the last write took to be confirmed by
the server, and how many writes are pending.

## Which server it uses

- If this checkout has `server/.deploy-keys.env` and `server/.deployed-url` (both git-ignored, created when
  you deployed), it connects to **your live Worker** with the read key.
- Otherwise it uses `wrangler dev` on `ws://127.0.0.1:8787` with `dev-read-key`
  (start it with `cd server && npm run dev`).
- Everything is editable in the top bar before you press Connect, or with `--url`, `--room`, `--key`, `--name`.

The writes it makes (`players/$cid`, `presence/$cid`, `chat/*`) are allowed for read-key clients by the
example `RULES` in `server/wrangler.jsonc`. Writing anywhere else is refused, and the refusal shows up in the chat list.
