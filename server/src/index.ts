// Worker front door. Checks keys and room names *before* touching a Durable Object, so rejected
// requests never cost a DO request, then forwards to the room's object.
//
//   wss://<host>/db/<room>?v=1                  realtime connection
//   GET https://<host>/db/<room>/snapshot?path=  one-off read (cached briefly per data centre)
//   GET https://<host>/db/<room>/stats           usage counters (write key only)
//   POST https://<host>/identity                 issue a signed player identity token (write key only)

import { loadConfig, type Config, type BonfireEnv } from "./config";
import { parsePath, joinPath } from "./paths";
import { IdentityError, checkPlayerId, issueIdentity, verifyIdentity } from "./identity";
import { PROTOCOL_VERSION, type Role } from "./protocol";

export { RoomDO } from "./room";

const encoder = new TextEncoder();

function json(status: number, code: string, msg: string, headers?: Record<string, string>): Response {
  return Response.json({ code, msg }, { status, headers: { "Cache-Control": "no-store", ...headers } });
}

function safeEqual(a: string, b: string): boolean {
  const x = encoder.encode(a);
  const y = encoder.encode(b);
  if (x.length !== y.length) {
    crypto.subtle.timingSafeEqual(x, x);
    return false;
  }
  return crypto.subtle.timingSafeEqual(x, y);
}

/** The role a key grants, or null for a wrong key. Without a READ_KEY, reading is open to all. */
function roleFor(key: string | null, env: BonfireEnv): Role | null {
  if (key && env.WRITE_KEY && safeEqual(key, env.WRITE_KEY)) return "admin";
  if (!env.READ_KEY) return "public";
  if (key && safeEqual(key, env.READ_KEY)) return "public";
  return null;
}

function keyFrom(req: Request, url: URL): string | null {
  const auth = req.headers.get("Authorization");
  if (auth?.startsWith("Bearer ")) return auth.slice(7).trim();
  return url.searchParams.get("key");
}

const BASE32 = "abcdefghijklmnopqrstuvwxyz234567";

/** Stable client id derived from a secret token: nobody can claim your id without your token. */
export async function clientIdFor(room: string, token: string): Promise<string> {
  const hash = new Uint8Array(await crypto.subtle.digest("SHA-256", encoder.encode(`${room}\n${token}`)));
  let bits = 0;
  let value = 0;
  let out = "";
  for (const byte of hash) {
    value = (value << 8) | byte;
    bits += 8;
    while (bits >= 5 && out.length < 16) {
      out += BASE32[(value >>> (bits - 5)) & 31];
      bits -= 5;
    }
    if (out.length >= 16) break;
  }
  return out;
}

let cachedConfig: { env: BonfireEnv; cfg: Config } | null = null;
function config(env: BonfireEnv): Config {
  if (cachedConfig?.env !== env) cachedConfig = { env, cfg: loadConfig(env) };
  return cachedConfig.cfg;
}

export default {
  async fetch(req: Request, env: BonfireEnv, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(req.url);
    if (url.pathname === "/" || url.pathname === "") return new Response("Bonfire is running.\n");
    if (url.pathname === "/identity") return identityEndpoint(req, url, env, config(env));
    const m = url.pathname.match(/^\/db\/([^/]+)(?:\/(snapshot|stats))?\/?$/);
    if (!m) return json(404, "not_found", "unknown endpoint");

    let room: string;
    try {
      room = decodeURIComponent(m[1]);
    } catch {
      return json(400, "bad_room", "invalid room name");
    }
    const cfg = config(env);
    if (!cfg.roomPattern.test(room)) return json(400, "bad_room", "invalid room name");

    const role = roleFor(keyFrom(req, url), env);
    if (!role) return json(401, "unauthorized", "missing or wrong key");

    const stub = env.ROOM.get(env.ROOM.idFromName(room));
    const headers = { "x-fs-role": role, "x-fs-room": room };

    try {
      switch (m[2]) {
        case undefined:
          return await connect(req, url, room, headers, stub, env, cfg);
        case "snapshot":
          return await snapshot(req, url, room, role, headers, stub, ctx);
        case "stats":
          if (role !== "admin") return json(403, "permission_denied", "stats need the write key");
          return await stub.fetch("https://room/stats", { headers });
      }
    } catch (e) {
      // Most likely the Durable Object request quota (or a transient platform error).
      console.error("room unavailable", String(e));
      return json(503, "quota", "room unavailable (daily quota may be exhausted)", { "Retry-After": "60" });
    }
    return json(404, "not_found", "unknown endpoint");
  },
} satisfies ExportedHandler<BonfireEnv>;

async function connect(
  req: Request,
  url: URL,
  room: string,
  headers: Record<string, string>,
  stub: DurableObjectStub,
  env: BonfireEnv,
  cfg: Config,
): Promise<Response> {
  if (req.headers.get("Upgrade")?.toLowerCase() !== "websocket") return json(426, "upgrade_required", "expected a WebSocket upgrade");
  const v = url.searchParams.get("v") ?? String(PROTOCOL_VERSION);
  if (v !== String(PROTOCOL_VERSION)) return json(426, "unsupported_version", `server speaks protocol v${PROTOCOL_VERSION}`);
  // A signed identity token proves who the player is; it takes precedence over the self-chosen client token.
  const identity = req.headers.get("X-Bonfire-Identity") ?? url.searchParams.get("identity");
  if (identity) {
    if (!env.IDENTITY_SECRET) return json(400, "identity_not_configured", "this server has no IDENTITY_SECRET, so it can't verify identity tokens");
    try {
      const id = await verifyIdentity(identity, env.IDENTITY_SECRET, room, Math.floor(Date.now() / 1000), cfg.identityMaxDays);
      return stub.fetch("https://room/connect", { headers: { ...headers, "x-fs-cid": id.sub, "x-fs-uid": id.sub, Upgrade: "websocket" } });
    } catch (e) {
      if (e instanceof IdentityError) return json(401, "bad_identity", e.message);
      throw e;
    }
  }

  const token = req.headers.get("X-Bonfire-Token") ?? url.searchParams.get("token");
  let cid: string;
  if (token) {
    if (token.length < 16 || token.length > 256) return json(400, "bad_token", "client token must be 16-256 characters");
    cid = await clientIdFor(room, token);
  } else {
    cid = "anon-" + crypto.randomUUID().replace(/-/g, "").slice(0, 11);
  }
  return stub.fetch("https://room/connect", { headers: { ...headers, "x-fs-cid": cid, Upgrade: "websocket" } });
}

async function snapshot(
  req: Request,
  url: URL,
  room: string,
  role: Role,
  headers: Record<string, string>,
  stub: DurableObjectStub,
  ctx: ExecutionContext,
): Promise<Response> {
  if (req.method !== "GET" && req.method !== "HEAD") return json(405, "method_not_allowed", "use GET");
  let path: string;
  try {
    path = joinPath(parsePath(url.searchParams.get("path") ?? ""));
  } catch (e) {
    return json(400, "bad_path", (e as Error).message);
  }
  const inm = req.headers.get("If-None-Match");
  const target = `https://room/snapshot?path=${encodeURIComponent(path)}`;

  // Admin reads may include data the public can't see, so only public reads are cached.
  if (role === "admin") return stub.fetch(target, { headers: inm ? { ...headers, "If-None-Match": inm } : headers });

  const cache = caches.default;
  const cacheKey = new Request(`${url.origin}/db/${encodeURIComponent(room)}/snapshot?path=${encodeURIComponent(path)}`);
  let res = await cache.match(cacheKey);
  if (!res) {
    res = await stub.fetch(target, { headers });
    if (res.status === 200) ctx.waitUntil(cache.put(cacheKey, res.clone()));
  }
  const etag = res.headers.get("ETag");
  if (inm && etag && inm === etag) return new Response(null, { status: 304, headers: res.headers });
  return res;
}

/**
 * POST /identity with the write key and a JSON body {"player": "<id>", "ttlSeconds": 86400, "room": "<optional>"}
 * returns {"token": "...", "player": "...", "expires": "<ISO time>"}. For backends that would rather make an HTTP
 * call than sign JWTs themselves. Runs in the Worker only (no Durable Object request).
 */
async function identityEndpoint(req: Request, url: URL, env: BonfireEnv, cfg: Config): Promise<Response> {
  if (req.method !== "POST") return json(405, "method_not_allowed", "use POST");
  if (roleFor(keyFrom(req, url), env) !== "admin") return json(403, "permission_denied", "issuing identities needs the write key");
  if (!env.IDENTITY_SECRET) return json(400, "identity_not_configured", "set the IDENTITY_SECRET secret first");
  let body: { player?: unknown; ttlSeconds?: unknown; room?: unknown };
  try {
    body = await req.json();
  } catch {
    return json(400, "bad_request", "body must be JSON: {\"player\": \"...\"}");
  }
  const bad = checkPlayerId(body.player);
  if (bad) return json(400, "bad_player", bad);
  const maxTtl = cfg.identityMaxDays * 86400;
  const ttl = typeof body.ttlSeconds === "number" && body.ttlSeconds > 0 ? Math.min(Math.floor(body.ttlSeconds), maxTtl) : Math.min(86400, maxTtl);
  if (body.room !== undefined && typeof body.room !== "string") return json(400, "bad_request", "room must be a string");
  const now = Math.floor(Date.now() / 1000);
  const token = await issueIdentity(env.IDENTITY_SECRET, body.player as string, now, now + ttl, body.room as string | undefined);
  return Response.json(
    { token, player: body.player, expires: new Date((now + ttl) * 1000).toISOString() },
    { headers: { "Cache-Control": "no-store" } },
  );
}
