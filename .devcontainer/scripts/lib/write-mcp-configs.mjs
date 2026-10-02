#!/usr/bin/env node
// Regenerate the per-harness MCP server configs inside the container.
//
// Idempotent: merges our managed server entries into each harness's config
// file, preserving all other user content. Prunes managed entries whose
// matching credential has disappeared (secret hygiene). Skips token-gated
// servers when the matching key is absent. Fail-closed: malformed or
// ambiguous configs, symlinked paths, and write failures exit nonzero and
// roll back files this run already changed. Diagnostics on stderr.
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
import { randomUUID } from "node:crypto";
import { spawnSync } from "node:child_process";
import { createRequire } from "node:module";

const npmRoot = spawnSync("npm", ["root", "-g"], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] });
if (npmRoot.status !== 0) throw new Error("cannot resolve the global npm module root");
const { applyEdits, modify, parse: parseJsonc } = createRequire(import.meta.url)(
  path.join(npmRoot.stdout.trim(), "jsonc-parser"),
);

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

const OPENCODE_SCHEMA = "https://opencode.ai/config.json";
const JSONC_FORMATTING = { insertSpaces: true, tabSize: 2, eol: "\n" };
const MANAGED_SERVER_NAMES = [
  "github",
  "zai-vision",
  "zai-web-search",
  "zai-web-reader",
  "zai-zread",
  "microsoft-docs",
  "context7",
  "git",
];
const ZAI_MANAGED_NAMES = MANAGED_SERVER_NAMES.filter((name) => name.startsWith("zai-"));

function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function requireObject(value, location) {
  if (!isObject(value)) throw new TypeError(`${location} must be an object`);
  return value;
}

function absolutePath(value) {
  const text = String(value || "");
  if (text === "~") return path.resolve(env.HOME || os.homedir());
  if (text.startsWith("~/")) return path.resolve(env.HOME || os.homedir(), text.slice(2));
  return path.resolve(text);
}

function assertNoSymlinkPath(file) {
  let current = path.resolve(file);
  while (true) {
    try {
      const stat = fs.lstatSync(current);
      if (stat.isSymbolicLink()) throw new Error(`refusing symlinked config path ${current}`);
    } catch (error) {
      if (error?.code !== "ENOENT") throw error;
    }
    const parent = path.dirname(current);
    if (parent === current) return;
    current = parent;
  }
}

function ensureSafeParent(file) {
  assertNoSymlinkPath(file);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  assertNoSymlinkPath(file);
}

function readConfig(file, jsonc = false) {
  assertNoSymlinkPath(file);
  if (!fs.existsSync(file)) return { text: jsonc ? "{}\n" : "{}", value: {} };
  if (fs.lstatSync(file).isSymbolicLink()) {
    throw new Error(`refusing to manage symlinked config ${file}`);
  }
  const text = fs.readFileSync(file, "utf8");
  try {
    const errors = [];
    const value = jsonc
      ? parseJsonc(text, errors, { allowTrailingComma: true, disallowComments: false })
      : JSON.parse(text);
    if (errors.length) throw new TypeError(errors.map((error) => error.errorText).join("; "));
    return { text, value: requireObject(value, file) };
  } catch (error) {
    throw new Error(`cannot parse ${file}`, { cause: error });
  }
}

function applyJsoncChanges(text, changes) {
  return changes.reduce(
    (current, [jsonPath, value]) =>
      applyEdits(
        current,
        modify(current, jsonPath, value, { formattingOptions: JSONC_FORMATTING }),
      ),
    text,
  );
}

function updateOpencodeJsonc(text, config) {
  const changes = [[["$schema"], OPENCODE_SCHEMA]];
  // jsonc-parser cannot delete a path that is absent (notably when a valid
  // JSONC file has no `mcp` object yet). Only remove legacy direct entries
  // when they are actually present; nested entries are written below.
  if (isObject(config.mcp)) {
    for (const name of MANAGED_SERVER_NAMES) {
      if (Object.prototype.hasOwnProperty.call(config.mcp, name)) {
        changes.push([["mcp", name], undefined]);
      }
    }
  }
  changes.push(
    ...MANAGED_SERVER_NAMES.map((name) => [
      ["mcp", "servers", name],
      config.mcp?.servers?.[name],
    ]),
  );
  return applyJsoncChanges(text, changes);
}

function writeFileAtomic(file, content) {
  ensureSafeParent(file);
  if (fs.existsSync(file) && fs.lstatSync(file).isSymbolicLink()) {
    throw new Error(`refusing to replace symlinked config ${file}`);
  }
  const temporary = path.join(path.dirname(file), `.${path.basename(file)}.${process.pid}.${randomUUID()}.tmp`);
  let descriptor;
  try {
    descriptor = fs.openSync(temporary, "wx", 0o600);
    fs.writeFileSync(descriptor, content);
    fs.fsyncSync(descriptor);
    fs.closeSync(descriptor);
    descriptor = undefined;
    fs.renameSync(temporary, file);
    const stat = fs.lstatSync(file);
    if (stat.isSymbolicLink()) throw new Error(`config became a symlink during write ${file}`);
    fs.chmodSync(file, 0o600);
  } finally {
    if (descriptor !== undefined) fs.closeSync(descriptor);
    if (fs.existsSync(temporary)) fs.unlinkSync(temporary);
  }
}

function sleepSync(milliseconds) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds);
}

function withMcpLock(callback) {
  // Shell-managed syncs (post-start, ai-backends) hold the same lock via
  // paths.sh before invoking this script; the env marker avoids deadlock.
  // Direct invocations serialize with them through an identical mkdir lock:
  // <state>/mcp-config.lock.d + owner token + dead-owner/age takeover. The
  // owner token leads with the writer PID in both implementations
  // (paths.sh: PID-RANDOM-TIME, here: PID:UUID), so a crashed writer's lock
  // is stolen within one wait instead of the 10 minute age bound; tokens
  // without a parseable PID fall back to the age bound.
  if (env.DEVCONTAINER_CONFIG_LOCK_HELD === "1") return callback();
  const home = os.homedir();
  const lockRoot = path.join(home, ".cache", "signal-fish-devcontainer");
  const lockDir = path.join(lockRoot, "mcp-config.lock.d");
  fs.mkdirSync(lockRoot, { recursive: true });
  assertNoSymlinkPath(lockDir);

  const token = `${process.pid}:${randomUUID()}`;
  let acquired = false;
  for (let attempt = 0; attempt < 600 && !acquired; attempt += 1) {
    try {
      fs.mkdirSync(lockDir, { mode: 0o700 });
      fs.writeFileSync(path.join(lockDir, "owner"), `${token}\n`, { mode: 0o600 });
      acquired = true;
    } catch (error) {
      if (error?.code !== "EEXIST") throw error;
      const lockStat = fs.lstatSync(lockDir);
      if (lockStat.isSymbolicLink()) throw new Error(`refusing symlinked MCP lock ${lockDir}`);
      if (lockOwnerIsDead(lockDir)) {
        fs.rmSync(lockDir, { recursive: true, force: true });
        continue;
      }
      const age = Date.now() - lockStat.mtimeMs;
      if (age > 10 * 60 * 1000) {
        fs.rmSync(lockDir, { recursive: true, force: true });
      } else {
        sleepSync(100);
      }
    }
  }
  if (!acquired) throw new Error(`timed out waiting for MCP config lock ${lockDir}`);

  try {
    return callback();
  } finally {
    try {
      const owner = fs.readFileSync(path.join(lockDir, "owner"), "utf8").trim();
      if (owner === token) {
        fs.unlinkSync(path.join(lockDir, "owner"));
        fs.rmdirSync(lockDir);
      }
    } catch (error) {
      if (error?.code !== "ENOENT") log(`could not release MCP lock: ${error}`);
    }
  }
}

// True when the lock's owner token names a PID that is no longer running.
// process.kill(pid, 0) throws ESRCH for a dead PID; EPERM means a live
// process owned by another user, which must be treated as alive. A missing
// or unparseable token returns false so the age bound stays in charge.
function lockOwnerIsDead(lockDir) {
  let owner;
  try {
    owner = fs.readFileSync(path.join(lockDir, "owner"), "utf8").trim();
  } catch {
    return false;
  }
  const pid = Number.parseInt(owner.split(/[-:]/, 1)[0], 10);
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return false;
  } catch (error) {
    return error?.code === "ESRCH";
  }
}

function snapshotFile(file) {
  assertNoSymlinkPath(file);
  if (!fs.existsSync(file)) return { file, exists: false };
  const stat = fs.lstatSync(file);
  if (!stat.isFile()) throw new Error(`managed config is not a regular file: ${file}`);
  return {
    file,
    exists: true,
    content: fs.readFileSync(file),
    mode: stat.mode & 0o7777,
    dev: stat.dev,
    ino: stat.ino,
  };
}

function sameFileState(snapshot, expected) {
  if (!fs.existsSync(expected.file)) return !expected.exists;
  const stat = fs.lstatSync(expected.file);
  if (!stat.isFile() || stat.isSymbolicLink()) return false;
  if (expected.exists && (stat.mode & 0o7777) !== expected.mode) return false;
  return fs.readFileSync(expected.file).equals(expected.content);
}

function restoreFile(snapshot, expected) {
  if (!sameFileState(snapshot, expected)) {
    throw new Error(`refusing rollback because ${snapshot.file} changed outside this writer`);
  }
  if (!snapshot.exists) {
    fs.rmSync(snapshot.file, { force: true });
    return;
  }
  writeFileAtomic(snapshot.file, snapshot.content);
  fs.chmodSync(snapshot.file, snapshot.mode);
}

function parentAt(obj, parts, location) {
  let current = obj;
  for (const part of parts) {
    if (!Object.prototype.hasOwnProperty.call(current, part)) return undefined;
    current = requireObject(current[part], `${location}.${parts.join(".")}`);
  }
  return current;
}

function upsert(obj, section, name, entry) {
  let current = obj;
  for (const part of section.split(".")) {
    if (!Object.prototype.hasOwnProperty.call(current, part)) current[part] = {};
    current = requireObject(current[part], `config path ${section}.${part}`);
  }
  current[name] = entry;
}

function prune(obj, section, name) {
  const parts = section.split(".");
  const parent = parentAt(obj, parts, `config path ${section}`);
  if (parent && Object.prototype.hasOwnProperty.call(parent, name)) {
    delete parent[name];
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
    disabled: !ghPat || undefined,
    oauth: false,
    headers: { Authorization: "Bearer {env:GITHUB_PERSONAL_ACCESS_TOKEN}" },
  }),
  vision: () => ({
    type: "local",
    command: ["zai-mcp-server"],
    disabled: !zaiKey || undefined,
    environment: { Z_AI_API_KEY: "{env:Z_AI_API_KEY}", Z_AI_MODE: zaiMode },
  }),
  remote: (url) => ({
    type: "remote",
    url,
    disabled: !zaiKey || undefined,
    oauth: false,
    headers: { Authorization: "Bearer {env:Z_AI_API_KEY}" },
  }),
  docs: () => ({ type: "remote", url: MS_DOCS_MCP_URL, oauth: false }),
  context7: () => ({
    type: "remote",
    url: CONTEXT7_MCP_URL,
    oauth: false,
    ...(context7Key ? { headers: { Authorization: "Bearer {env:CONTEXT7_API_KEY}" } } : {}),
  }),
  git: () => ({ type: "local", command: ["uvx", "mcp-server-git"] }),
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

function assertAuthorization(entry, label, secret) {
  const auth = entry?.headers?.Authorization;
  if (!auth || typeof auth !== "string") throw new Error(`${label}: missing Authorization header`);
  if (secret && !auth.includes(secret) && !auth.includes("{env:") && !auth.includes("${")) {
    throw new Error(`${label}: Authorization does not carry the credential or an environment reference`);
  }
}

// ---------- config writers ----------

function opencodeConfigTarget() {
  // OPENCODE_CONFIG_DIR is searched after the project tree and therefore wins
  // over OPENCODE_CONFIG for effective managed settings. An explicit file is
  // still honored when no custom directory is supplied.
  const customDir = env.OPENCODE_CONFIG_DIR;
  if (customDir) {
    const dir = absolutePath(customDir);
    const candidates = ["opencode.jsonc", "opencode.json", "config.json"].map((name) => path.join(dir, name));
    const existing = candidates.filter((file) => fs.existsSync(file));
    if (existing.length > 1) {
      throw new Error(`OpenCode has multiple config files in ${dir}; keep one custom config file`);
    }
    const file = existing[0] || candidates[1];
    return { file, jsonc: file.endsWith(".jsonc"), source: "OPENCODE_CONFIG_DIR" };
  }
  if (env.OPENCODE_CONFIG) {
    const file = absolutePath(env.OPENCODE_CONFIG);
    return { file, jsonc: file.endsWith(".jsonc"), source: "OPENCODE_CONFIG" };
  }
  const xdg = env.XDG_CONFIG_HOME || path.join(env.HOME || os.homedir(), ".config");
  const dir = absolutePath(path.join(xdg, "opencode"));
  const candidates = ["opencode.jsonc", "opencode.json", "config.json"].map((name) => path.join(dir, name));
  const existing = candidates.filter((file) => fs.existsSync(file));
  if (existing.length > 1) {
    throw new Error(`OpenCode has multiple global config files in ${dir}; keep one global config file`);
  }
  const file = existing[0] || candidates[1];
  return { file, jsonc: file.endsWith(".jsonc"), source: "XDG_CONFIG_HOME" };
}

function validateInlineOpencodeConfig() {
  if (!env.OPENCODE_CONFIG_CONTENT) return;
  const errors = [];
  let value;
  try {
    value = parseJsonc(env.OPENCODE_CONFIG_CONTENT, errors, { allowTrailingComma: true, disallowComments: false });
  } catch (error) {
    throw new Error(`OPENCODE_CONFIG_CONTENT is not valid JSONC: ${error.message}`);
  }
  if (errors.length || !isObject(value)) throw new Error("OPENCODE_CONFIG_CONTENT must be a JSON object");
  const servers = value.mcp?.servers;
  if (isObject(servers)) {
    const conflicts = MANAGED_SERVER_NAMES.filter((name) => Object.prototype.hasOwnProperty.call(servers, name));
    if (conflicts.length) {
      throw new Error(`OPENCODE_CONFIG_CONTENT overrides managed MCP servers: ${conflicts.join(", ")}`);
    }
  }
}

function targets() {
  const home = os.homedir();
  const opencodeTarget = opencodeConfigTarget();
  log(`OpenCode config target (${opencodeTarget.source}): ${opencodeTarget.file}`);
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
      file: opencodeTarget.file,
      jsonc: opencodeTarget.jsonc,
      section: "mcp.servers",
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

function prepareTarget(t) {
  const loaded = readConfig(t.file, t.jsonc);
  const existing = loaded.value;
  const before = JSON.stringify(existing);
  if (t.harness === "opencode") {
    existing.$schema = OPENCODE_SCHEMA;
    for (const name of MANAGED_SERVER_NAMES) {
      if (existing.mcp && Object.prototype.hasOwnProperty.call(existing.mcp, name)) {
        delete existing.mcp[name];
      }
    }
  }
  const written = [];
  const pruned = [];

  if (t.skipGithub) {
    if (prune(existing, t.section, t.githubName)) pruned.push(t.githubName);
  } else {
    const entry = t.builder.github(ghPat);
    assertAuthorization(entry, `${t.harness}: github`, ghPat);
    upsert(existing, t.section, t.githubName, entry);
    written.push(t.githubName);
  }
  if (t.skipZai) {
    for (const name of ZAI_MANAGED_NAMES) {
      if (prune(existing, t.section, name)) pruned.push(name);
    }
  } else {
    const vision = t.builder.vision(zaiKey);
    upsert(existing, t.section, "zai-vision", vision);
    written.push("zai-vision");
    for (const [name, url] of zaiRemotes) {
      const entry = t.builder.remote(url, zaiKey);
      // Key-embedding builders interpolate the key into an Authorization
      // header; a call forgetting the argument would persist "Bearer
      // undefined" and fail auth even with a valid key. Ref-embedding
      // builders ({env:...}/${VAR}) omit the header field instead.
      assertAuthorization(entry, `${t.harness}: ${name}`, zaiKey);
      upsert(existing, t.section, name, entry);
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

  const semanticUnchanged = JSON.stringify(existing) === before;
  const output = t.jsonc
    ? updateOpencodeJsonc(loaded.text, existing)
    : JSON.stringify(existing, null, 2) + "\n";
  if (t.jsonc) {
    const errors = [];
    const parsed = parseJsonc(output, errors, { allowTrailingComma: true, disallowComments: false });
    if (errors.length || !isObject(parsed)) {
      throw new Error(`generated invalid JSONC for ${t.file}`);
    }
  }
  return {
    target: t,
    output,
    unchanged: semanticUnchanged && (!t.jsonc || output === loaded.text),
    written,
    pruned,
  };
}

function run() {
  validateInlineOpencodeConfig();
  // Prepare and validate every target before replacing any file. A malformed or
  // symlinked config in a later harness must not leave earlier harnesses updated
  // with a different credential set.
  const prepared = targets().map(prepareTarget);
  const snapshots = prepared.map((item) => snapshotFile(item.target.file));
  const applied = [];

  try {
    for (let index = 0; index < prepared.length; index += 1) {
      const item = prepared[index];
      const snapshot = snapshots[index];
      const t = item.target;
      if (item.unchanged) {
        assertNoSymlinkPath(t.file);
        if (fs.existsSync(t.file)) fs.chmodSync(t.file, 0o600);
        log(`${t.harness}: already up to date (${t.file})`);
        continue;
      }
      const output = t.jsonc && !item.output.endsWith("\n") ? `${item.output}\n` : item.output;
      const expected = {
        file: t.file,
        exists: true,
        content: Buffer.from(output),
        mode: 0o600,
      };
      applied.push({ snapshot, expected });
      writeFileAtomic(t.file, output);
      const parts = [];
      if (item.written.length) parts.push(`wrote ${item.written.join(", ")}`);
      if (item.pruned.length) parts.push(`pruned ${item.pruned.join(", ")}`);
      log(`${t.harness}: ${parts.join("; ")} -> ${t.file}`);
    }
  } catch (error) {
    // Best-effort rollback keeps a failed multi-file sync from exposing a mixed
    // credential set. The lock prevents another managed writer from changing a
    // file underneath us; the state check also refuses to clobber an unrelated
    // external edit made while this process was running.
    for (const { snapshot, expected } of applied.toReversed()) {
      try {
        restoreFile(snapshot, expected);
      } catch (rollbackError) {
        log(`rollback failed for ${snapshot.file}: ${rollbackError}`);
      }
    }
    throw error;
  }
}

withMcpLock(run);
