// Shared settings for the backup/restore scripts: where the Worker is and the write key.
// Environment variables win; otherwise the git-ignored files written when deploying are used.
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const serverDir = join(dirname(fileURLToPath(import.meta.url)), "..");

export function loadConfig() {
  let url = process.env.FREESYNC_URL;
  let key = process.env.FREESYNC_WRITE_KEY;
  const urlFile = join(serverDir, ".deployed-url");
  const keysFile = join(serverDir, ".deploy-keys.env");
  if (!url && existsSync(urlFile)) url = readFileSync(urlFile, "utf8").replace(/^﻿/, "").trim();
  if (!key && existsSync(keysFile)) key = readFileSync(keysFile, "utf8").match(/^WRITE_KEY=(.*)$/m)?.[1]?.trim();
  if (!url || !key) {
    fail("Set FREESYNC_URL and FREESYNC_WRITE_KEY, or create server/.deployed-url and server/.deploy-keys.env (see README).");
  }
  const http = url.replace(/^ws(s?):\/\//, "http$1://").replace(/\/+$/, "");
  return { http, ws: http.replace(/^http/, "ws"), key };
}

/** Minimal --flag value / --switch parsing. */
export function parseArgs(argv) {
  const args = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith("--")) args._.push(a);
    else if (i + 1 < argv.length && !argv[i + 1].startsWith("--")) args[a.slice(2)] = argv[++i];
    else args[a.slice(2)] = true;
  }
  return args;
}

export function fail(message) {
  console.error("error: " + message);
  process.exit(1);
}
