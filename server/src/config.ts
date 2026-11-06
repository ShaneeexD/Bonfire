// Configuration from Wrangler vars. Every setting is optional and has a hobby-friendly default.
// Vars may be given as numbers/objects (wrangler.jsonc) or strings (dashboard, .dev.vars).

import type { RulesConfig } from "./rules";

export interface Config {
  /** Depth at which the tree is cut into rows. Fixed per room after its first write. */
  chunkDepth: number;
  /** Largest accepted incoming WebSocket frame, in bytes. */
  maxFrameBytes: number;
  /** Largest JSON size of one stored chunk (SQLite rows are limited to 2 MB). */
  maxChunkBytes: number;
  /** Writes that add data are refused once the room's database is this large. */
  maxRoomBytes: number;
  /** Stored rows each room may write per UTC day (protects the account-wide 100k/day budget). */
  roomDailyWriteRows: number;
  /** Cache-Control max-age for the snapshot endpoint, in seconds. */
  snapshotMaxAge: number;
  /** Largest snapshot or subscription value the server will send, in bytes. */
  maxSnapshotBytes: number;
  /** Maximum subscriptions per connection (they live in the 16 KB socket attachment). */
  maxSubs: number;
  /** Maximum messages in one frame. */
  maxOpsPerFrame: number;
  /** Maximum onDisconnect operations per connection. */
  maxOnDisconnect: number;
  /** Per-connection token bucket: messages per second, burst size, and bytes per second. */
  rateOpsPerSec: number;
  rateBurst: number;
  rateBytesPerSec: number;
  /** Allowed room names. */
  roomPattern: RegExp;
  /** Longest validity accepted for identity tokens, in days. */
  identityMaxDays: number;
  rules: RulesConfig;
}

export interface FreeSyncEnv {
  ROOM: DurableObjectNamespace;
  READ_KEY?: string;
  WRITE_KEY?: string;
  /** Shared with your backend; signs identity tokens (HS256 JWTs). Optional. */
  IDENTITY_SECRET?: string;
  [key: string]: unknown;
}

function num(v: unknown, def: number, min = 0): number {
  const n = typeof v === "number" ? v : typeof v === "string" && v.trim() !== "" ? Number(v) : NaN;
  return Number.isFinite(n) && n >= min ? n : def;
}

const DEFAULT_RULES: RulesConfig = { default: { read: "public", write: "admin" }, paths: [] };

function rules(v: unknown): RulesConfig {
  if (v === undefined || v === null || v === "") return DEFAULT_RULES;
  const obj = typeof v === "string" ? JSON.parse(v) : v;
  return { ...DEFAULT_RULES, ...(obj as RulesConfig) };
}

export function loadConfig(env: FreeSyncEnv): Config {
  return {
    chunkDepth: Math.floor(num(env.CHUNK_DEPTH, 2, 1)),
    maxFrameBytes: num(env.MAX_FRAME_BYTES, 64 * 1024, 256),
    maxChunkBytes: num(env.MAX_CHUNK_BYTES, 1_000_000, 64),
    maxRoomBytes: num(env.MAX_ROOM_BYTES, 50_000_000),
    roomDailyWriteRows: num(env.ROOM_DAILY_WRITE_ROWS, 25_000),
    snapshotMaxAge: num(env.SNAPSHOT_MAX_AGE, 10),
    maxSnapshotBytes: num(env.MAX_SNAPSHOT_BYTES, 8_000_000),
    maxSubs: num(env.MAX_SUBS, 64, 1),
    maxOpsPerFrame: num(env.MAX_OPS_PER_FRAME, 100, 1),
    maxOnDisconnect: num(env.MAX_ON_DISCONNECT, 16),
    rateOpsPerSec: num(env.RATE_OPS_PER_SEC, 20),
    rateBurst: num(env.RATE_BURST, 100, 1),
    rateBytesPerSec: num(env.RATE_BYTES_PER_SEC, 32 * 1024),
    identityMaxDays: num(env.IDENTITY_MAX_DAYS, 30, 0),
    roomPattern: new RegExp(typeof env.ROOM_PATTERN === "string" && env.ROOM_PATTERN ? env.ROOM_PATTERN : "^[A-Za-z0-9_-]{1,64}$"),
    rules: rules(env.RULES),
  };
}
