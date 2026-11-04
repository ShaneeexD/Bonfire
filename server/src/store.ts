// Chunked document storage in the Durable Object's SQLite database.
//
// The tree is cut at a fixed depth D. Each node at depth D is one row holding its whole subtree as
// JSON. A scalar or array above depth D is a row at its own path. Objects above D are implicit.
// A typical small write (below depth D) therefore reads and writes exactly one row.
//
// Tables are WITHOUT ROWID with the path as primary key, so no secondary index rows are written
// (index rows count as extra "rows written" on the Free plan).

import { joinPath, utf8Length, type Path } from "./paths";
import { getIn, isObject, setIn, type Json } from "./value";

/** Revision numbers are reserved in blocks so we don't write a meta row on every write. */
export const REV_BLOCK = 1_000_000;

export class StoreError extends Error {
  constructor(
    readonly code: string,
    message: string,
  ) {
    super(message);
  }
}

interface Row {
  val: Json;
  rev: number;
}

export interface ReadResult {
  value: Json;
  /** Highest revision of any row that holds data at or below the path (0 if none). */
  rev: number;
}

export interface CommitResult {
  rowsWritten: number;
  /** True when at least one row was deleted (subtree versions can't detect deletions). */
  removed: boolean;
}

function splitKey(key: string): string[] {
  return key === "" ? [] : key.split("/");
}

/** Range bounds for keys strictly below `key` ("/" sorts directly before "0"). */
function childRange(key: string): [string, string] {
  return key === "" ? ["", "￿"] : [key + "/", key + "0"];
}

export class Store {
  /** Exact-key row cache (null = known absent). Lost on hibernation, which is fine. */
  private rows = new Map<string, Row | null>();
  private meta = new Map<string, Json>();
  private lastRev: number | null = null;
  private reservedUntil = 0;
  /** True when the last nextRev() call also wrote a new revision block (a meta row). */
  reservedBlock = false;

  /** Chunk depth. Fixed by the room's first write; later config changes don't affect old rooms. */
  readonly depth: number;

  constructor(
    private readonly sql: SqlStorage,
    configuredDepth: number,
    readonly maxChunkBytes: number,
  ) {
    sql.exec(`CREATE TABLE IF NOT EXISTS nodes (key TEXT PRIMARY KEY, val TEXT NOT NULL, rev INTEGER NOT NULL) WITHOUT ROWID`);
    sql.exec(`CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL) WITHOUT ROWID`);
    const stored = this.getMeta("depth");
    this.depth = typeof stored === "number" ? stored : configuredDepth;
  }

  // ---- meta ------------------------------------------------------------------------------

  getMeta(k: string): Json {
    if (this.meta.has(k)) return this.meta.get(k)!;
    const r = this.sql.exec<{ v: string }>(`SELECT v FROM meta WHERE k = ?`, k).toArray()[0];
    const v = r ? (JSON.parse(r.v) as Json) : null;
    this.meta.set(k, v);
    return v;
  }

  /** Writes a meta value. Returns the rows written (1). */
  setMeta(k: string, v: Json): number {
    const c = this.sql.exec(`INSERT INTO meta (k, v) VALUES (?, ?) ON CONFLICT(k) DO UPDATE SET v = excluded.v`, k, JSON.stringify(v));
    c.toArray();
    this.meta.set(k, v);
    return c.rowsWritten;
  }

  // ---- revisions -------------------------------------------------------------------------

  /** The newest revision that may exist. Never decreases, even across hibernation. */
  currentRev(): number {
    if (this.lastRev !== null) return this.lastRev;
    return ((this.getMeta("revHi") as number | null) ?? 1) - 1;
  }

  /** Revision of the last write that deleted rows. */
  removeRev(): number {
    return (this.getMeta("removeRev") as number | null) ?? 0;
  }

  /** Allocates the next revision. Must run inside the same transaction as the write it labels. */
  nextRev(): number {
    const r = this.lastRev === null ? ((this.getMeta("revHi") as number | null) ?? 1) : this.lastRev + 1;
    this.reservedBlock = r >= this.reservedUntil;
    if (this.reservedBlock) {
      this.reservedUntil = r + REV_BLOCK;
      this.setMeta("revHi", this.reservedUntil);
    }
    this.lastRev = r;
    return r;
  }

  /** Drops every in-memory cache. Call after a failed (rolled back) transaction. */
  resetMemory(): void {
    this.rows.clear();
    this.meta.clear();
    this.lastRev = null;
    this.reservedUntil = 0;
  }

  // ---- reads -----------------------------------------------------------------------------

  getRow(key: string): Row | null {
    const cached = this.rows.get(key);
    if (cached !== undefined) return cached;
    const r = this.sql.exec<{ val: string; rev: number }>(`SELECT val, rev FROM nodes WHERE key = ?`, key).toArray()[0];
    const row = r ? { val: JSON.parse(r.val) as Json, rev: r.rev } : null;
    this.rememberRow(key, row);
    return row;
  }

  private rememberRow(key: string, row: Row | null): void {
    // Keep the cache bounded; it is only an optimisation.
    if (this.rows.size >= 5000) this.rows.clear();
    this.rows.set(key, row);
  }

  /** Rows at or below `key`. */
  private rowsUnder(key: string): { key: string; val: string; rev: number }[] {
    const [lo, hi] = childRange(key);
    return this.sql
      .exec<{ key: string; val: string; rev: number }>(
        `SELECT key, val, rev FROM nodes WHERE key = ? OR (key >= ? AND key < ?)`,
        key,
        lo,
        hi,
      )
      .toArray();
  }

  private keysUnder(key: string): string[] {
    const [lo, hi] = childRange(key);
    return this.sql
      .exec<{ key: string }>(`SELECT key FROM nodes WHERE key = ? OR (key >= ? AND key < ?)`, key, lo, hi)
      .toArray()
      .map((r) => r.key);
  }

  read(path: Path): ReadResult {
    const D = this.depth;
    if (path.length >= D) {
      const row = this.getRow(joinPath(path.slice(0, D)));
      if (!row) return { value: null, rev: 0 };
      return { value: getIn(row.val, path, D), rev: row.rev };
    }
    const base = joinPath(path);
    let value: Json = null;
    let rev = 0;
    for (const r of this.rowsUnder(base)) {
      const val = JSON.parse(r.val) as Json;
      this.rememberRow(r.key, { val, rev: r.rev });
      const segs = splitKey(r.key);
      value = setIn(value, segs, val, path.length);
      if (r.rev > rev) rev = r.rev;
    }
    return { value, rev };
  }

  beginBatch(): Batch {
    return new Batch(this);
  }

  // ---- used by Batch -----------------------------------------------------------------------

  /** @internal */ _keysUnder(key: string): string[] {
    return this.keysUnder(key);
  }

  /** @internal */ _commit(staged: Map<string, Json>, rev: number): CommitResult {
    let rowsWritten = 0;
    let removed = false;
    if (this.getMeta("depth") === null) rowsWritten += this.setMeta("depth", this.depth);
    for (const [key, val] of staged) {
      if (val === null) {
        const c = this.sql.exec(`DELETE FROM nodes WHERE key = ?`, key);
        c.toArray();
        if (c.rowsWritten > 0) removed = true;
        rowsWritten += c.rowsWritten;
        this.rememberRow(key, null);
      } else {
        const old = this.rows.get(key);
        const json = JSON.stringify(val);
        if (old && JSON.stringify(old.val) === json) continue; // unchanged: costs nothing
        const c = this.sql.exec(
          `INSERT INTO nodes (key, val, rev) VALUES (?, ?, ?) ON CONFLICT(key) DO UPDATE SET val = excluded.val, rev = excluded.rev`,
          key,
          json,
          rev,
        );
        c.toArray();
        rowsWritten += c.rowsWritten;
        this.rememberRow(key, { val, rev });
      }
    }
    if (removed) rowsWritten += this.setMeta("removeRev", rev);
    return { rowsWritten, removed };
  }
}

/**
 * Stages a group of writes so that each touched row is written once, however many writes hit it.
 * Each apply() is all-or-nothing: if it throws, nothing it touched is staged.
 */
export class Batch {
  private staged = new Map<string, Json>();

  constructor(private readonly store: Store) {}

  get isEmpty(): boolean {
    return this.staged.size === 0;
  }

  private current(key: string): Json {
    if (this.staged.has(key)) return this.staged.get(key)!;
    return this.store.getRow(key)?.val ?? null;
  }

  private checkSize(key: string, val: Json): void {
    if (val === null) return;
    const bytes = utf8Length(JSON.stringify(val));
    if (bytes > this.store.maxChunkBytes) {
      throw new StoreError(
        "chunk_too_large",
        `subtree at "${key}" would be ${bytes} bytes; the limit per chunk is ${this.store.maxChunkBytes}`,
      );
    }
  }

  /** Applies several writes as one unit: if any of them throws, none of them is staged. */
  applyAll(changes: readonly { path: Path; value: Json }[]): void {
    const saved = new Map(this.staged);
    try {
      for (const c of changes) this.apply(c.path, c.value);
    } catch (e) {
      this.staged = saved;
      throw e;
    }
  }

  /** Writes `value` (already normalised; null deletes) at `path`. */
  apply(path: Path, value: Json): void {
    const D = this.store.depth;
    const next = new Map<string, Json>();
    const stage = (k: string, v: Json) => next.set(k, v);

    if (path.length >= D) {
      const key = joinPath(path.slice(0, D));
      const chunk = setIn(this.current(key), path, value, D);
      this.checkSize(key, chunk);
      stage(key, chunk);
    } else {
      // Replace everything at or below the path, then lay the new value out as rows.
      for (const k of this.store._keysUnder(joinPath(path))) stage(k, null);
      const prefix = joinPath(path);
      for (const [k, v] of this.staged) {
        if (v !== null && (k === prefix || prefix === "" || k.startsWith(prefix + "/"))) stage(k, null);
      }
      const explode = (p: string[], v: Json) => {
        if (v === null) return;
        if (p.length < D && isObject(v)) {
          for (const [k, c] of Object.entries(v)) explode([...p, k], c);
        } else {
          const k = joinPath(p);
          this.checkSize(k, v);
          stage(k, v);
        }
      };
      explode([...path], value);
    }

    // A scalar or array stored above the written path is replaced by the implicit parent object.
    const top = Math.min(path.length, D);
    for (let i = 0; i < top; i++) {
      const k = joinPath(path.slice(0, i));
      if (!next.has(k) && this.current(k) !== null) stage(k, null);
    }

    for (const [k, v] of next) this.staged.set(k, v);
  }

  commit(rev: number): CommitResult {
    return this.store._commit(this.staged, rev);
  }
}
