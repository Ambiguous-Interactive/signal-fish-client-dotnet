#!/usr/bin/env node
// Regenerate the per-harness MCP server configs inside the container.
//
// Idempotent: merges our managed server entries into each harness's config
// file, preserving all other user content. Prunes managed entries whose
// matching credential has disappeared (secret hygiene). Skips token-gated
// servers when the matching key is absent. Diagnostics on stderr; exits 0.
//
// Managed entries (written to every supported harness):
//   github          https://api.githubcopilot.com/mcp/         (Bearer PAT)
//   zai-vision      zai-mcp-server (global npm bin; npx in Cursor) (Z_AI_API_KEY)
//   zai-web-search  {zaiBase}/web_search_prime/mcp
//   zai-web-reader  {zaiBase}/web_reader/mcp
//   zai-zread       {zaiBase}/zread/mcp
//   microsoft-docs  https://learn.microsoft.com/api/mcp        (keyless)
//   context7        https://mcp.context7.com/mcp               (optional Bearer)
//   git             uvx mcp-server-git (only when uvx is on PATH)
// where zaiBase follows Z_AI_MODE: ZAI (default) -> https://api.z.ai/api/mcp,
// ZHIPU -> https://open.bigmodel.cn/api/mcp.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";

const GITHUB_MCP_URL = "https://api.githubcopilot.com/mcp/";
const MS_DOCS_MCP_URL = "https://learn.microsoft.com/api/mcp";
const CONTEXT7_MCP_URL = "https://mcp.context7.com/mcp";
const ZAI_BASE_URL =
  (process.env.Z_AI_MODE || "ZAI") === "ZHIPU"
    ? "https://open.bigmodel.cn/api/mcp"
    : "https://api.z.ai/api/mcp";
const ZAI_SEARCH_URL = `${ZAI_BASE_URL}/web_search_prime/mcp`;
const ZAI_READER_URL = `${ZAI_BASE_URL}/web_reader/mcp`;
const ZAI_ZREAD_URL = `${ZAI_BASE_URL}/zread/mcp`;

const env = process.env;
const ghPat = env.GITHUB_PERSONAL_ACCESS_TOKEN || "";
const zaiKey = env.Z_AI_API_KEY || "";
const zaiMode = (env.Z_AI_MODE || "ZAI") === "ZHIPU" ? "ZHIPU" : "ZAI";
const context7Key = env.CONTEXT7_API_KEY || "";

const log = (msg) => console.error(`write-mcp-configs: ${msg}`);

const ZAI_MANAGED_NAMES = ["zai-vision", "zai-web-search", "zai-web-reader", "zai-zread"];

function readJson(file) {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8"));
  } catch {
    return {};
  }
}

function writeJson(file, obj) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(obj, null, 2) + "\n", { mode: 0o600 });
}

function upsert(obj, section, name, entry) {
  obj[section] = { ...(obj[section] || {}), [name]: entry };
}

function prune(obj, section, name) {
  if (obj[section] && Object.prototype.hasOwnProperty.call(obj[section], name)) {
    delete obj[section][name];
    return true;
  }
  return false;
}

// ---------- server entry builders (per harness) ----------
// In-container harnesses use the globally installed `zai-mcp-server` binary
// (durable npm-prefix volume, no npx resolution at start). Host-targeted
// configs (Cursor) fall back to `npx -y @z_ai/mcp-server` because hosts have
// no npm-global volume.

const claudeCopilot = {
  github: (token) => ({
    type: "http",
    url: GITHUB_MCP_URL,
    headers: { Authorization: `Bearer ${token}` },
  }),
  vision: (key) => ({
    type: "stdio",
    command: "zai-mcp-server",
    env: { Z_AI_API_KEY: key, Z_AI_MODE: zaiMode },
  }),
  remote: (url, key) => ({
    type: "http",
    url,
    headers: { Authorization: `Bearer ${key}` },
  }),
  docs: () => ({ type: "http", url: MS_DOCS_MCP_URL }),
  context7: () => ({
    type: "http",
    url: CONTEXT7_MCP_URL,
    ...(context7Key ? { headers: { Authorization: `Bearer ${context7Key}` } } : {}),
  }),
  git: () => ({ type: "stdio", command: "uvx", args: ["mcp-server-git"] }),
};

const opencode = {
  github: () => ({
    type: "remote",
    url: GITHUB_MCP_URL,
    enabled: true,
    oauth: false,
    headers: { Authorization: "Bearer {env:GITHUB_PERSONAL_ACCESS_TOKEN}" },
  }),
  vision: () => ({
    type: "local",
    command: ["zai-mcp-server"],
    enabled: true,
    environment: { Z_AI_API_KEY: "{env:Z_AI_API_KEY}", Z_AI_MODE: zaiMode },
  }),
  remote: (url) => ({
    type: "remote",
    url,
    enabled: true,
    oauth: false,
    headers: { Authorization: "Bearer {env:Z_AI_API_KEY}" },
  }),
  docs: () => ({ type: "remote", url: MS_DOCS_MCP_URL, enabled: true }),
  context7: () => ({
    type: "remote",
    url: CONTEXT7_MCP_URL,
    enabled: true,
    ...(context7Key
      ? { headers: { Authorization: "Bearer {env:CONTEXT7_API_KEY}" } }
      : {}),
  }),
  git: () => ({ type: "local", command: ["uvx", "mcp-server-git"], enabled: true }),
};

const nanocoder = {
  github: () => ({
    transport: "http",
    url: GITHUB_MCP_URL,
    headers: { Authorization: "Bearer ${GITHUB_PERSONAL_ACCESS_TOKEN}" },
  }),
  vision: () => ({
    transport: "stdio",
    command: "zai-mcp-server",
    env: { Z_AI_API_KEY: "${Z_AI_API_KEY}", Z_AI_MODE: zaiMode },
  }),
  remote: (url) => ({
    transport: "http",
    url,
    headers: { Authorization: "Bearer ${Z_AI_API_KEY}" },
  }),
  docs: () => ({ transport: "http", url: MS_DOCS_MCP_URL }),
  context7: () => ({
    transport: "http",
    url: CONTEXT7_MCP_URL,
    ...(context7Key
      ? { headers: { Authorization: "Bearer ${CONTEXT7_API_KEY}" } }
      : {}),
  }),
  git: () => ({ transport: "stdio", command: "uvx", args: ["mcp-server-git"] }),
};

const gemini = {
  github: (token) => ({
    httpUrl: GITHUB_MCP_URL,
    headers: { Authorization: `Bearer ${token}` },
  }),
  vision: (key) => ({
    command: "zai-mcp-server",
    env: { Z_AI_API_KEY: key, Z_AI_MODE: zaiMode },
  }),
  remote: (url, key) => ({
    httpUrl: url,
    headers: { Authorization: `Bearer ${key}` },
  }),
  docs: () => ({ httpUrl: MS_DOCS_MCP_URL }),
  context7: () => ({
    httpUrl: CONTEXT7_MCP_URL,
    ...(context7Key ? { headers: { Authorization: `Bearer ${context7Key}` } } : {}),
  }),
  git: () => ({ command: "uvx", args: ["mcp-server-git"] }),
};

const cursor = {
  github: (token) => ({
    url: GITHUB_MCP_URL,
    headers: { Authorization: `Bearer ${token}` },
  }),
  vision: (key) => ({
    command: "npx",
    args: ["-y", "@z_ai/mcp-server"],
    env: { Z_AI_API_KEY: key, Z_AI_MODE: zaiMode },
  }),
  remote: (url, key) => ({
    url,
    headers: { Authorization: `Bearer ${key}` },
  }),
  docs: () => ({ url: MS_DOCS_MCP_URL }),
  context7: () => ({
    url: CONTEXT7_MCP_URL,
    ...(context7Key ? { headers: { Authorization: `Bearer ${context7Key}` } } : {}),
  }),
  git: () => ({ command: "uvx", args: ["mcp-server-git"] }),
};

// The git MCP server needs uv's tool runner; gate the managed entry on the
// binary actually existing so hosts without uv get a pruned entry instead of
// a permanently failing server.
function uvxAvailable() {
  try {
    return spawnSync("uvx", ["--version"], { stdio: "ignore" }).status === 0;
  } catch {
    return false;
  }
}

// ---------- config writers ----------

function targets() {
  const home = os.homedir();
  return [
    {
      harness: "claude",
      file: path.join(home, ".claude.json"),
      section: "mcpServers",
      builder: claudeCopilot,
      skipGithub: !ghPat,
      skipZai: !zaiKey,
      githubName: "github",
    },
    {
      harness: "copilot",
      file: path.join(home, ".copilot", "mcp-config.json"),
      section: "mcpServers",
      builder: claudeCopilot,
      skipGithub: !ghPat,
      skipZai: !zaiKey,
      // Naming it github-mcp-server replaces Copilot CLI's built-in read-only server.
      githubName: "github-mcp-server",
    },
    {
      harness: "opencode",
      file: path.join(home, ".config", "opencode", "opencode.json"),
      section: "mcp",
      builder: opencode,
      skipGithub: false, // uses {env:...} refs; nothing secret on disk
      skipZai: false,
      githubName: "github",
    },
    {
      harness: "nanocoder",
      file: path.join(home, ".config", "nanocoder", ".mcp.json"),
      section: "mcpServers",
      builder: nanocoder,
      skipGithub: false, // uses ${VAR} refs
      skipZai: false,
      githubName: "github",
    },
    {
      harness: "gemini",
      file: path.join(home, ".gemini", "settings.json"),
      section: "mcpServers",
      builder: gemini,
      skipGithub: !ghPat,
      skipZai: !zaiKey,
      githubName: "github",
    },
    {
      harness: "cursor",
      file: path.join(home, ".cursor", "mcp.json"),
      section: "mcpServers",
      builder: cursor,
      skipGithub: !ghPat,
      skipZai: !zaiKey,
      githubName: "github",
    },
  ];
}

const zaiRemotes = [
  ["zai-web-search", ZAI_SEARCH_URL],
  ["zai-web-reader", ZAI_READER_URL],
  ["zai-zread", ZAI_ZREAD_URL],
];

const uvxOk = uvxAvailable();
if (!uvxOk) log("uvx not found; git server entries will be pruned");

for (const t of targets()) {
  const existing = readJson(t.file);
  const before = JSON.stringify(existing);
  const written = [];
  const pruned = [];

  if (t.skipGithub) {
    if (prune(existing, t.section, t.githubName)) pruned.push(t.githubName);
  } else {
    upsert(existing, t.section, t.githubName, t.builder.github(ghPat));
    written.push(t.githubName);
  }
  if (t.skipZai) {
    for (const name of ZAI_MANAGED_NAMES) {
      if (prune(existing, t.section, name)) pruned.push(name);
    }
  } else {
    upsert(existing, t.section, "zai-vision", t.builder.vision(zaiKey));
    written.push("zai-vision");
    for (const [name, url] of zaiRemotes) {
      upsert(existing, t.section, name, t.builder.remote(url));
      written.push(name);
    }
  }

  // Keyless Microsoft Learn docs server: always managed.
  upsert(existing, t.section, "microsoft-docs", t.builder.docs());
  written.push("microsoft-docs");
  // Context7 library docs: keyless works; a CONTEXT7_API_KEY (when present)
  // only adds a Bearer header for higher rate limits.
  upsert(existing, t.section, "context7", t.builder.context7());
  written.push("context7");
  // Local git operations: only managed while uvx is available; pruned when
  // the tool disappears so harnesses never show a permanently failed server.
  if (uvxOk) {
    upsert(existing, t.section, "git", t.builder.git());
    written.push("git");
  } else if (prune(existing, t.section, "git")) {
    pruned.push("git");
  }

  if (JSON.stringify(existing) === before) {
    log(`${t.harness}: already up to date (${t.file})`);
    continue;
  }
  writeJson(t.file, existing);
  const parts = [];
  if (written.length) parts.push(`wrote ${written.join(", ")}`);
  if (pruned.length) parts.push(`pruned ${pruned.join(", ")}`);
  log(`${t.harness}: ${parts.join("; ")} -> ${t.file}`);
}
