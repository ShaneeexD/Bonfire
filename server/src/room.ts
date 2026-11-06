// One Durable Object per room. Holds one JSON document and the room's WebSocket connections.
//
// Uses the WebSocket Hibernation API: every piece of per-connection state lives in the socket
// attachment or in SQLite, never only in memory. There are no timers, alarms or outgoing sockets,
// so the object can hibernate as soon as a handler returns.

import { DurableObject } from "cloudflare:workers";
import { loadConfig, type Config, type FreeSyncEnv } from "./config";
import { isPrefix, joinPath, parsePath, PathError, utf8Length, MAX_PATH_DEPTH, type Path } from "./paths";
import { Close, Err, PING, PONG, PROTOCOL_VERSION, type Attachment, type Role } from "./protocol";
import { Rules, type Who } from "./rules";
import { Stats } from "./stats";
import { Store, StoreError } from "./store";
import { getIn, isObject, normalize, setIn, ValueError, type Json, type JsonObject } from "./value";

/** An error that becomes an `err` message for one operation. */
class OpError extends Error {
  constructor(
    readonly code: string,
    message: string,
  ) {
    super(message);
  }
}

interface Change {
  path: Path;
  value: Json;
  /** Broadcast only, never stored. */
  eph: boolean;
}

interface PreparedOp {
  id: unknown;
  changes: Change[];
}

/** onDisconnect ops are stored already validated, so they run without the client's context. */
interface StoredOdOp {
  p: string;
  c: { p: string; v: Json; e: boolean }[];
}

type Msg = JsonObject & { t: string; id?: unknown };

const MAX_ATTACHMENT_BYTES = 12_000; // the platform limit is 16,384; keep headroom

function toOpError(e: unknown): OpError {
  if (e instanceof OpError) return e;
  if (e instanceof PathError) return new OpError(Err.BadPath, e.message);
  if (e instanceof ValueError) return new OpError(Err.BadValue, e.message);
  if (e instanceof StoreError) return new OpError(e.code, e.message);
  return new OpError(Err.Internal, "internal error");
}

function errMsg(id: unknown, e: OpError, extra?: JsonObject): JsonObject {
  const m: JsonObject = { t: "err", code: e.code, msg: e.message, ...extra };
  if (id !== undefined) m.id = id as Json;
  return m;
}

function deepMerge(base: Json, over: Json): Json {
  if (!isObject(base) || !isObject(over)) return over ?? base;
  const out: JsonObject = { ...base };
  for (const [k, v] of Object.entries(over)) out[k] = deepMerge(base[k] ?? null, v);
  return out;
}

function who(att: Attachment): Who {
  return { cid: att.cid, uid: att.uid, role: att.role };
}

function nextUtcMidnight(now = Date.now()): number {
  const d = new Date(now);
  return Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() + 1);
}

/** The part of each change that a connection with these subscriptions should see. */
export function opsFor(subs: Path[], changes: readonly { path: Path; value: Json }[]): [string, Json][] {
  const out: [string, Json][] = [];
  for (const c of changes) {
    if (subs.some((s) => isPrefix(s, c.path))) {
      out.push([joinPath(c.path), c.value]);
      continue;
    }
    // Subscriptions below the write get only their own slice; skip ones covered by another.
    const below = subs.filter((s) => isPrefix(c.path, s));
    for (const s of below) {
      if (below.some((o) => o !== s && o.length < s.length && isPrefix(o, s))) continue;
      out.push([joinPath(s), getIn(c.value, s, c.path.length)]);
    }
  }
  return out;
}

export class RoomDO extends DurableObject<FreeSyncEnv> {
  private readonly cfg: Config;
  private readonly store: Store;
  private readonly stats: Stats;
  private rulesCache: { room: string; rules: Rules } | null = null;
  /** Values written under ephemeral paths. In memory only, by design. */
  private eph: Json = null;
  /** Set when SQLite reports a quota error; writes are refused until the next UTC midnight. */
  private quotaUntil = 0;

  constructor(ctx: DurableObjectState, env: FreeSyncEnv) {
    super(ctx, env);
    this.cfg = loadConfig(env);
    this.store = new Store(ctx.storage.sql, this.cfg.chunkDepth, this.cfg.maxChunkBytes);
    this.stats = new Stats(this.store);
    ctx.storage.sql.exec(
      `CREATE TABLE IF NOT EXISTS ondisconnect (conn TEXT NOT NULL, seq INTEGER NOT NULL, op TEXT NOT NULL, PRIMARY KEY (conn, seq)) WITHOUT ROWID`,
    );
    // App-level pings are answered by the runtime without waking us.
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair(PING, PONG));
  }

  private rules(room: string): Rules {
    if (this.rulesCache?.room !== room) this.rulesCache = { room, rules: new Rules(this.cfg.rules, room) };
    return this.rulesCache.rules;
  }

  // ---- HTTP entry points (called by the Worker, which has already checked the key) ---------

  async fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);
    const role = (req.headers.get("x-fs-role") ?? "public") as Role;
    const room = req.headers.get("x-fs-room") ?? "";
    switch (url.pathname) {
      case "/connect":
        return this.acceptClient(room, role, req.headers.get("x-fs-cid") ?? "", req.headers.get("x-fs-uid") ?? undefined);
      case "/snapshot":
        return this.snapshot(room, role, url.searchParams.get("path") ?? "", req.headers.get("if-none-match"));
      case "/stats":
        return this.statsResponse(room);
      default:
        return new Response("not found", { status: 404 });
    }
  }

  private acceptClient(room: string, role: Role, cid: string, uid: string | undefined): Response {
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    const conn = crypto.randomUUID();
    this.ctx.acceptWebSocket(server, [conn]);
    const att: Attachment = {
      conn,
      cid,
      ...(uid ? { uid } : {}),
      role,
      room,
      subs: [],
      rl: { o: this.cfg.rateBurst, b: this.byteBurst(), at: Date.now(), s: 0 },
      od: 0,
    };
    server.serializeAttachment(att);
    this.stats.bump("connects");
    server.send(
      JSON.stringify({
        t: "welcome",
        v: PROTOCOL_VERSION,
        cid,
        role,
        verified: uid !== undefined,
        rev: this.store.currentRev(),
        lim: {
          frame: this.cfg.maxFrameBytes,
          ops: this.cfg.maxOpsPerFrame,
          subs: this.cfg.maxSubs,
          rate: this.cfg.rateOpsPerSec,
          burst: this.cfg.rateBurst,
        },
      }),
    );
    this.sweepOrphans();
    return new Response(null, { status: 101, webSocket: client });
  }

  private snapshot(room: string, role: Role, rawPath: string, ifNoneMatch: string | null): Response {
    let path: Path;
    try {
      path = parsePath(rawPath);
    } catch (e) {
      return Response.json({ code: Err.BadPath, msg: (e as Error).message }, { status: 400 });
    }
    const denied = this.rules(room).checkRead(path, { cid: "", role });
    if (denied) return Response.json({ code: Err.PermissionDenied, msg: denied }, { status: 403 });
    const { value, rev } = this.store.read(path);
    const etag = `"${Math.max(rev, this.store.removeRev())}"`;
    const headers: Record<string, string> = {
      ETag: etag,
      "Cache-Control": `public, max-age=${this.cfg.snapshotMaxAge}`,
      "X-FreeSync-Rev": String(this.store.currentRev()),
      "Content-Type": "application/json",
    };
    if (ifNoneMatch === etag) return new Response(null, { status: 304, headers });
    const body = JSON.stringify(value);
    if (body.length > this.cfg.maxSnapshotBytes) {
      return Response.json({ code: Err.SnapshotTooLarge, msg: "value too large; read a deeper path" }, { status: 413 });
    }
    return new Response(body, { headers });
  }

  private statsResponse(room: string): Response {
    return Response.json({
      room,
      rev: this.store.currentRev(),
      chunkDepth: this.store.depth,
      databaseBytes: this.ctx.storage.sql.databaseSize,
      connections: this.ctx.getWebSockets().length,
      writeQuotaUntil: this.quotaUntil > Date.now() ? new Date(this.quotaUntil).toISOString() : null,
      // Limits the counters are measured against, so dashboards don't have to hard-code them.
      limits: { roomDailyWriteRows: this.cfg.roomDailyWriteRows, maxRoomBytes: this.cfg.maxRoomBytes },
      resetsAt: new Date(nextUtcMidnight()).toISOString(),
      today: this.stats.today(),
      days: this.stats.all(),
    });
  }

  // ---- WebSocket handlers -------------------------------------------------------------------

  async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    const att = ws.deserializeAttachment() as Attachment | null;
    if (!att) {
      ws.close(Close.Protocol, "unknown connection");
      return;
    }
    this.stats.bump("frames");
    if (typeof message !== "string") {
      this.send(ws, errMsg(undefined, new OpError(Err.BadRequest, "binary frames are not supported")));
      return;
    }
    const bytes = message.length * 3 > this.cfg.maxFrameBytes ? utf8Length(message) : message.length;
    if (bytes > this.cfg.maxFrameBytes) {
      this.stats.bump("denied");
      this.send(ws, errMsg(undefined, new OpError(Err.FrameTooLarge, `frame is ${bytes} bytes; limit ${this.cfg.maxFrameBytes}`)));
      return;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(message);
    } catch {
      this.send(ws, errMsg(undefined, new OpError(Err.BadRequest, "frame is not valid JSON")));
      return;
    }
    const msgs = Array.isArray(parsed) ? parsed : [parsed];
    if (msgs.length > this.cfg.maxOpsPerFrame) {
      this.send(ws, errMsg(undefined, new OpError(Err.BadRequest, `more than ${this.cfg.maxOpsPerFrame} messages in one frame`)));
      return;
    }

    // The write key is fully trusted (it can already change everything), and restoring a backup needs many
    // large frames, so admin connections skip the rate limit. The per-room daily write budget still applies.
    const retryMs = att.role === "admin" ? 0 : this.takeTokens(att, msgs.length, bytes);
    if (retryMs > 0) {
      this.stats.bump("denied", msgs.length);
      ws.serializeAttachment(att);
      if (att.rl.s > 30) {
        ws.close(Close.RateLimit, "rate limit exceeded repeatedly");
        return;
      }
      const e = new OpError(Err.RateLimited, "too many messages; slow down");
      this.send(ws, msgs.map((m) => errMsg(isObject(m) ? m.id : undefined, e, { retryMs })));
      return;
    }

    const out: JsonObject[] = [];
    let writes: Msg[] = [];
    for (const m of msgs) {
      if (!isObject(m) || typeof m.t !== "string") {
        out.push(errMsg(undefined, new OpError(Err.BadRequest, "message must be an object with a string 't'")));
        continue;
      }
      const msg = m as Msg;
      if (msg.t === "set" || msg.t === "upd" || msg.t === "rm") {
        writes.push(msg);
        continue;
      }
      // Writes before a read must be visible to it, so flush them first.
      if (writes.length) this.handleWrites(ws, att, writes, out);
      writes = [];
      this.handleOther(ws, att, msg, out);
    }
    if (writes.length) this.handleWrites(ws, att, writes, out);
    ws.serializeAttachment(att);
    if (out.length) this.send(ws, out.length === 1 ? out[0] : out);
  }

  async webSocketClose(ws: WebSocket): Promise<void> {
    this.onSocketGone(ws);
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    this.onSocketGone(ws);
  }

  private onSocketGone(ws: WebSocket): void {
    this.sweepOrphans(ws);
    // When the room empties it will soon sleep and lose in-memory counters, so store them (1 row).
    const othersOpen = this.ctx.getWebSockets().some((o) => o !== ws && o.readyState === WebSocket.OPEN);
    if (!othersOpen && this.stats.isDirty()) {
      try {
        this.ctx.storage.transactionSync(() => this.stats.flush());
      } catch (e) {
        console.warn("could not store stats", String(e));
      }
    }
  }

  // ---- message handling -----------------------------------------------------------------------

  private handleOther(ws: WebSocket, att: Attachment, m: Msg, out: JsonObject[]): void {
    try {
      switch (m.t) {
        case "sub":
          out.push(this.subscribe(att, m));
          return;
        case "unsub": {
          const key = joinPath(parsePath(m.p));
          att.subs = att.subs.filter((s) => s !== key);
          if (m.id !== undefined) out.push({ t: "ack", id: m.id as Json, rev: this.store.currentRev() });
          return;
        }
        case "od":
          out.push(this.onDisconnect(att, m));
          return;
        case "ping":
          out.push(m.id !== undefined ? { t: "pong", id: m.id as Json } : { t: "pong" });
          return;
        default:
          throw new OpError(Err.BadRequest, `unknown message type "${m.t}"`);
      }
    } catch (e) {
      const err = toOpError(e);
      if (err.code === Err.PermissionDenied || err.code === Err.TooManySubs) this.stats.bump("denied");
      out.push(errMsg(m.id, err));
    }
  }

  private subscribe(att: Attachment, m: Msg): JsonObject {
    const path = parsePath(m.p);
    const denied = this.rules(att.room).checkRead(path, who(att));
    if (denied) throw new OpError(Err.PermissionDenied, denied);
    const key = joinPath(path);
    if (!att.subs.includes(key)) {
      if (att.subs.length >= this.cfg.maxSubs) throw new OpError(Err.TooManySubs, `at most ${this.cfg.maxSubs} subscriptions per connection`);
      att.subs.push(key);
      if (utf8Length(JSON.stringify(att)) > MAX_ATTACHMENT_BYTES) {
        att.subs.pop();
        throw new OpError(Err.TooManySubs, "subscription paths use too much connection state");
      }
    }
    const stored = this.store.read(path);
    const ov = getIn(this.eph, path);
    const rev = this.store.currentRev();
    const reply: JsonObject = { t: "snap", p: key, rev };
    if (m.id !== undefined) reply.id = m.id as Json;
    const since = typeof m.since === "number" ? m.since : -1;
    if (since >= 0 && ov === null && since >= Math.max(stored.rev, this.store.removeRev())) {
      reply.nm = true;
      return reply;
    }
    const value = ov === null ? stored.value : deepMerge(stored.value, ov);
    if (JSON.stringify(value).length > this.cfg.maxSnapshotBytes) {
      att.subs = att.subs.filter((s) => s !== key);
      throw new OpError(Err.SnapshotTooLarge, "value too large to subscribe to; subscribe to a deeper path");
    }
    reply.v = value;
    return reply;
  }

  /** Validates a write message and turns it into normalised changes. */
  private prepare(att: Attachment, m: Msg): PreparedOp {
    const base = parsePath(m.p);
    let raw: [Path, unknown][];
    if (m.t === "set") raw = [[base, m.v]];
    else if (m.t === "rm") raw = [[base, null]];
    else {
      if (!isObject(m.v)) throw new OpError(Err.BadValue, "upd needs an object of relative paths to values");
      raw = Object.entries(m.v).map(([k, v]) => {
        const rel = parsePath(k);
        if (rel.length === 0) throw new OpError(Err.BadPath, "upd keys must be non-empty paths");
        if (base.length + rel.length > MAX_PATH_DEPTH) throw new PathError(`path deeper than ${MAX_PATH_DEPTH} levels`);
        return [[...base, ...rel], v] as [Path, unknown];
      });
      for (let i = 0; i < raw.length; i++)
        for (let j = 0; j < raw.length; j++)
          if (i !== j && isPrefix(raw[i][0], raw[j][0]))
            throw new OpError(Err.BadPath, `upd paths "${joinPath(raw[i][0])}" and "${joinPath(raw[j][0])}" overlap`);
    }
    const rules = this.rules(att.room);
    const changes = raw.map(([path, v]): Change => {
      const value = normalize(v);
      const eff = rules.effective(path, who(att));
      const denied = rules.checkWrite(path, who(att), eff);
      if (denied) throw new OpError(Err.PermissionDenied, denied);
      if (eff.maxBytes !== undefined && att.role !== "admin") {
        const size = utf8Length(JSON.stringify(value));
        if (size > eff.maxBytes) throw new OpError(Err.ValueTooLarge, `value is ${size} bytes; limit here is ${eff.maxBytes}`);
      }
      return { path, value, eph: eff.ephemeral };
    });
    return { id: m.id, changes };
  }

  private handleWrites(ws: WebSocket, att: Attachment, msgs: Msg[], out: JsonObject[]): void {
    const prepared: PreparedOp[] = [];
    for (const m of msgs) {
      try {
        prepared.push(this.prepare(att, m));
      } catch (e) {
        this.stats.bump("denied");
        out.push(errMsg(m.id, toOpError(e)));
      }
    }
    const { rev, ok, errors } = this.commit(prepared);
    for (const [op, e] of errors) out.push(errMsg(op.id, e));
    this.broadcast(ok.flatMap((op) => op.changes), rev, { ws, att });
    for (const op of ok) if (op.id !== undefined) out.push({ t: "ack", id: op.id as Json, rev });
  }

  /**
   * Stores the operations' non-ephemeral changes in one transaction and applies their ephemeral
   * changes in memory. Each operation succeeds or fails as a unit. `inTxn` runs inside the same
   * transaction (used to delete executed onDisconnect rows).
   */
  private commit(ops: PreparedOp[], inTxn?: () => number): { rev: number; ok: PreparedOp[]; errors: [PreparedOp, OpError][] } {
    const errors: [PreparedOp, OpError][] = [];
    let ok: PreparedOp[] = [];
    const batch = this.store.beginBatch();
    const today = this.stats.today();
    const quotaHit = this.quotaUntil > Date.now();
    for (const op of ops) {
      const stored = op.changes.filter((c) => !c.eph);
      if (stored.length) {
        let e: OpError | null = null;
        if (quotaHit) e = new OpError(Err.WriteQuota, "the account's daily storage write quota is used up; resets at 00:00 UTC");
        else if (today.rows >= this.cfg.roomDailyWriteRows) e = new OpError(Err.RoomQuota, "this room's daily write budget is used up; resets at 00:00 UTC");
        else if (stored.some((c) => c.value !== null) && this.ctx.storage.sql.databaseSize > this.cfg.maxRoomBytes)
          e = new OpError(Err.RoomFull, "room storage is full; remove data first");
        if (e) {
          this.stats.bump("quota");
          errors.push([op, e]);
          continue;
        }
        try {
          batch.applyAll(stored);
        } catch (err) {
          errors.push([op, toOpError(err)]);
          continue;
        }
      }
      ok.push(op);
    }

    let rev = this.store.currentRev();
    let counted = false;
    const extra = inTxn !== undefined;
    if (!batch.isEmpty || extra) {
      try {
        rev = this.ctx.storage.transactionSync(() => {
          const r = batch.isEmpty ? this.store.currentRev() : this.store.nextRev();
          let rows = batch.isEmpty ? 0 : batch.commit(r).rowsWritten + (this.store.reservedBlock ? 1 : 0);
          if (inTxn) rows += inTxn();
          this.stats.bump("rows", rows);
          // Count before a possible flush, so the stored counters include this batch.
          this.stats.bump("writes", ok.length);
          counted = true;
          if (this.stats.wantsFlush() || this.store.reservedBlock) this.stats.flush();
          return r;
        });
      } catch (err) {
        this.store.resetMemory();
        this.stats.reset();
        counted = false;
        const msg = String(err);
        let e: OpError;
        if (/SQLITE_FULL|disk is full/i.test(msg)) e = new OpError(Err.RoomFull, "room storage is full");
        else if (/exceed|quota|limit/i.test(msg)) {
          this.quotaUntil = nextUtcMidnight();
          e = new OpError(Err.WriteQuota, "the account's daily storage write quota is used up; resets at 00:00 UTC");
        } else {
          console.error("write failed", msg);
          e = new OpError(Err.Internal, "write failed");
        }
        this.stats.bump("quota", ok.length);
        // Ephemeral-only ops don't depend on storage and still succeed.
        const failed = ok.filter((op) => op.changes.some((c) => !c.eph));
        for (const op of failed) errors.push([op, e]);
        ok = ok.filter((op) => !failed.includes(op));
      }
    }

    for (const op of ok) {
      for (const c of op.changes) {
        if (c.eph) this.eph = setIn(this.eph, c.path, c.value);
        else if (getIn(this.eph, c.path) !== null) this.eph = setIn(this.eph, c.path, null);
      }
    }
    if (!counted) this.stats.bump("writes", ok.length);
    return { rev, ok, errors };
  }

  private onDisconnect(att: Attachment, m: Msg): JsonObject {
    const op = m.op;
    const path = parsePath(m.p);
    const key = joinPath(path);
    const sql = this.ctx.storage.sql;
    if (op === "cancel") {
      // Cancels ops registered at or below the path.
      const rows = sql.exec<{ seq: number; op: string }>(`SELECT seq, op FROM ondisconnect WHERE conn = ?`, att.conn).toArray();
      for (const r of rows) {
        const stored = JSON.parse(r.op) as StoredOdOp;
        if (isPrefix(path, parsePath(stored.p))) sql.exec(`DELETE FROM ondisconnect WHERE conn = ? AND seq = ?`, att.conn, r.seq);
      }
      att.od = sql.exec<{ n: number }>(`SELECT COUNT(*) AS n FROM ondisconnect WHERE conn = ?`, att.conn).one().n;
    } else {
      if (op !== "set" && op !== "upd" && op !== "rm") throw new OpError(Err.BadRequest, `unknown onDisconnect op "${String(op)}"`);
      if (att.od >= this.cfg.maxOnDisconnect) throw new OpError(Err.TooManyOnDisconnect, `at most ${this.cfg.maxOnDisconnect} onDisconnect ops per connection`);
      const prepared = this.prepare(att, { t: op, p: m.p, v: m.v ?? null } as Msg);
      const stored: StoredOdOp = { p: key, c: prepared.changes.map((c) => ({ p: joinPath(c.path), v: c.value, e: c.eph })) };
      const json = JSON.stringify(stored);
      if (json.length > this.cfg.maxFrameBytes) throw new OpError(Err.ValueTooLarge, "onDisconnect value too large");
      sql.exec(
        `INSERT INTO ondisconnect (conn, seq, op) VALUES (?, (SELECT COALESCE(MAX(seq), 0) + 1 FROM ondisconnect WHERE conn = ?), ?)`,
        att.conn,
        att.conn,
        json,
      );
      att.od++;
    }
    const reply: JsonObject = { t: "ack", rev: this.store.currentRev() };
    if (m.id !== undefined) reply.id = m.id as Json;
    return reply;
  }

  /**
   * Runs the onDisconnect ops of every connection that is no longer open. Called on close and on
   * connect, so ops still run if a close event was missed (for example after an object restart).
   */
  private sweepOrphans(closing?: WebSocket): void {
    const sql = this.ctx.storage.sql;
    const owners = sql.exec<{ conn: string }>(`SELECT DISTINCT conn FROM ondisconnect`).toArray().map((r) => r.conn);
    if (owners.length === 0) return;
    const live = new Set<string>();
    for (const ws of this.ctx.getWebSockets()) {
      if (ws === closing || ws.readyState !== WebSocket.OPEN) continue;
      for (const tag of this.ctx.getTags(ws)) live.add(tag);
    }
    const gone = owners.filter((c) => !live.has(c));
    if (gone.length === 0) return;

    const ops: PreparedOp[] = [];
    for (const conn of gone) {
      for (const r of sql.exec<{ op: string }>(`SELECT op FROM ondisconnect WHERE conn = ? ORDER BY seq`, conn)) {
        const stored = JSON.parse(r.op) as StoredOdOp;
        ops.push({ id: undefined, changes: stored.c.map((c) => ({ path: parsePath(c.p), value: c.v, eph: c.e })) });
      }
    }
    const deleteRows = () => {
      let rows = 0;
      for (const conn of gone) {
        const c = sql.exec(`DELETE FROM ondisconnect WHERE conn = ?`, conn);
        c.toArray();
        rows += c.rowsWritten;
      }
      return rows;
    };
    const { rev, ok, errors } = this.commit(ops, deleteRows);
    for (const [, e] of errors) console.warn("onDisconnect op failed", e.code);
    this.broadcast(ok.flatMap((op) => op.changes), rev, closing ? { ws: closing, skip: true } : undefined);
  }

  // ---- output -------------------------------------------------------------------------------

  /** Sends one `patch` per connection with the changes it subscribed to. */
  private broadcast(changes: Change[], rev: number, self?: { ws: WebSocket; att?: Attachment; skip?: boolean }): void {
    if (changes.length === 0) return;
    for (const ws of this.ctx.getWebSockets()) {
      if (ws.readyState !== WebSocket.OPEN) continue;
      let att: Attachment | null;
      if (self && ws === self.ws) {
        if (self.skip) continue;
        att = self.att ?? null;
      } else att = ws.deserializeAttachment() as Attachment | null;
      if (!att || att.subs.length === 0) continue;
      const ops = opsFor(att.subs.map((s) => (s === "" ? [] : s.split("/"))), changes);
      if (ops.length) this.send(ws, { t: "patch", rev, ops });
    }
  }

  private send(ws: WebSocket, msg: Json): void {
    try {
      ws.send(JSON.stringify(msg));
    } catch {
      // The socket closed under us; its close handler will clean up.
    }
  }

  private byteBurst(): number {
    return Math.max(this.cfg.maxFrameBytes, this.cfg.rateBytesPerSec * 2);
  }

  /** Token bucket in the attachment. Returns 0 when allowed, else a suggested retry delay in ms. */
  private takeTokens(att: Attachment, ops: number, bytes: number): number {
    const c = this.cfg;
    if (c.rateOpsPerSec <= 0) return 0;
    const now = Date.now();
    const dt = Math.max(0, (now - att.rl.at) / 1000);
    att.rl.o = Math.min(c.rateBurst, att.rl.o + dt * c.rateOpsPerSec);
    att.rl.b = Math.min(this.byteBurst(), att.rl.b + dt * c.rateBytesPerSec);
    att.rl.at = now;
    if (att.rl.o >= ops && att.rl.b >= bytes) {
      att.rl.o -= ops;
      att.rl.b -= bytes;
      // Strikes decay slowly, so a client spamming well above the limit is closed even if some frames get through.
      att.rl.s = Math.max(0, att.rl.s - 1);
      return 0;
    }
    att.rl.s++;
    const wait = Math.max((ops - att.rl.o) / c.rateOpsPerSec, c.rateBytesPerSec > 0 ? (bytes - att.rl.b) / c.rateBytesPerSec : 0);
    return Math.max(1, Math.ceil(wait * 1000));
  }
}
