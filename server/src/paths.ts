// Path handling. A path is a list of object keys; "" / "/" is the root.
// Stored and sent on the wire as segments joined by "/", without leading or trailing slashes.

export const MAX_PATH_DEPTH = 32;
export const MAX_PATH_BYTES = 512;

export type Path = readonly string[];

// Forbidden in keys: the separator, Firebase's reserved characters, and control characters.
const BAD_SEGMENT = /[\/.#$\[\]\u0000-\u001f\u007f]/;

export class PathError extends Error {}

const encoder = new TextEncoder();

export function utf8Length(s: string): number {
  return encoder.encode(s).length;
}

/** Validates a single key. Returns an error message, or null when the key is acceptable. */
export function checkKey(key: string): string | null {
  if (key.length === 0) return "empty key";
  if (BAD_SEGMENT.test(key)) return `key "${key}" contains a forbidden character (/ . # $ [ ] or control)`;
  return null;
}

/** Parses and validates a wire path. Throws PathError when invalid. */
export function parsePath(raw: unknown): Path {
  if (typeof raw !== "string") throw new PathError("path must be a string");
  if (utf8Length(raw) > MAX_PATH_BYTES) throw new PathError(`path longer than ${MAX_PATH_BYTES} bytes`);
  const trimmed = raw.replace(/^\/+|\/+$/g, "");
  if (trimmed === "") return [];
  const segs = trimmed.split("/");
  if (segs.length > MAX_PATH_DEPTH) throw new PathError(`path deeper than ${MAX_PATH_DEPTH} levels`);
  for (const s of segs) {
    const err = checkKey(s);
    if (err) throw new PathError(err);
  }
  return segs;
}

export function joinPath(p: Path): string {
  return p.join("/");
}

/** True when `a` equals `b` or is an ancestor of `b`. */
export function isPrefix(a: Path, b: Path): boolean {
  if (a.length > b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

/** True when one path is at, above, or below the other. */
export function overlaps(a: Path, b: Path): boolean {
  return isPrefix(a, b) || isPrefix(b, a);
}
