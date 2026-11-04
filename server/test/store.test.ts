import { env, exports } from "cloudflare:workers";
import { evictDurableObject, runInDurableObject } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { REV_BLOCK, Store } from "../src/store";
import type { Json } from "../src/value";

let n = 0;
function stub() {
  return env.ROOM.get(env.ROOM.idFromName(`store-${n++}-${Math.random()}`));
}

/** Runs `fn` with a fresh Store inside a real SQLite-backed Durable Object. */
function withStore<R>(fn: (store: Store, sql: SqlStorage) => R, s = stub(), depth = 2) {
  return runInDurableObject(s, (_inst, state) => fn(new Store(state.storage.sql, depth, 1000), state.storage.sql));
}

function write(store: Store, path: string, value: Json) {
  const b = store.beginBatch();
  b.apply(path === "" ? [] : path.split("/"), value);
  const rev = store.nextRev();
  return { rev, ...b.commit(rev) };
}

function rows(sql: SqlStorage) {
  return sql.exec<{ key: string; val: string }>(`SELECT key, val FROM nodes ORDER BY key`).toArray().map((r) => [r.key, JSON.parse(r.val)]);
}

describe("chunked store", () => {
  it("cuts the tree into one row per depth-2 subtree", async () => {
    await withStore((store, sql) => {
      write(store, "", { players: { a: { hp: 1, pos: { x: 1 } }, b: { hp: 2 } }, motd: "hi" });
      expect(rows(sql)).toEqual([
        ["motd", "hi"],
        ["players/a", { hp: 1, pos: { x: 1 } }],
        ["players/b", { hp: 2 }],
      ]);
      expect(store.read([]).value).toEqual({ players: { a: { hp: 1, pos: { x: 1 } }, b: { hp: 2 } }, motd: "hi" });
      expect(store.read(["players", "a", "pos", "x"]).value).toBe(1);
      expect(store.read(["players"]).value).toEqual({ a: { hp: 1, pos: { x: 1 } }, b: { hp: 2 } });
      expect(store.read(["nothing", "here"]).value).toBeNull();
    });
  });

  it("a small write below the chunk depth costs exactly one row", async () => {
    await withStore((store) => {
      write(store, "players/a", { hp: 1 });
      const r = write(store, "players/a/pos/x", 5);
      expect(r.rowsWritten).toBe(1);
      expect(store.read(["players", "a"]).value).toEqual({ hp: 1, pos: { x: 5 } });
      // Re-writing the same value costs nothing.
      expect(write(store, "players/a/pos/x", 5).rowsWritten).toBe(0);
    });
  });

  it("several writes to one chunk in a batch write it once", async () => {
    await withStore((store) => {
      write(store, "players/a", { hp: 1 });
      const b = store.beginBatch();
      for (let i = 0; i < 10; i++) b.apply(["players", "a", `k${i}`], i);
      const res = b.commit(store.nextRev());
      expect(res.rowsWritten).toBe(1);
    });
  });

  it("replacing a shallow path deletes stale rows and records removeRev", async () => {
    await withStore((store, sql) => {
      write(store, "players", { a: { hp: 1 }, b: { hp: 2 } });
      const r = write(store, "players", { b: { hp: 3 } });
      expect(r.removed).toBe(true);
      expect(rows(sql)).toEqual([["players/b", { hp: 3 }]]);
      expect(store.removeRev()).toBe(r.rev);
      write(store, "players", null);
      expect(rows(sql)).toEqual([]);
    });
  });

  it("scalars above the chunk depth are replaced by objects and vice versa", async () => {
    await withStore((store, sql) => {
      write(store, "settings", 5);
      expect(rows(sql)).toEqual([["settings", 5]]);
      write(store, "settings/volume/level", 3);
      expect(rows(sql)).toEqual([["settings/volume", { level: 3 }]]);
      write(store, "settings", "off");
      expect(rows(sql)).toEqual([["settings", "off"]]);
      write(store, "", 42);
      expect(rows(sql)).toEqual([["", 42]]);
      expect(store.read([]).value).toBe(42);
      write(store, "a/b", 1);
      expect(rows(sql)).toEqual([["a/b", 1]]);
    });
  });

  it("rejects chunks over the size limit without staging anything", async () => {
    await withStore((store, sql) => {
      const b = store.beginBatch();
      b.apply(["a", "ok"], 1);
      expect(() => b.applyAll([{ path: ["a", "big", "x"], value: "y".repeat(2000) }])).toThrow(/limit per chunk/);
      b.commit(store.nextRev());
      expect(rows(sql)).toEqual([["a/ok", 1]]);
    });
  });

  it("subtree revisions track the newest row", async () => {
    await withStore((store) => {
      const r1 = write(store, "x/a", 1).rev;
      const r2 = write(store, "y/a", 1).rev;
      expect(r2).toBeGreaterThan(r1);
      expect(store.read(["x"]).rev).toBe(r1);
      expect(store.read([]).rev).toBe(r2);
    });
  });

  it("revisions stay monotonic across eviction, reserving one block row per wake", async () => {
    const s = stub();
    const first = await withStore((store, sql) => {
      const a = write(store, "k/a", 1).rev;
      const b = write(store, "k/a", 2).rev;
      expect(b).toBe(a + 1);
      return { last: b, hi: JSON.parse(sql.exec<{ v: string }>(`SELECT v FROM meta WHERE k='revHi'`).one().v) };
    }, s);
    expect(first.hi).toBe(1 + REV_BLOCK);
    await evictDurableObject(s);
    await withStore((store) => {
      expect(store.currentRev()).toBeGreaterThanOrEqual(first.last);
      const next = write(store, "k/a", 3).rev;
      expect(next).toBeGreaterThan(first.last);
      expect(store.read(["k", "a"]).value).toBe(3);
    }, s);
  });

  it("keeps the chunk depth a room was created with", async () => {
    const s = stub();
    await withStore((store) => write(store, "a/b/c", 1), s, 2);
    await withStore((store, sql) => {
      expect(store.depth).toBe(2);
      write(store, "a/b/d", 2);
      expect(rows(sql)).toEqual([["a/b", { c: 1, d: 2 }]]);
    }, s, 3);
  });
});

// Keep the default export referenced so the main worker is loaded for these tests.
void exports;
