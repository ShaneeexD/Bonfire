// JSON value helpers. Values are plain JSON; `null` means "absent".
// Objects never contain null or empty children (they are pruned), like Firebase.
// Arrays are opaque leaves: paths cannot address inside them.

import { checkKey, type Path } from "./paths";

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };
export type JsonObject = { [key: string]: Json };

export const MAX_VALUE_DEPTH = 32;

export class ValueError extends Error {}

export function isObject(v: unknown): v is JsonObject {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

/**
 * Validates an incoming value and returns its normalised form:
 * null children and empty objects/arrays are removed; an empty result becomes null.
 * Throws ValueError for non-JSON values, bad keys, non-finite numbers or excessive depth.
 */
export function normalize(v: unknown, depth = 0): Json {
  if (depth > MAX_VALUE_DEPTH) throw new ValueError(`value nested deeper than ${MAX_VALUE_DEPTH} levels`);
  if (v === null || v === undefined) return null;
  switch (typeof v) {
    case "boolean":
    case "string":
      return v;
    case "number":
      if (!Number.isFinite(v)) throw new ValueError("numbers must be finite");
      return v;
    case "object": {
      if (Array.isArray(v)) {
        if (v.length === 0) return null;
        // Array elements keep their positions, so nulls stay as nulls.
        return v.map((e) => normalize(e, depth + 1));
      }
      const out: JsonObject = {};
      let any = false;
      for (const [k, child] of Object.entries(v as Record<string, unknown>)) {
        const err = checkKey(k);
        if (err) throw new ValueError(err);
        const n = normalize(child, depth + 1);
        if (n !== null) {
          out[k] = n;
          any = true;
        }
      }
      return any ? out : null;
    }
    default:
      throw new ValueError(`unsupported value type ${typeof v}`);
  }
}

/** Reads the value at `path` below `root`. Paths never descend into arrays or scalars. */
export function getIn(root: Json, path: Path, from = 0): Json {
  let cur: Json = root;
  for (let i = from; i < path.length; i++) {
    if (!isObject(cur)) return null;
    cur = cur[path[i]] ?? null;
  }
  return cur;
}

/**
 * Returns a copy of `root` with `value` placed at `path` (from index `from`), copying only the
 * objects along the path. A null value deletes the key and prunes parents that become empty.
 * Scalars or arrays in the way are replaced by objects.
 */
export function setIn(root: Json, path: Path, value: Json, from = 0): Json {
  if (from >= path.length) return value;
  const key = path[from];
  const base: JsonObject = isObject(root) ? root : {};
  const child = setIn(base[key] ?? null, path, value, from + 1);
  const copy: JsonObject = { ...base };
  if (child === null) delete copy[key];
  else copy[key] = child;
  for (const _ in copy) return copy;
  return null;
}
