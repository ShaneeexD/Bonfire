import { describe, expect, it } from "vitest";
import { issueIdentity, verifyIdentity } from "../src/identity";
import { Rules } from "../src/rules";
import { connect, http, READ_KEY, roomName, WRITE_KEY } from "./helpers";

const SECRET = "test-identity-secret"; // matches vitest.config.ts
const now = () => Math.floor(Date.now() / 1000);
const p = (s: string) => s.split("/");

// The same token is produced by the C# FreeSyncIdentity.Create test; both sides must agree byte for byte.
const VECTOR =
  "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJwbGF5ZXItNDIiLCJpYXQiOjE4MDAwMDAwMDAsImV4cCI6MTgwMDA4NjQwMCwicm9vbSI6Imdsb2JhbCJ9.v2yC4brhKZy3b65rj4SO5TJEycquTVk5npg0mMkbK94";

describe("identity tokens (unit)", () => {
  it("signs standard HS256 JWTs, identical to the C# helper and any JWT library", async () => {
    expect(await issueIdentity("vector-secret", "player-42", 1800000000, 1800086400, "global")).toBe(VECTOR);
    const id = await verifyIdentity(VECTOR, "vector-secret", "global", 1800000100, 30);
    expect(id).toEqual({ sub: "player-42", exp: 1800086400 });
  });

  it("rejects tampering, other algorithms, expiry, long lifetimes, wrong rooms and bad ids", async () => {
    const t = now();
    const good = await issueIdentity(SECRET, "p1", t, t + 3600);
    await expect(verifyIdentity(good, "another-secret", "r", t, 30)).rejects.toThrow(/bad signature/);
    const [h, , s] = good.split(".");
    const forged = `${h}.${btoa(JSON.stringify({ sub: "admin", exp: t + 3600 })).replace(/=+$/, "")}.${s}`;
    await expect(verifyIdentity(forged, SECRET, "r", t, 30)).rejects.toThrow(/bad signature|malformed/);
    const none = `${btoa('{"alg":"none"}').replace(/=+$/, "")}.${good.split(".")[1]}.`;
    await expect(verifyIdentity(none, SECRET, "r", t, 30)).rejects.toThrow(/HS256/);
    await expect(verifyIdentity(await issueIdentity(SECRET, "p1", t - 7200, t - 3600), SECRET, "r", t, 30)).rejects.toThrow(/expired/);
    await expect(verifyIdentity(await issueIdentity(SECRET, "p1", t, t + 40 * 86400), SECRET, "r", t, 30)).rejects.toThrow(/more than 30 days/);
    await expect(verifyIdentity(await issueIdentity(SECRET, "p1", t, t + 60, "lobby-*"), SECRET, "global", t, 30)).rejects.toThrow(/another room/);
    expect((await verifyIdentity(await issueIdentity(SECRET, "p1", t, t + 60, "lobby-*"), SECRET, "lobby-7", t, 30)).sub).toBe("p1");
    await expect(verifyIdentity(await issueIdentity(SECRET, "a/b", t, t + 60), SECRET, "r", t, 30)).rejects.toThrow(/not a valid key/);
    await expect(verifyIdentity("not-a-token", SECRET, "r", t, 30)).rejects.toThrow(/malformed/);
  });

  it("$uid and verified only ever match verified connections", () => {
    const rules = new Rules(
      { default: { read: "public", write: "admin" }, paths: [{ path: "profiles/$uid", write: "public" }, { path: "lounge", write: "verified" }] },
      "r",
    );
    const verified = { cid: "p1", uid: "p1", role: "public" as const };
    const unverified = { cid: "p1", role: "public" as const }; // same id, but self-chosen
    expect(rules.checkWrite(p("profiles/p1/bio"), verified)).toBeNull();
    expect(rules.checkWrite(p("profiles/p1/bio"), unverified)).toMatch(/requires admin/);
    expect(rules.checkWrite(p("profiles/p2/bio"), verified)).toMatch(/requires admin/);
    expect(rules.checkWrite(p("lounge/msg"), verified)).toBeNull();
    expect(rules.checkWrite(p("lounge/msg"), unverified)).toMatch(/verified player identity/);
  });
});

describe("identity tokens (integration, rules from wrangler.jsonc)", () => {
  const ws = (room: string, identity: string) =>
    http(`/db/${room}?v=1`, { headers: { Upgrade: "websocket", Authorization: `Bearer ${READ_KEY}`, "X-FreeSync-Identity": identity } });

  it("a verified player gets their id as cid and can write profiles/$uid; others can't", async () => {
    const room = roomName();
    const t = now();
    const res = await ws(room, await issueIdentity(SECRET, "steam-76561198000000001", t, t + 3600));
    expect(res.status).toBe(101);
    const sock = res.webSocket!;
    sock.accept();
    const msgs: any[] = [];
    sock.addEventListener("message", (e) => msgs.push(...[].concat(JSON.parse(e.data as string))));
    const until = async (pred: (m: any) => boolean) => {
      for (let i = 0; i < 100; i++) {
        const m = msgs.find(pred);
        if (m) return m;
        await new Promise((r) => setTimeout(r, 10));
      }
      throw new Error("timeout: " + JSON.stringify(msgs));
    };
    const welcome = await until((m) => m.t === "welcome");
    expect(welcome).toMatchObject({ cid: "steam-76561198000000001", verified: true, role: "public" });
    sock.send(JSON.stringify({ t: "set", id: 1, p: "profiles/steam-76561198000000001/bio", v: "hi" }));
    expect(await until((m) => m.id === 1)).toMatchObject({ t: "ack" });

    // An unverified client can't write a profile, not even one matching its own client id.
    const { client, welcome: w2 } = await connect(room, { key: READ_KEY, token: "unverified-token-0123456789" });
    expect(w2.verified).toBe(false);
    expect((await client.request({ t: "set", id: 2, p: `profiles/${w2.cid}/bio`, v: "x" })).code).toBe("permission_denied");
  });

  it("refuses bad identity tokens in the Worker, before the room is touched", async () => {
    const room = roomName();
    const t = now();
    expect((await ws(room, await issueIdentity("wrong-secret", "p1", t, t + 60))).status).toBe(401);
    const expired = await ws(room, await issueIdentity(SECRET, "p1", t - 7200, t - 3600));
    expect(expired.status).toBe(401);
    expect(await expired.json()).toMatchObject({ code: "bad_identity", msg: "token expired" });
  });

  it("POST /identity issues tokens with the write key only", async () => {
    const post = (key: string, body: unknown) =>
      http("/identity", { method: "POST", headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" }, body: JSON.stringify(body) });
    expect((await post(READ_KEY, { player: "p1" })).status).toBe(403);
    expect((await post(WRITE_KEY, { player: "bad/id" })).status).toBe(400);
    const res = await post(WRITE_KEY, { player: "p9", ttlSeconds: 120, room: "global" });
    expect(res.status).toBe(200);
    const body: any = await res.json();
    expect(body.player).toBe("p9");
    const id = await verifyIdentity(body.token, SECRET, "global", now(), 30);
    expect(id.sub).toBe("p9");
    expect(id.exp - now()).toBeLessThanOrEqual(121);
    // Lifetimes are capped at IDENTITY_MAX_DAYS.
    const long: any = await (await post(WRITE_KEY, { player: "p9", ttlSeconds: 365 * 86400 })).json();
    expect((await verifyIdentity(long.token, SECRET, "r", now(), 30)).exp - now()).toBeLessThanOrEqual(30 * 86400 + 1);
  });
});
