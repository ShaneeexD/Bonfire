import { env, exports } from "cloudflare:workers";
import { runInDurableObject } from "cloudflare:test";
import { expect } from "vitest";
import type { RoomDO } from "../src/room";

export const WRITE_KEY = "test-write";
export const READ_KEY = "test-read";

let counter = 0;
export function roomName(prefix = "room"): string {
  return `${prefix}-${counter++}-${Math.floor(Math.random() * 1e9)}`;
}

export function roomStub(room: string): DurableObjectStub {
  return env.ROOM.get(env.ROOM.idFromName(room)) as unknown as DurableObjectStub;
}

/** Runs `fn` inside the room's Durable Object, with access to its private state (tests only). */
export function inRoom(room: string, fn: (instance: any) => void): Promise<void> {
  return runInDurableObject<RoomDO, void>(roomStub(room), (instance) => fn(instance));
}

/** The main Worker's fetch handler (the generated `Exports` type doesn't describe the default export). */
const worker = (exports as unknown as { default: Fetcher }).default;

export function http(path: string, init?: RequestInit) {
  return worker.fetch(`https://fs.test${path}`, init);
}

/** A test WebSocket client that records every server message. */
export class TestClient {
  private queue: any[] = [];
  private waiters: { pred: (m: any) => boolean; resolve: (m: any) => void }[] = [];
  closed: { code: number; reason: string } | null = null;

  constructor(readonly ws: WebSocket) {
    ws.addEventListener("message", (e) => {
      const data = JSON.parse(e.data as string);
      for (const m of Array.isArray(data) ? data : [data]) this.push(m);
    });
    ws.addEventListener("close", (e) => {
      this.closed = { code: e.code, reason: e.reason };
    });
  }

  private push(m: any) {
    const i = this.waiters.findIndex((w) => w.pred(m));
    if (i >= 0) this.waiters.splice(i, 1)[0].resolve(m);
    else this.queue.push(m);
  }

  send(m: unknown) {
    this.ws.send(typeof m === "string" ? m : JSON.stringify(m));
  }

  /** Resolves with (and consumes) the first message matching `pred`. */
  next(pred: (m: any) => boolean = () => true, timeoutMs = 2000): Promise<any> {
    const i = this.queue.findIndex(pred);
    if (i >= 0) return Promise.resolve(this.queue.splice(i, 1)[0]);
    return new Promise((resolve, reject) => {
      const w = { pred, resolve };
      this.waiters.push(w);
      setTimeout(() => {
        const idx = this.waiters.indexOf(w);
        if (idx >= 0) {
          this.waiters.splice(idx, 1);
          reject(new Error(`timed out waiting for message; queued: ${JSON.stringify(this.queue)}`));
        }
      }, timeoutMs);
    });
  }

  nextOf(t: string, extra: (m: any) => boolean = () => true) {
    return this.next((m) => m.t === t && extra(m));
  }

  /** Messages received but not consumed yet. */
  pending(): any[] {
    return [...this.queue];
  }

  async request(m: { id: number; [k: string]: unknown }) {
    this.send(m);
    return this.next((r) => r.id === m.id && (r.t === "ack" || r.t === "err" || r.t === "snap"));
  }

  close() {
    this.ws.close(1000, "bye");
  }
}

export async function connect(room: string, opts: { key?: string; token?: string } = {}): Promise<{ client: TestClient; welcome: any }> {
  const headers: Record<string, string> = { Upgrade: "websocket", Authorization: `Bearer ${opts.key ?? WRITE_KEY}` };
  if (opts.token) headers["X-Bonfire-Token"] = opts.token;
  const res = await http(`/db/${room}?v=1`, { headers });
  expect(res.status).toBe(101);
  const ws = res.webSocket!;
  ws.accept();
  const client = new TestClient(ws);
  const welcome = await client.nextOf("welcome");
  return { client, welcome };
}

export const tick = (ms = 20) => new Promise((r) => setTimeout(r, ms));
