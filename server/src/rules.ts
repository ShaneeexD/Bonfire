// Path rules: which clients may read/write where. The admin role (write key) bypasses all rules.
//
// A rule pattern is a path whose segments are literals, "*" (any one key), "$cid" (the connecting client's id)
// or "$uid" (the connecting player's *verified* id, from a signed identity token; it never matches an
// unverified connection). A rule applies to its path and everything below it. For each setting
// (read, write, maxBytes, ephemeral) the most specific matching rule that sets it wins.

import { type Path } from "./paths";
import type { Role } from "./protocol";

/**
 * "public" = anyone with the read key; "verified" = read-key clients with a valid identity token;
 * "admin" = write key only.
 */
export type Access = "public" | "verified" | "admin";

/** Who is asking: client id, verified player id (if the connection presented a valid identity token), role. */
export interface Who {
  cid: string;
  uid?: string;
  role: Role;
}

export interface PathRule {
  path: string;
  read?: Access;
  write?: Access;
  /** Maximum JSON size of a value written at or below this path. */
  maxBytes?: number;
  /** Broadcast writes but never store them (costs no rows). Values are lost when the room sleeps. */
  ephemeral?: boolean;
}

export interface RoomRules {
  /** Glob for room names ("*" matches any characters). */
  match: string;
  default?: { read?: Access; write?: Access };
  paths?: PathRule[];
}

export interface RulesConfig {
  default: { read?: Access; write?: Access };
  paths?: PathRule[];
  rooms?: RoomRules[];
}

export interface Effective {
  read: Access;
  write: Access;
  maxBytes?: number;
  ephemeral: boolean;
}

interface Compiled {
  segs: string[];
  rule: PathRule;
  score: number;
}

function segMatch(pattern: string, key: string, who: Who): boolean {
  if (pattern === "*") return true;
  if (pattern === "$cid") return key === who.cid;
  if (pattern === "$uid") return who.uid !== undefined && key === who.uid;
  return pattern === key;
}

function globToRegExp(glob: string): RegExp {
  const esc = glob.replace(/[.+?^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*");
  return new RegExp(`^${esc}$`);
}

export function allowed(access: Access, who: Who): boolean {
  return who.role === "admin" || access === "public" || (access === "verified" && who.uid !== undefined);
}

function describe(access: Access): string {
  return access === "verified" ? "a verified player identity" : access;
}

export class Rules {
  private readonly compiled: Compiled[];
  private readonly def: { read: Access; write: Access };

  constructor(cfg: RulesConfig, room: string) {
    let def = { read: "public" as Access, write: "admin" as Access, ...cfg.default };
    const list: PathRule[] = [...(cfg.paths ?? [])];
    for (const r of cfg.rooms ?? []) {
      if (!globToRegExp(r.match).test(room)) continue;
      def = { ...def, ...r.default };
      list.push(...(r.paths ?? []));
    }
    this.def = def;
    this.compiled = list
      .map((rule, i) => {
        const segs = rule.path.replace(/^\/+|\/+$/g, "").split("/").filter((s) => s !== "");
        const literals = segs.filter((s) => s !== "*" && s !== "$cid" && s !== "$uid").length;
        // Deeper beats shallower, literal beats wildcard, later beats earlier.
        return { segs, rule, score: segs.length * 1_000_000 + literals * 1000 + i };
      })
      .sort((a, b) => a.score - b.score);
  }

  /** Settings that apply at `path` for this client. */
  effective(path: Path, who: Who): Effective {
    const eff: Effective = { read: this.def.read, write: this.def.write, ephemeral: false };
    for (const c of this.compiled) {
      if (c.segs.length > path.length) continue;
      if (!c.segs.every((s, i) => segMatch(s, path[i], who))) continue;
      if (c.rule.read) eff.read = c.rule.read;
      if (c.rule.write) eff.write = c.rule.write;
      if (c.rule.maxBytes !== undefined) eff.maxBytes = c.rule.maxBytes;
      if (c.rule.ephemeral !== undefined) eff.ephemeral = c.rule.ephemeral;
    }
    return eff;
  }

  /** Rules strictly below `path` that may apply to some descendant of it. */
  private below(path: Path, who: Who): PathRule[] {
    return this.compiled
      .filter((c) => c.segs.length > path.length && path.every((k, i) => segMatch(c.segs[i], k, who)))
      .map((c) => c.rule);
  }

  /** Null when allowed, otherwise the reason. Writing at a path replaces everything below it. */
  checkWrite(path: Path, who: Who, eff = this.effective(path, who)): string | null {
    if (who.role === "admin") return null;
    if (!allowed(eff.write, who)) return `write to "/${path.join("/")}" requires ${describe(eff.write)}`;
    for (const r of this.below(path, who)) {
      if (r.write && !allowed(r.write, who)) return `write to "/${path.join("/")}" would overwrite "${r.path}", which requires ${describe(r.write)}`;
    }
    return null;
  }

  checkRead(path: Path, who: Who): string | null {
    if (who.role === "admin") return null;
    const eff = this.effective(path, who);
    if (!allowed(eff.read, who)) return `read of "/${path.join("/")}" requires ${describe(eff.read)}`;
    for (const r of this.below(path, who)) {
      if (r.read && !allowed(r.read, who)) return `read of "/${path.join("/")}" would include "${r.path}", which requires ${describe(r.read)}`;
    }
    return null;
  }
}
