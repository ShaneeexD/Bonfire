import { describe, expect, it } from "vitest";
import { isPrefix, parsePath, PathError } from "../src/paths";
import { opsFor } from "../src/room";
import { getIn, normalize, setIn, ValueError } from "../src/value";

describe("paths", () => {
  it("parses and trims slashes", () => {
    expect(parsePath("")).toEqual([]);
    expect(parsePath("/")).toEqual([]);
    expect(parsePath("/a/b/")).toEqual(["a", "b"]);
  });

  it("rejects bad keys, depth and length", () => {
    for (const bad of ["a//b", "a.b", "a#", "x/$y", "a[0]", "tab\there"]) expect(() => parsePath(bad)).toThrow(PathError);
    expect(() => parsePath(Array(33).fill("k").join("/"))).toThrow(PathError);
    expect(() => parsePath("x".repeat(600))).toThrow(PathError);
    expect(() => parsePath(42)).toThrow(PathError);
  });

  it("prefix checks", () => {
    expect(isPrefix([], ["a"])).toBe(true);
    expect(isPrefix(["a"], ["a", "b"])).toBe(true);
    expect(isPrefix(["a", "b"], ["a"])).toBe(false);
    expect(isPrefix(["a"], ["ab"])).toBe(false);
  });
});

describe("values", () => {
  it("normalises nulls and empty containers away", () => {
    expect(normalize({ a: null, b: {}, c: { d: null }, e: [], f: 1 })).toEqual({ f: 1 });
    expect(normalize({})).toBeNull();
    expect(normalize([1, null, { x: null }])).toEqual([1, null, null]);
  });

  it("rejects invalid values", () => {
    expect(() => normalize(NaN)).toThrow(ValueError);
    expect(() => normalize({ "a.b": 1 })).toThrow(ValueError);
    expect(() => normalize(() => 1)).toThrow(ValueError);
    let deep: unknown = 1;
    for (let i = 0; i < 40; i++) deep = { k: deep };
    expect(() => normalize(deep)).toThrow(ValueError);
  });

  it("setIn copies along the path and prunes", () => {
    const root = { a: { b: 1, c: 2 }, z: { y: 1 } };
    const next = setIn(root, ["a", "b"], null);
    expect(next).toEqual({ a: { c: 2 }, z: { y: 1 } });
    expect(root.a.b).toBe(1); // original untouched
    expect((next as any).z).toBe(root.z); // untouched branches are shared
    expect(setIn({ a: { b: 1 } }, ["a", "b"], null)).toBeNull();
    expect(setIn(5, ["a"], 1)).toEqual({ a: 1 });
  });

  it("getIn does not descend into arrays or scalars", () => {
    expect(getIn({ a: [1, 2] }, ["a", "0"])).toBeNull();
    expect(getIn({ a: 1 }, ["a", "b"])).toBeNull();
    expect(getIn({ a: { b: 2 } }, ["a", "b"])).toBe(2);
  });
});

describe("opsFor (diff trimming)", () => {
  const p = (s: string) => (s === "" ? [] : s.split("/"));

  it("sends writes at or below a subscription unchanged", () => {
    expect(opsFor([p("players")], [{ path: p("players/a/x"), value: 1 }])).toEqual([["players/a/x", 1]]);
  });

  it("trims writes above a subscription to the subscribed slice", () => {
    const ops = opsFor([p("players/a")], [{ path: p("players"), value: { a: { hp: 3 }, b: { hp: 9 } } }]);
    expect(ops).toEqual([["players/a", { hp: 3 }]]);
  });

  it("ignores unrelated writes and de-duplicates overlapping subscriptions", () => {
    expect(opsFor([p("chat")], [{ path: p("players/a"), value: 1 }])).toEqual([]);
    const ops = opsFor([p("a"), p("a/b")], [{ path: p("a/b/c"), value: 1 }]);
    expect(ops).toEqual([["a/b/c", 1]]);
    const ops2 = opsFor([p("a/b"), p("a/b/c")], [{ path: p("a"), value: { b: { c: 1 } } }]);
    expect(ops2).toEqual([["a/b", { c: 1 }]]);
  });
});
