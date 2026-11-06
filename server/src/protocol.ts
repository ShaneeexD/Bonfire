// Wire protocol v1. See docs/PROTOCOL.md.

export const PROTOCOL_VERSION = 1;

/** Sent by clients as an exact string; answered by the runtime without waking the object. */
export const PING = '{"t":"ping"}';
export const PONG = '{"t":"pong"}';

export type Role = "public" | "admin";

/** Error codes carried by `err` messages. */
export const Err = {
  BadRequest: "bad_request",
  BadPath: "bad_path",
  BadValue: "bad_value",
  PermissionDenied: "permission_denied",
  RateLimited: "rate_limited",
  TooManySubs: "too_many_subs",
  TooManyOnDisconnect: "too_many_on_disconnect",
  ChunkTooLarge: "chunk_too_large",
  ValueTooLarge: "value_too_large",
  FrameTooLarge: "frame_too_large",
  SnapshotTooLarge: "snapshot_too_large",
  RoomFull: "room_full",
  RoomQuota: "room_quota",
  WriteQuota: "write_quota",
  Internal: "internal",
} as const;

/** WebSocket close codes used by the server. */
export const Close = {
  Auth: 4001,
  RateLimit: 4008,
  Quota: 4029,
  Protocol: 4400,
} as const;

/** Per-connection state kept in the WebSocket attachment (max 16 KB, survives hibernation). */
export interface Attachment {
  /** Connection id; also the socket's tag and the onDisconnect owner. */
  conn: string;
  /** Client id (stable across reconnects when the client sends a token). */
  cid: string;
  /** Verified player id, when the connection presented a valid identity token (then cid === uid). */
  uid?: string;
  role: Role;
  /** Room name (the object needs it for per-room rules after waking from hibernation). */
  room: string;
  /** Subscribed paths in wire form. */
  subs: string[];
  /** Rate-limit bucket: op tokens, byte tokens, last refill time (ms), strikes. */
  rl: { o: number; b: number; at: number; s: number };
  /** Number of onDisconnect ops registered (so close handlers can skip the query). */
  od: number;
}
