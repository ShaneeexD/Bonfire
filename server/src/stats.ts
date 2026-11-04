// Approximate per-room usage counters per UTC day.
// Kept in memory and stored in a meta row inside write transactions that happen anyway: on each
// rev-block reservation (first write after waking), every 20 writes, at least once a minute while
// writes continue, and on day rollover. Also stored when the room's last connection closes.
// That costs at most ~5% extra rows. Counts since the last flush are lost if the room hibernates
// without writing again, so the numbers are approximate (they undercount, never overcount).

import type { Store } from "./store";

export interface DayStats {
  day: string;
  /** Accepted write operations (set/upd/rm, including ephemeral and onDisconnect). */
  writes: number;
  /** SQLite rows written for document data and metadata. */
  rows: number;
  connects: number;
  /** Incoming WebSocket frames (20 frames = 1 billed request). */
  frames: number;
  /** Operations refused by rules, validation or rate limits. */
  denied: number;
  /** Operations refused because a storage quota was hit. */
  quota: number;
}

const KEEP_DAYS = 7;
const FLUSH_EVERY_WRITES = 20;
const FLUSH_EVERY_MS = 60_000;

export function utcDay(now = Date.now()): string {
  return new Date(now).toISOString().slice(0, 10);
}

export class Stats {
  private days: DayStats[] | null = null;
  private unflushed = 0;
  private rolled = false;
  private dirty = false;
  private lastFlush = 0;

  constructor(private readonly store: Store) {}

  all(): DayStats[] {
    if (!this.days) this.days = (this.store.getMeta("stats") as unknown as DayStats[] | null) ?? [];
    return this.days;
  }

  today(): DayStats {
    const days = this.all();
    const day = utcDay();
    if (days[0]?.day !== day) {
      days.unshift({ day, writes: 0, rows: 0, connects: 0, frames: 0, denied: 0, quota: 0 });
      days.length = Math.min(days.length, KEEP_DAYS);
      this.rolled = true;
    }
    return days[0];
  }

  bump(field: Exclude<keyof DayStats, "day">, n = 1): void {
    this.today()[field] += n;
    this.dirty = true;
    if (field === "writes") this.unflushed += n;
  }

  /** Whether the next write transaction should also store the counters. */
  wantsFlush(): boolean {
    return this.rolled || this.unflushed >= FLUSH_EVERY_WRITES || (this.dirty && Date.now() - this.lastFlush >= FLUSH_EVERY_MS);
  }

  /** Whether anything was counted since the last flush. */
  isDirty(): boolean {
    return this.dirty;
  }

  /** Stores the counters; call inside a write transaction. Returns rows written. */
  flush(): number {
    this.unflushed = 0;
    this.rolled = false;
    this.dirty = false;
    this.lastFlush = Date.now();
    const rows = this.store.setMeta("stats", this.all() as never);
    this.today().rows += rows;
    return rows;
  }

  /** Forget in-memory counters (after a rolled-back transaction). */
  reset(): void {
    this.days = null;
    this.unflushed = 0;
    this.dirty = false;
  }
}
