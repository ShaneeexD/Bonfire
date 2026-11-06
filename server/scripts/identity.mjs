// Prints a signed player identity token (HS256 JWT), for testing or for backends without a JWT library.
//
//   npm run identity -- --player steam-76561198000000001                 valid for 24 hours, any room
//   npm run identity -- --player alice --hours 2 --room lobby-*
//
// The secret comes from IDENTITY_SECRET in the environment, server/.deploy-keys.env, or (with --dev)
// server/.dev.vars for `npm run dev`. Never put the secret in a client mod.
import { createHmac } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { fail, parseArgs } from "./freesync-config.mjs";

const args = parseArgs(process.argv.slice(2));
const player = args.player;
if (!player || player === true) fail("--player <id> is required");
if (/[\/.#$\[\]\u0000-\u001f\u007f]/.test(player)) fail("the player id can't contain / . # $ [ ] or control characters");
const hours = Number(args.hours ?? 24);
if (!(hours > 0)) fail("--hours must be a positive number");

const serverDir = join(dirname(fileURLToPath(import.meta.url)), "..");
const file = join(serverDir, args.dev ? ".dev.vars" : ".deploy-keys.env");
let secret = process.env.IDENTITY_SECRET;
if (!secret && existsSync(file)) secret = readFileSync(file, "utf8").match(/^IDENTITY_SECRET=(.*)$/m)?.[1]?.trim();
if (!secret) fail(`no IDENTITY_SECRET (set the environment variable or add IDENTITY_SECRET=... to ${file})`);

const b64 = (s) => Buffer.from(s).toString("base64url");
const now = Math.floor(Date.now() / 1000);
const claims = { sub: player, iat: now, exp: now + Math.round(hours * 3600) };
if (typeof args.room === "string") claims.room = args.room;
const unsigned = `${b64(JSON.stringify({ alg: "HS256", typ: "JWT" }))}.${b64(JSON.stringify(claims))}`;
const token = `${unsigned}.${createHmac("sha256", secret).update(unsigned).digest("base64url")}`;

console.log(token);
console.error(`(player ${player}, expires ${new Date(claims.exp * 1000).toISOString()}${claims.room ? `, room ${claims.room}` : ""})`);
