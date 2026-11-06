// Verified player identities: JWTs signed with HMAC-SHA256 (HS256) by a backend you trust.
//
// The Worker and your backend share IDENTITY_SECRET. A token says "this connection is player <sub>"; rules can
// then trust that id ($uid) instead of the self-chosen client id ($cid). Any JWT library can issue tokens:
//   header  {"alg":"HS256","typ":"JWT"}
//   payload {"sub":"<player id>","iat":<unix s>,"exp":<unix s>,"room":"<optional room or glob>"}

import { checkKey } from "./paths";

export interface Identity {
  /** The verified player id. */
  sub: string;
  exp: number;
}

export class IdentityError extends Error {}

const encoder = new TextEncoder();
/** Expiry tolerance for clock differences between your backend and Cloudflare. */
const SKEW_SECONDS = 60;
const MAX_SUB_LENGTH = 128;

function b64urlEncode(bytes: Uint8Array): string {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function b64urlDecode(s: string): Uint8Array {
  if (!/^[A-Za-z0-9_-]*$/.test(s)) throw new IdentityError("malformed token");
  const pad = s.length % 4 === 0 ? "" : "=".repeat(4 - (s.length % 4));
  const bin = atob(s.replace(/-/g, "+").replace(/_/g, "/") + pad);
  return Uint8Array.from(bin, (c) => c.charCodeAt(0));
}

const keys = new Map<string, Promise<CryptoKey>>();
function hmacKey(secret: string): Promise<CryptoKey> {
  let k = keys.get(secret);
  if (!k) {
    k = crypto.subtle.importKey("raw", encoder.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign", "verify"]);
    keys.set(secret, k);
  }
  return k;
}

function roomMatches(pattern: string, room: string): boolean {
  const re = new RegExp("^" + pattern.replace(/[.+?^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*") + "$");
  return re.test(room);
}

/** Creates a token. Used by POST /identity; backends can use any HS256 JWT library instead. */
export async function issueIdentity(secret: string, sub: string, iat: number, exp: number, room?: string): Promise<string> {
  const header = b64urlEncode(encoder.encode(JSON.stringify({ alg: "HS256", typ: "JWT" })));
  const claims: Record<string, unknown> = { sub, iat, exp };
  if (room) claims.room = room;
  const payload = b64urlEncode(encoder.encode(JSON.stringify(claims)));
  const sig = new Uint8Array(await crypto.subtle.sign("HMAC", await hmacKey(secret), encoder.encode(`${header}.${payload}`)));
  return `${header}.${payload}.${b64urlEncode(sig)}`;
}

/** Checks a player id is usable as a path key (players/<id>) and not absurdly long. */
export function checkPlayerId(sub: unknown): string | null {
  if (typeof sub !== "string" || sub.length === 0) return "the token has no player id (sub)";
  if (sub.length > MAX_SUB_LENGTH) return `player id longer than ${MAX_SUB_LENGTH} characters`;
  const bad = checkKey(sub);
  return bad ? `player id is not a valid key: ${bad}` : null;
}

/**
 * Verifies a token for `room`. Throws IdentityError with a short reason on anything wrong: bad signature,
 * other algorithm, expired, valid for too long, wrong room, or an unusable player id.
 */
export async function verifyIdentity(token: string, secret: string, room: string, nowSeconds: number, maxDays: number): Promise<Identity> {
  const parts = token.split(".");
  if (parts.length !== 3) throw new IdentityError("malformed token");
  const [h, p, s] = parts;
  let header: Record<string, unknown>;
  let claims: Record<string, unknown>;
  try {
    header = JSON.parse(new TextDecoder().decode(b64urlDecode(h)));
    claims = JSON.parse(new TextDecoder().decode(b64urlDecode(p)));
  } catch {
    throw new IdentityError("malformed token");
  }
  // Only HS256: never accept "none" or another algorithm chosen by whoever made the token.
  if (header?.alg !== "HS256") throw new IdentityError("token must be signed with HS256");
  const ok = await crypto.subtle.verify("HMAC", await hmacKey(secret), b64urlDecode(s), encoder.encode(`${h}.${p}`));
  if (!ok) throw new IdentityError("bad signature");

  const exp = claims.exp;
  if (typeof exp !== "number") throw new IdentityError("the token has no expiry (exp)");
  if (exp + SKEW_SECONDS < nowSeconds) throw new IdentityError("token expired");
  if (exp - nowSeconds > maxDays * 86400 + SKEW_SECONDS) throw new IdentityError(`token valid for more than ${maxDays} days`);
  if (typeof claims.nbf === "number" && claims.nbf - SKEW_SECONDS > nowSeconds) throw new IdentityError("token not valid yet");
  if (claims.room !== undefined && (typeof claims.room !== "string" || !roomMatches(claims.room, room)))
    throw new IdentityError("token is for another room");
  const bad = checkPlayerId(claims.sub);
  if (bad) throw new IdentityError(bad);
  return { sub: claims.sub as string, exp };
}
