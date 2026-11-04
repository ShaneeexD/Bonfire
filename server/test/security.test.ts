import { evictDurableObject } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { clientIdFor } from "../src/index";
import { Rules } from "../src/rules";
import { connect, http, inRoom, READ_KEY, roomName, roomStub, tick, WRITE_KEY } from "./helpers";

const p = (s: string) => (s === "" ? [] : s.split("/"));

describe("rules (unit)", () => {
  const rules = new Rules(
    {
      default: { read: "public", write: "admin" },
      paths: [
        { path: "players/$cid", write: "public", maxBytes: 100 },
        { path: "players/$cid/pos", ephemeral: true },
        { path: "chat/*", write: "public" },
        { path: "chat/pinned", write: "admin" },
        { path: "secret", read: "admin" },
      ],
      rooms: [{ match: "open-*", default: { write: "public" } }],
    },
    "lobby",
  );

  it("lets clients write only under their own id", () => {
    expect(rules.checkWrite(p("players/abc/score"), "abc", "public")).toBeNull();
    expect(rules.checkWrite(p("players/xyz/score"), "abc", "public")).toMatch(/requires admin/);
    expect(rules.checkWrite(p("players"), "abc", "public")).toMatch(/requires admin/);
    expect(rules.checkWrite(p("players/xyz"), "abc", "admin")).toBeNull();
  });

  it("checks rules below the written path", () => {
    expect(rules.checkWrite(p("chat/123"), "abc", "public")).toBeNull();
    expect(rules.checkWrite(p("chat/pinned"), "abc", "public")).toMatch(/requires admin/);
    expect(rules.checkRead(p(""), "abc", "public")).toMatch(/would include "secret"/);
    expect(rules.checkRead(p("secret"), "abc", "admin")).toBeNull();
  });

  it("inherits settings from less specific rules", () => {
    const eff = rules.effective(p("players/abc/pos/x"), "abc");
    expect(eff).toMatchObject({ write: "public", ephemeral: true, maxBytes: 100 });
    expect(rules.effective(p("players/abc/name"), "abc").ephemeral).toBe(false);
  });

  it("applies per-room overrides", () => {
    const open = new Rules({ default: { read: "public", write: "admin" }, rooms: [{ match: "open-*", default: { write: "public" } }] }, "open-1");
    expect(open.checkWrite(p("anything"), "abc", "public")).toBeNull();
    const closed = new Rules({ default: { read: "public", write: "admin" }, rooms: [{ match: "open-*", default: { write: "public" } }] }, "lobby");
    expect(closed.checkWrite(p("anything"), "abc", "public")).not.toBeNull();
  });
});

describe("roles and rules (integration, rules from wrangler.jsonc)", () => {
  it("derives the same client id as the C# client", async () => {
    expect(await clientIdFor("global", "my-secret-token-123456")).toBe("jghcqgrt4btbgyfs");
  });

  it("public clients write their own subtree; admin writes anywhere", async () => {
    const room = roomName();
    const { client, welcome } = await connect(room, { key: READ_KEY, token: "player-token-0123456789" });
    expect(welcome.role).toBe("public");
    const mine = await client.request({ t: "set", id: 1, p: `players/${welcome.cid}/score`, v: 5 });
    expect(mine.t).toBe("ack");
    const other = await client.request({ t: "set", id: 2, p: "players/someone/score", v: 5 });
    expect(other).toMatchObject({ t: "err", code: "permission_denied" });
    const above = await client.request({ t: "rm", id: 3, p: "players" });
    expect(above.code).toBe("permission_denied");
    const big = await client.request({ t: "set", id: 4, p: `players/${welcome.cid}/bio`, v: "x".repeat(5000) });
    expect(big.code).toBe("value_too_large");

    const { client: admin } = await connect(room, { key: WRITE_KEY });
    expect((await admin.request({ t: "set", id: 1, p: "config/motd", v: "hi" })).t).toBe("ack");
    expect((await client.request({ t: "set", id: 5, p: "config/motd", v: "hacked" })).code).toBe("permission_denied");
  });

  it("anonymous clients get a fresh id per connection", async () => {
    const room = roomName();
    const a = await connect(room, { key: READ_KEY });
    const b = await connect(room, { key: READ_KEY });
    expect(a.welcome.cid).not.toBe(b.welcome.cid);
  });
});

describe("validation and limits", () => {
  it("rejects oversized frames and too many messages per frame", async () => {
    const { client } = await connect(roomName());
    client.send({ t: "set", id: 1, p: "a/b", v: "x".repeat(70_000) });
    expect(await client.nextOf("err")).toMatchObject({ code: "frame_too_large" });
    client.send(Array.from({ length: 101 }, (_, i) => ({ t: "ping", id: i })));
    expect(await client.nextOf("err")).toMatchObject({ code: "bad_request" });
  });

  it("rejects values nested too deeply (chunk size limits are covered in store.test.ts)", async () => {
    const { client } = await connect(roomName());
    let deep: unknown = 1;
    for (let i = 0; i < 40; i++) deep = { k: deep };
    expect((await client.request({ t: "set", id: 1, p: "a/b", v: deep })).code).toBe("bad_value");
  });

  it("rate-limits a connection and closes it after repeated abuse", async () => {
    const { client } = await connect(roomName());
    const frame = Array.from({ length: 100 }, (_, i) => ({ t: "set", p: `spam/k${i % 5}`, v: i }));
    client.send(frame); // uses the whole burst
    client.send([{ t: "set", id: 1, p: "spam/x", v: 1 }]);
    const err = await client.next((m) => m.id === 1);
    expect(err).toMatchObject({ t: "err", code: "rate_limited" });
    expect(err.retryMs).toBeGreaterThan(0);
    // Keep spamming far above 20 msgs/s; the server must eventually close the socket.
    for (let i = 0; i < 50 && !client.closed; i++) {
      for (let j = 0; j < 20; j++) client.send([{ t: "ping" }]);
      await tick(10);
    }
    for (let i = 0; i < 100 && !client.closed; i++) await tick(10);
    expect(client.closed?.code).toBe(4008);
  });

  it("caps subscriptions per connection", async () => {
    const { client } = await connect(roomName());
    client.send(Array.from({ length: 64 }, (_, i) => ({ t: "sub", p: `s/${i}` })));
    for (let i = 0; i < 64; i++) await client.nextOf("snap");
    expect((await client.request({ t: "sub", id: 99, p: "s/extra" })).code).toBe("too_many_subs");
  });
});

describe("onDisconnect", () => {
  it("runs registered writes when the socket closes, and supports cancel", async () => {
    const room = roomName();
    const { client: watcher } = await connect(room);
    await watcher.request({ t: "sub", id: 1, p: "presence" });
    const { client: player, welcome } = await connect(room, { key: READ_KEY, token: "presence-token-0123456789" });
    const me = `presence/${welcome.cid}`;
    await player.request({ t: "set", id: 1, p: me, v: "online" });
    expect((await player.request({ t: "od", id: 2, op: "rm", p: me })).t).toBe("ack");
    expect((await player.request({ t: "od", id: 3, op: "set", p: "config/x", v: 1 })).code).toBe("permission_denied");
    // Register and cancel a second op; only the first should run.
    await player.request({ t: "od", id: 4, op: "set", p: `players/${welcome.cid}/left`, v: true });
    await player.request({ t: "od", id: 5, op: "cancel", p: `players/${welcome.cid}` });
    await watcher.nextOf("patch", (m) => m.ops[0][1] === "online");

    player.close();
    const gone = await watcher.nextOf("patch");
    expect(gone.ops).toEqual([[me, null]]);
    await tick(50);
    const snap = await watcher.request({ t: "sub", id: 9, p: "players" });
    expect(snap.v).toBeNull();
  });

  it("runs orphaned ops on the next connection if the close event was missed", async () => {
    const room = roomName();
    const { client: player, welcome } = await connect(room, { key: READ_KEY, token: "orphan-token-0123456789" });
    const me = `presence/${welcome.cid}`;
    await player.request({ t: "set", id: 1, p: me, v: "online" });
    await player.request({ t: "od", id: 2, op: "rm", p: me });

    // Tear the object down and close its sockets without running handlers on this instance.
    await evictDurableObject(roomStub(room), { webSockets: "close" });
    await tick(50);

    const { client: next } = await connect(room);
    const snap = await next.request({ t: "sub", id: 1, p: "presence" });
    expect(snap.v).toBeNull();
  });

  it("limits ops per connection", async () => {
    const { client, welcome } = await connect(roomName(), { key: READ_KEY, token: "limit-token-0123456789" });
    for (let i = 0; i < 16; i++) await client.request({ t: "od", id: i, op: "rm", p: `presence/${welcome.cid}` });
    expect((await client.request({ t: "od", id: 99, op: "rm", p: `presence/${welcome.cid}` })).code).toBe("too_many_on_disconnect");
  });
});

describe("ephemeral paths", () => {
  it("broadcasts without storing, and forgets values when the room sleeps", async () => {
    const room = roomName();
    const { client: watcher } = await connect(room);
    await watcher.request({ t: "sub", id: 1, p: "players" });
    const { client: player, welcome } = await connect(room, { key: READ_KEY, token: "ephemeral-token-0123456789" });
    const base = `players/${welcome.cid}`;
    await player.request({ t: "set", id: 1, p: `${base}/name`, v: "Ann" });
    const statsBefore: any = await (await http(`/db/${room}/stats?key=${WRITE_KEY}`)).json();

    for (let i = 0; i < 5; i++) await player.request({ t: "set", id: 10 + i, p: `${base}/pos`, v: { x: i } });
    const patch = await watcher.nextOf("patch", (m) => m.ops[0][0] === `${base}/pos` && m.ops[0][1].x === 4);
    expect(patch.ops).toEqual([[`${base}/pos`, { x: 4 }]]);

    const statsAfter: any = await (await http(`/db/${room}/stats?key=${WRITE_KEY}`)).json();
    expect(statsAfter.today.rows).toBe(statsBefore.today.rows); // no storage writes
    expect((await watcher.request({ t: "sub", id: 2, p: base })).v).toEqual({ name: "Ann", pos: { x: 4 } });
    const snapshot = await (await http(`/db/${room}/snapshot?path=${base}&key=${WRITE_KEY}`)).json();
    expect(snapshot).toEqual({ name: "Ann" }); // the HTTP snapshot only has stored data

    await evictDurableObject(roomStub(room));
    expect((await watcher.request({ t: "sub", id: 3, p: base })).v).toEqual({ name: "Ann" });
  });
});

describe("quota guards", () => {
  it("refuses stored writes once the room's daily row budget is used, but not reads", async () => {
    const room = roomName();
    const { client } = await connect(room);
    await client.request({ t: "set", id: 1, p: "a/b", v: 1 });
    await inRoom(room, (instance) => {
      instance.stats.today().rows = 1e9;
    });
    expect((await client.request({ t: "set", id: 2, p: "a/b", v: 2 })).code).toBe("room_quota");
    expect((await client.request({ t: "sub", id: 3, p: "a" })).v).toEqual({ b: 1 });
  });

  it("turns a storage quota exception into write_quota and stays read-only until midnight", async () => {
    const room = roomName();
    const { client } = await connect(room);
    await client.request({ t: "set", id: 1, p: "a/b", v: 1 });
    await inRoom(room, (instance) => {
      instance.store._commit = () => {
        throw new Error("Exceeded allowed rows written in Durable Objects free tier.");
      };
    });
    expect((await client.request({ t: "set", id: 2, p: "a/b", v: 2 })).code).toBe("write_quota");
    expect((await client.request({ t: "sub", id: 3, p: "a" })).v).toEqual({ b: 1 });
    const stats: any = await (await http(`/db/${room}/stats?key=${WRITE_KEY}`)).json();
    expect(stats.writeQuotaUntil).toMatch(/T00:00:00/);
    expect(stats.today.quota).toBeGreaterThan(0);
  });
});
