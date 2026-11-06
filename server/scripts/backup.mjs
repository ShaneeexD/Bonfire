// Saves a room (or part of it) to a timestamped JSON backup file and keeps the newest N.
//
//   npm run backup -- --room global                      backups/global/global-export-<time>.json
//   npm run backup -- --room global --path players --out D:\Backups --keep 90
//
// Run it from Task Scheduler or cron for daily backups. Reads the stored data through the snapshot
// endpoint with the write key (uncached), so ephemeral values like positions are not included.
// The file is the same format the database editor exports and can restore.
import { mkdirSync, readdirSync, unlinkSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { fail, loadConfig, parseArgs } from "./freesync-config.mjs";

const args = parseArgs(process.argv.slice(2));
const room = args.room;
if (!room || room === true) fail("--room <name> is required");
const path = typeof args.path === "string" ? args.path.replace(/^\/+|\/+$/g, "") : "";
const keep = Number(args.keep ?? 30);
const outDir = resolve(typeof args.out === "string" ? args.out : "backups", room);

const { http, key } = loadConfig();
const res = await fetch(`${http}/db/${encodeURIComponent(room)}/snapshot?path=${encodeURIComponent(path)}`, {
  headers: { Authorization: `Bearer ${key}` },
});
if (!res.ok) fail(`the server answered HTTP ${res.status}: ${await res.text()}`);
const value = await res.json();

const created = new Date();
const backup = {
  format: "freesync-backup",
  version: 1,
  room,
  kind: "export",
  createdAt: created.toISOString(),
  rev: Number(res.headers.get("X-FreeSync-Rev") ?? 0),
  note: "npm run backup",
  entries: [{ path, value }],
};
mkdirSync(outDir, { recursive: true });
const stamp = created.toISOString().replace(/[-:]/g, "").replace("T", "-").replace(/\..*/, "");
const file = join(outDir, `${room}-export-${stamp}.json`);
const json = JSON.stringify(backup);
writeFileSync(file, json);

// Keep the newest `keep` backups of this room in the folder.
const old = readdirSync(outDir).filter((f) => f.endsWith(".json")).sort().reverse().slice(keep);
for (const f of old) unlinkSync(join(outDir, f));

const kb = (Buffer.byteLength(json) / 1024).toFixed(1);
console.log(`saved /${path} of room '${room}' (${kb} KB, rev ${backup.rev}) to ${file}${old.length ? `; removed ${old.length} old backup(s)` : ""}`);
