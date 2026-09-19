#!/usr/bin/env node
// Minimal MCP stdio client: proves a configured server command actually
// speaks MCP (initialize + tools/list) without calling any tools.
// Usage: node mcp-probe.mjs [--env K=V | --env=K=V ...] -- <command> [args...]
//
// Exit codes:
//   0  handshake completed, non-empty tool list
//   1  real failure (missing binary, no MCP response, timeout, no tools)
//   2  usage error
//   3  key-gated startup: the server validates its API key at startup and
//      refused the (test) credential — binary itself launches and initializes
// Server stderr is captured (for the key-gated detection), never echoed.

import { spawn } from "node:child_process";
import readline from "node:readline";

const argv = process.argv.slice(2);
const envPairs = [];
let command = null;
const args = [];
for (let i = 0; i < argv.length; i++) {
  const a = argv[i];
  if (a === "--") {
    args.push(...argv.slice(i + 1));
    break;
  }
  if (a === "--env") {
    envPairs.push(argv[++i]);
    continue;
  }
  if (a.startsWith("--env=")) {
    envPairs.push(a.slice("--env=".length));
    continue;
  }
  if (command === null) command = a;
  else args.push(a);
}
// "--" may appear before the command; the first positional always is it.
if (command === null && args.length > 0) command = args.shift();

if (!command) {
  console.error("mcp-probe: usage: mcp-probe.mjs [--env K=V ...] -- <command> [args...]");
  process.exit(2);
}

const childEnv = { ...process.env };
for (const pair of envPairs) {
  const eq = pair.indexOf("=");
  if (eq > 0) childEnv[pair.slice(0, eq)] = pair.slice(eq + 1);
}

let serverOutput = "";
let failed = false;

// The vision server validates its API key at startup and may refuse with a
// diagnostic on either stream before speaking a word of MCP.
function keyGated() {
  return /api key/i.test(serverOutput);
}

function fail(msg, code = 1) {
  if (failed) return;
  failed = true;
  console.error(`mcp-probe: FAIL: ${msg}`);
  try {
    child.kill("SIGKILL");
  } catch {}
  process.exit(code);
}

function classifyEarlyExit(code) {
  if (failed) return;
  if (keyGated()) {
    console.error("mcp-probe: key-gated: server validates its API key at startup");
    process.exit(3);
  }
  fail(`server exited early (code ${code ?? "null"}) before completing the handshake`);
}

const child = spawn(command, args, { env: childEnv, stdio: ["pipe", "pipe", "pipe"] });
child.stdout.on("data", (chunk) => {
  serverOutput += chunk.toString();
});
child.stderr.on("data", (chunk) => {
  serverOutput += chunk.toString();
});
child.on("error", (err) => fail(`could not launch ${command}: ${err.message}`));
child.on("close", classifyEarlyExit);

const pending = new Map();
const rl = readline.createInterface({ input: child.stdout });
rl.on("line", (line) => {
  let msg;
  try {
    msg = JSON.parse(line);
  } catch {
    return;
  }
  if (msg && msg.id !== undefined && pending.has(msg.id)) {
    pending.get(msg.id)(msg);
    pending.delete(msg.id);
  }
});

const timer = setTimeout(() => fail("timeout waiting for MCP response (30s)"), 30000);

function request(id, method, params) {
  return new Promise((resolve) => {
    pending.set(id, resolve);
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
  });
}

const init = await request(1, "initialize", {
  protocolVersion: "2024-11-05",
  capabilities: {},
  clientInfo: { name: "signal-fish-mcp-probe", version: "1.0" },
}).catch(() => null);

if (!init || !init.result) {
  if (keyGated()) {
    console.error("mcp-probe: key-gated: server validates its API key at startup");
    child.kill("SIGKILL");
    clearTimeout(timer);
    process.exit(3);
  }
  fail(`initialize rejected: ${JSON.stringify(init?.error ?? init)}`);
}
child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");

const tools = await request(2, "tools/list", {});
const count = tools?.result?.tools?.length ?? 0;
if (!count) fail("no tools discovered");
console.error(`mcp-probe: ok: initialized; ${count} tools discovered`);
child.kill("SIGKILL");
clearTimeout(timer);
process.exit(0);
