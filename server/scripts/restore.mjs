// Restores a backup file (from `npm run backup` or the database editor) into a room.
//
//   npm run restore -- backups/global/global-export-20261106-120000.json            shows what would happen
//   npm run restore -- <file> --yes                                                  restores into the same room
//   npm run restore -- <file> --room global-copy --path archive --yes                into another room/path
//
// Each entry REPLACES the data at its path. Needs Node 22+ (built-in WebSocket) and the write key.
// Large values are split into frames that fit the server's 64 KB limit, in an order that gives the same
// result as one big write (but only each frame is atomic).
import { readFileSync } from "node:fs";
import { fail, loadConfig, parseArgs } from "./bonfire-config.mjs";

const MAX_FRAME = 56_000;

const args = parseArgs(process.argv.slice(2));
const file = args._[0];
if (!file) fail("give the backup file to restore");
const backup = JSON.parse(readFileSync(file, "utf8").replace(/^﻿/, ""));
// "freesync-backup" is the format id from before the project was renamed.
if (!["bonfire-backup", "freesync-backup"].includes(backup.format) || !Array.isArray(backup.entries)) fail(`${file} is not a Bonfire backup file`);
const room = typeof args.room === "string" ? args.room : backup.room;
if (!room || room === "?") fail("the backup doesn't say which room it is from; pass --room <name>");
let entries = backup.entries.map((e) => ({ path: String(e.path ?? "").replace(/^\/+|\/+$/g, ""), value: e.value ?? null }));
if (typeof args.path === "string") {
  if (entries.length !== 1) fail("--path only works for backups with a single entry");
  entries = [{ path: args.path.replace(/^\/+|\/+$/g, ""), value: entries[0].value }];
}

// ---- split into frames (same rules as the database editor's ChangeSet.ToFrames) -----------------
const size = (path, value) => Buffer.byteLength(path) + Buffer.byteLength(JSON.stringify(value)) + 8;
const isObject = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
const frames = [];
let current = {};
let currentSize = 0;
const flush = () => {
  if (Object.keys(current).length) frames.push({ paths: current });
  current = {};
  currentSize = 0;
};
const add = (path, value, len) => {
  if (path === "") {
    flush();
    frames.push({ root: value });
    return;
  }
  if (Object.keys(current).length && currentSize + len > MAX_FRAME) flush();
  current[path] = value;
  currentSize += len;
};
const emit = (path, value) => {
  const len = size(path, value);
  if (len <= MAX_FRAME) return add(path, value, len);
  if (!isObject(value)) fail(`the value at /${path} is ${len} bytes and can't be split into frames`);
  const first = {};
  const rest = [];
  let firstSize = size(path, null);
  for (const [k, child] of Object.entries(value)) {
    const childLen = size(k, child);
    if (!rest.length && firstSize + childLen <= MAX_FRAME) {
      first[k] = child;
      firstSize += childLen;
    } else rest.push([k, child]);
  }
  add(path, Object.keys(first).length ? first : null, firstSize);
  flush(); // children must not share a frame with their parent path
  for (const [k, child] of rest) emit(path ? `${path}/${k}` : k, child);
};
for (const e of entries) emit(e.path, e.value);
flush();

const total = Buffer.byteLength(JSON.stringify(entries));
console.log(`backup: ${file}`);
console.log(`  from room '${backup.room}', ${backup.kind ?? "export"}, saved ${backup.createdAt ?? "?"}`);
console.log(`  restores into room '${room}': ${entries.map((e) => "/" + e.path).join(", ")} (${(total / 1024).toFixed(1)} KB, ${frames.length} frame(s))`);
if (!args.yes) {
  console.log("\nNothing was written. Add --yes to replace that data.");
  process.exit(0);
}

// ---- send ------------------------------------------------------------------------------------
const { ws: wsBase, key } = loadConfig();
if (typeof WebSocket === "undefined") fail("this needs Node 22 or newer (built-in WebSocket)");
const ws = new WebSocket(`${wsBase}/db/${encodeURIComponent(room)}?v=1&key=${encodeURIComponent(key)}`);
const inbox = [];
let wake = () => {};
ws.onmessage = (e) => {
  for (const m of [].concat(JSON.parse(e.data))) inbox.push(m);
  wake();
};
ws.onerror = () => fail("could not connect to the server");
const next = (pred, ms = 30_000) =>
  new Promise((res, rej) => {
    const timer = setTimeout(() => rej(new Error("timed out waiting for the server")), ms);
    const check = () => {
      const i = inbox.findIndex(pred);
      if (i < 0) return;
      clearTimeout(timer);
      res(inbox.splice(i, 1)[0]);
    };
    wake = check;
    check();
  });

const welcome = await next((m) => m.t === "welcome");
if (welcome.role !== "admin") fail("the key is not the write key");
let rev = 0;
for (let i = 0; i < frames.length; i++) {
  const f = frames[i];
  const message = JSON.stringify("root" in f ? { t: "set", id: i, p: "", v: f.root } : { t: "upd", id: i, p: "", v: f.paths });
  for (;;) {
    ws.send(message);
    const reply = await next((m) => m.id === i && (m.t === "ack" || m.t === "err"));
    if (reply.t === "ack") {
      rev = reply.rev;
      break;
    }
    // Older servers rate-limit the write key too: wait as suggested and send the same frame again.
    if (reply.code === "rate_limited") {
      await new Promise((r) => setTimeout(r, (reply.retryMs ?? 1000) + 50));
      continue;
    }
    fail(`frame ${i + 1}/${frames.length} was refused: ${reply.code}: ${reply.msg}`);
  }
  if (frames.length > 1) process.stdout.write(`\r  sent ${i + 1}/${frames.length} frames`);
}
if (frames.length > 1) process.stdout.write("\n");
console.log(`restored in ${frames.length} frame(s); room '${room}' is now at rev ${rev}`);
// Every frame is acknowledged, so don't wait for the close handshake (Node can linger on it for ~10 s).
ws.close(1000, "done");
process.exit(0);
