import { describe, expect, it } from "vitest";
import { connect, http, READ_KEY, roomName, WRITE_KEY } from "./helpers";

describe("snapshot caching", () => {
  it("serves repeated public reads from the Cache API without asking the room", async () => {
    const room = roomName();
    const { client } = await connect(room);
    await client.request({ t: "set", id: 1, p: "cfg/motd", v: "v1" });

    const first = await http(`/db/${room}/snapshot?path=cfg&key=${READ_KEY}`);
    expect(await first.json()).toEqual({ motd: "v1" });
    const etag = first.headers.get("ETag");

    await client.request({ t: "set", id: 2, p: "cfg/motd", v: "v2" });

    // Within max-age the cached copy is served, so the room's newer value isn't visible yet.
    const cached = await http(`/db/${room}/snapshot?path=/cfg/&key=${READ_KEY}`);
    expect(await cached.json()).toEqual({ motd: "v1" });
    expect(cached.headers.get("ETag")).toBe(etag);
    const revalidated = await http(`/db/${room}/snapshot?path=cfg`, { headers: { Authorization: `Bearer ${READ_KEY}`, "If-None-Match": etag! } });
    expect(revalidated.status).toBe(304);

    // Admin reads are never cached (they may include data the public can't see).
    const admin = await http(`/db/${room}/snapshot?path=cfg&key=${WRITE_KEY}`);
    expect(await admin.json()).toEqual({ motd: "v2" });
    expect(admin.headers.get("ETag")).not.toBe(etag);
  });

  it("does not cache refusals", async () => {
    const room = roomName();
    const res = await http(`/db/${room}/snapshot?path=a.b&key=${READ_KEY}`);
    expect(res.status).toBe(400);
    expect(res.headers.get("Cache-Control")).toBe("no-store");
  });

  it("returns null for missing data", async () => {
    const res = await http(`/db/${roomName()}/snapshot?path=nothing/here&key=${READ_KEY}`);
    expect(res.status).toBe(200);
    expect(await res.json()).toBeNull();
  });
});
