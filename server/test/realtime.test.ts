import { evictDurableObject } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { connect, http, READ_KEY, roomName, roomStub, tick, WRITE_KEY } from "./helpers";

describe("connection", () => {
  it("welcomes with version, role, client id and revision", async () => {
    const { welcome, client } = await connect(roomName());
    expect(welcome).toMatchObject({ t: "welcome", v: 1, role: "admin", rev: 0 });
    expect(welcome.cid).toMatch(/^anon-/);
    expect(welcome.lim.frame).toBeGreaterThan(0);
    client.close();
  });

  it("derives a stable client id from the token", async () => {
    const room = roomName();
    const token = "my-secret-token-123456";
    const a = await connect(room, { token });
    const b = await connect(room, { token });
    const c = await connect(room, { token: "another-token-abcdefgh" });
    expect(a.welcome.cid).toBe(b.welcome.cid);
    expect(a.welcome.cid).toMatch(/^[a-z2-7]{16}$/);
    expect(c.welcome.cid).not.toBe(a.welcome.cid);
  });

  it("rejects bad keys, versions, rooms and non-upgrades in the Worker", async () => {
    const room = roomName();
    expect((await http(`/db/${room}?v=1`, { headers: { Upgrade: "websocket", Authorization: "Bearer nope" } })).status).toBe(401);
    expect((await http(`/db/${room}?v=1`, { headers: { Upgrade: "websocket" } })).status).toBe(401);
    expect((await http(`/db/${room}?v=2`, { headers: { Upgrade: "websocket", Authorization: `Bearer ${READ_KEY}` } })).status).toBe(426);
    expect((await http(`/db/${room}`, { headers: { Authorization: `Bearer ${READ_KEY}` } })).status).toBe(426);
    expect((await http(`/db/bad%20room?v=1`, { headers: { Upgrade: "websocket", Authorization: `Bearer ${READ_KEY}` } })).status).toBe(400);
    expect((await http(`/elsewhere`)).status).toBe(404);
  });

  it("answers the exact ping string via auto-response", async () => {
    const { client } = await connect(roomName());
    client.send('{"t":"ping"}');
    expect(await client.nextOf("pong")).toEqual({ t: "pong" });
  });
});

describe("subscriptions and patches", () => {
  it("delivers a snapshot, then minimal patches with revisions to every subscriber", async () => {
    const room = roomName();
    const { client: a } = await connect(room);
    const { client: b } = await connect(room);

    expect(await a.request({ t: "sub", id: 1, p: "scores" })).toMatchObject({ t: "snap", p: "scores", v: null });
    await b.request({ t: "sub", id: 1, p: "scores/p1" });

    const ack = await a.request({ t: "set", id: 2, p: "scores/p1", v: 42 });
    expect(ack).toMatchObject({ t: "ack", id: 2 });
    expect(ack.rev).toBeGreaterThan(0);

    const pa = await a.nextOf("patch");
    const pb = await b.nextOf("patch");
    expect(pa).toEqual({ t: "patch", rev: ack.rev, ops: [["scores/p1", 42]] });
    expect(pb).toEqual({ t: "patch", rev: ack.rev, ops: [["scores/p1", 42]] });
  });

  it("trims writes above a subscription and skips unrelated ones", async () => {
    const room = roomName();
    const { client: w } = await connect(room);
    const { client: s } = await connect(room);
    await s.request({ t: "sub", id: 1, p: "players/a" });

    await w.request({ t: "set", id: 1, p: "chat/1", v: "hello" }); // unrelated
    await w.request({ t: "set", id: 2, p: "players", v: { a: { hp: 3 }, b: { hp: 9 } } });
    const patch = await s.nextOf("patch");
    expect(patch.ops).toEqual([["players/a", { hp: 3 }]]);
    await tick();
    expect(s.pending()).toEqual([]);
  });

  it("applies upd as a multi-path merge and rm as delete", async () => {
    const room = roomName();
    const { client } = await connect(room);
    await client.request({ t: "set", id: 1, p: "players/a", v: { hp: 1, name: "Ann", inv: { sword: true } } });
    await client.request({ t: "upd", id: 2, p: "players/a", v: { hp: 5, "inv/shield": true, name: null } });
    expect((await client.request({ t: "sub", id: 3, p: "players/a" })).v).toEqual({ hp: 5, inv: { sword: true, shield: true } });
    await client.request({ t: "rm", id: 4, p: "players/a/inv" });
    const patch = await client.nextOf("patch", (m) => m.ops[0][0] === "players/a/inv");
    expect(patch.ops).toEqual([["players/a/inv", null]]);
    expect((await client.request({ t: "upd", id: 5, p: "x", v: { a: 1, "a/b": 2 } })).code).toBe("bad_path");
  });

  it("sends one patch per frame for a batch of writes", async () => {
    const room = roomName();
    const { client: w } = await connect(room);
    const { client: s } = await connect(room);
    await s.request({ t: "sub", id: 1, p: "" });
    w.send([
      { t: "set", id: 1, p: "a/x", v: 1 },
      { t: "set", id: 2, p: "a/y", v: 2 },
      { t: "set", id: 3, p: "b/z", v: 3 },
    ]);
    const patch = await s.nextOf("patch");
    expect(patch.ops).toEqual([
      ["a/x", 1],
      ["a/y", 2],
      ["b/z", 3],
    ]);
    const acks = [await w.nextOf("ack"), await w.nextOf("ack"), await w.nextOf("ack")];
    expect(new Set(acks.map((a) => a.rev)).size).toBe(1); // one revision per frame
    expect(acks[0].rev).toBe(patch.rev);
  });

  it("answers a resubscribe with notModified when nothing changed since the given rev", async () => {
    const room = roomName();
    const { client } = await connect(room);
    const ack = await client.request({ t: "set", id: 1, p: "cfg/a", v: 1 });
    const snap = await client.request({ t: "sub", id: 2, p: "cfg", since: ack.rev });
    expect(snap).toMatchObject({ t: "snap", nm: true });
    expect(snap.v).toBeUndefined();
    const older = await client.request({ t: "sub", id: 3, p: "cfg", since: ack.rev - 1 });
    expect(older.v).toEqual({ a: 1 });
    // A deletion elsewhere bumps removeRev, which conservatively forces a full resend.
    const rm = await client.request({ t: "rm", id: 4, p: "other" });
    await client.request({ t: "set", id: 5, p: "other/x", v: 1 });
    const del = await client.request({ t: "rm", id: 6, p: "other/x" });
    expect((await client.request({ t: "sub", id: 7, p: "cfg", since: rm.rev })).v).toEqual({ a: 1 });
    expect((await client.request({ t: "sub", id: 8, p: "cfg", since: del.rev })).nm).toBe(true);
  });

  it("reports validation errors per operation without failing the others", async () => {
    const { client } = await connect(roomName());
    client.send([
      { t: "set", id: 1, p: "a.b", v: 1 },
      { t: "set", id: 2, p: "ok/x", v: NaN },
      { t: "set", id: 3, p: "ok/y", v: 1 },
      { t: "bogus", id: 4 },
    ]);
    expect(await client.next((m) => m.id === 1)).toMatchObject({ t: "err", code: "bad_path" });
    expect(await client.next((m) => m.id === 2)).toMatchObject({ t: "ack" }); // NaN serialises as null = delete
    expect(await client.next((m) => m.id === 3)).toMatchObject({ t: "ack" });
    expect(await client.next((m) => m.id === 4)).toMatchObject({ t: "err", code: "bad_request" });
    client.send("not json");
    expect(await client.nextOf("err")).toMatchObject({ code: "bad_request" });
  });
});

describe("hibernation", () => {
  it("keeps subscriptions, identity and data across eviction (attachments restored)", async () => {
    const room = roomName();
    const { client: a, welcome } = await connect(room, { key: READ_KEY, token: "token-for-hibernation-test" });
    const { client: w } = await connect(room);
    await a.request({ t: "sub", id: 1, p: "state" });
    const before = await w.request({ t: "set", id: 1, p: "state/x", v: 1 });
    await a.nextOf("patch");

    await evictDurableObject(roomStub(room)); // in-memory state is gone; sockets hibernate

    const after = await w.request({ t: "set", id: 2, p: "state/y", v: 2 });
    expect(after.rev).toBeGreaterThan(before.rev);
    // A fresh instance reserved a new revision block, which proves the eviction really happened.
    expect(after.rev).toBeGreaterThanOrEqual(1_000_000);
    expect(await a.nextOf("patch")).toEqual({ t: "patch", rev: after.rev, ops: [["state/y", 2]] });

    // Role restored from the attachment: still public, so writes are refused.
    expect((await a.request({ t: "set", id: 2, p: "state/z", v: 1 })).code).toBe("permission_denied");
    const snap = await a.request({ t: "sub", id: 3, p: "state" });
    expect(snap.v).toEqual({ x: 1, y: 2 });
    expect(welcome.role).toBe("public");
  });
});

describe("snapshot endpoint", () => {
  it("returns JSON with a revision ETag and honours If-None-Match", async () => {
    const room = roomName();
    const { client } = await connect(room);
    await client.request({ t: "set", id: 1, p: "scores", v: { a: 1, b: 2 } });

    const res = await http(`/db/${room}/snapshot?path=scores&key=${READ_KEY}`);
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ a: 1, b: 2 });
    const etag = res.headers.get("ETag")!;
    expect(etag).toMatch(/^"\d+"$/);
    expect(res.headers.get("Cache-Control")).toMatch(/max-age=\d+/);

    const again = await http(`/db/${room}/snapshot?path=scores`, { headers: { Authorization: `Bearer ${READ_KEY}`, "If-None-Match": etag } });
    expect(again.status).toBe(304);

    expect((await http(`/db/${room}/snapshot?path=scores`)).status).toBe(401);
    expect((await http(`/db/${room}/snapshot?path=a.b&key=${READ_KEY}`)).status).toBe(400);
  });

  it("serves stats to the write key only", async () => {
    const room = roomName();
    const { client } = await connect(room);
    await client.request({ t: "set", id: 1, p: "a/b", v: 1 });
    expect((await http(`/db/${room}/stats?key=${READ_KEY}`)).status).toBe(403);
    const res = await http(`/db/${room}/stats`, { headers: { Authorization: `Bearer ${WRITE_KEY}` } });
    const stats: any = await res.json();
    expect(stats.today.writes).toBe(1);
    expect(stats.today.connects).toBe(1);
    expect(stats.today.rows).toBeGreaterThanOrEqual(1);
    expect(stats.connections).toBe(1);
    expect(stats.limits).toEqual({ roomDailyWriteRows: 25000, maxRoomBytes: 50000000 });
    expect(stats.resetsAt).toMatch(/T00:00:00\.000Z$/);
  });

  it("keeps most counters when the room sleeps with clients still connected", async () => {
    const room = roomName();
    const { client } = await connect(room);
    for (let i = 0; i < 25; i++) await client.request({ t: "set", id: i, p: "a/b", v: i });
    await evictDurableObject(roomStub(room)); // hibernate; the client stays connected
    const stats: any = await (await http(`/db/${room}/stats?key=${WRITE_KEY}`)).json();
    // Flushed with the first write (rev block) and after 20 more; the last few may be lost by design.
    expect(stats.today.writes).toBeGreaterThanOrEqual(20);
    expect(stats.today.writes).toBeLessThanOrEqual(25);
    expect(stats.connections).toBe(1);
  });

  it("keeps counters when the room empties and sleeps", async () => {
    const room = roomName();
    const { client } = await connect(room);
    for (let i = 0; i < 3; i++) await client.request({ t: "set", id: i, p: "a/b", v: i });
    client.close();
    await tick(50);
    await evictDurableObject(roomStub(room));
    const stats: any = await (await http(`/db/${room}/stats?key=${WRITE_KEY}`)).json();
    expect(stats.today).toMatchObject({ writes: 3, connects: 1 });
  });
});
