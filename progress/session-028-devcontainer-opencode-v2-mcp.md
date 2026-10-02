# Session 028 — Devcontainer OpenCode v2 and MCP migration

Date: 2026-09-24. Scope: migrate the devcontainer-managed OpenCode installation
from the V1 npm package to V2, convert its MCP configuration to the native V2
shape, adopt Code Mode, and prove the result on the container lifecycle path.

## Starting state

- OpenCode `1.18.32` is installed from `opencode-ai@latest` on ARM64 through a
  custom fallback that separately resolves the native package.
- The generated OpenCode MCP surface uses the V1 flat `mcp` map, `enabled`, and
  no Code Mode policy.
- Existing persistent OpenCode sessions/auth will be reset at rollout; the
  migration does not preserve V1 data.
- OpenCode v2 and the CLI refresh policy track `latest`; CI must expose the
  resolved major version and detect config, architecture, and MCP regressions.
- `.env.local` is ignored but currently mode `0777`; credentials must be rotated
  and the file restricted before rollout.

## Delivered

- Replaced the legacy `opencode-ai` installer with the official V2 npm
  package (`@opencode/cli`), a product-prefix-tolerant V2 probe, visible npm
  failure diagnostics, and a post-start repair path for an interrupted create.
- Migrated generated OpenCode config to native V2 `mcp.servers`, `disabled`,
  environment references, Code Mode defaults, and explicit OAuth policy for
  keyless Microsoft Learn. The writer is fail-closed, atomic, JSONC-aware,
  preserves unrelated settings/servers, and rejects ambiguous `.json`/`.jsonc`
  global files.
- Added state-aware OpenCode service restart after credential/config changes,
  `waitFor: postStartCommand`, the official `sst-dev.opencode-v2` VS Code
  extension, and a versioned `opencode-v2-data` volume so the existing V1
  database is not mutated by V2.
- Replaced the mismatched ARM64 PowerShell payload with pinned, SHA-256-verified
  official PowerShell 7.6.6 archives for ARM64 and x64.
- Added red/green coverage for the version predicate, config writer, MCP probe,
  JSONC preservation, credential pruning, service behavior, and architecture.

## Verification

- RED: the supplied log's actionable failure is at lines 7782/7786/7792;
  reproducing the old predicate on `opencode v2.0.16` returned 1. The next
  red state (`readJson is not defined`) was reproduced before the writer fix.
- GREEN: ARM64 image build completed; nested `pwsh` returned `nested-ok`; a
  running OpenCode service changed PID after a credential/config sync; the
  disposable full self-test passed 167/167 checks. The direct Dockerfile smoke
  skips only `gh` because the declared Dev Container feature supplies it; the
  feature-enabled lifecycle path checks it normally. Follow-up red/green tests
  cover JSONC without `mcp`, multi-target preflight/rollback, env allowlisting,
  TOML escaping/symlink refusal, isolated self-test HOME, and the V2 cold-start
  MCP-list retry.
- GREEN: Dev Containers CLI 0.89 read the JSONC configuration successfully;
  `opencode debug config`, shellcheck, Node syntax, markdownlint, convention
  lints, and the LLM instruction lint passed. A real disposable Dev Containers
  `up` with the declared GitHub feature completed `onCreateCommand` and
  `postStartCommand`; the resulting container reported `opencode v2.0.16`,
  `gh 2.101.0`, nested PowerShell `nested-ok`, and a mode-600 MCP config.
  The local ARM-only BuildKit cannot cross-emulate amd64; native x86_64 CI
  remains the final architecture check. The pinned x64 PowerShell archive was
  independently checksum/ELF verified (`e_machine=62`); only Docker execution
  of the full x64 image awaits the native CI runner.

## Review loop

- Root cause was a false-negative post-install assertion, not an npm/ARM64
  download failure. Secondary blockers were an undefined renamed writer
  helper and a base-image PowerShell payload mismatch.
- Durable procedure is captured in the devcontainer README and the dated
  improvement-log entry. Rebuild the existing VS Code container once so the
  failed lifecycle marker is rerun; rotate any `.env.local` credential that
  has been readable by another user.

## Follow-up hardening (same day, second review)

A second review found nine unresolved failure classes; each was fixed
red-green with disposable tests:

- Credential source semantics (`env.sh`): a malformed or alias-conflicting
  later source now CLEARS the credential instead of resurrecting the
  inherited value; blank mentions are "no override"; canonical
  `Z_AI_API_KEY`/`GITHUB_PERSONAL_ACCESS_TOKEN` beat aliases within a source;
  the parser now matches `ai-backends.sh` (inline comments, unterminated
  quotes). 17 disposable tests cover every rule.
- Effective tool paths: shared `paths.sh` resolvers (`CODEX_HOME`, OpenCode
  `OPENCODE_CONFIG_DIR` > `OPENCODE_CONFIG` > `XDG_CONFIG_HOME` > default)
  wired into both MCP writers, readiness probes, and the post-start
  fingerprint; `OPENCODE_CONFIG_CONTENT` managed-name overrides are rejected
  fail-closed; `ai-backends.sh` now supports `ZHIPU_API_KEY` and warns when
  `Z_AI_MODE=ZHIPU` conflicts with its `api.z.ai` endpoints.
- Concurrency: one mkdir-token config lock shared by the Node writer, both
  Codex TOML writers, and `ai-backends.sh`; serialized npm install
  transaction (single lock, readiness-gated stamp, old stamp invalidated
  first, future-dated stamps stale); rollback refuses to clobber files
  changed outside the transaction; parent-symlink checks before and after
  `mkdir`.
- Lifecycle propagation: failed OpenCode restart or exhausted MCP wait fails
  `post-start.sh` and leaves `opencode-restart.pending` for the next start;
  `mcp list` gating now requires every expected ENABLED server name;
  core-CLI and AI-backend repairs are independent; `post-create.sh` asserts
  final readiness; CI `runCmd` runs under `set -euo pipefail`; Dockerfile
  asserts `flock` exists; self-test setup runs through `must_run`.
- Self-test hermeticity: `HOME` + `USERPROFILE` + XDG isolation, `.env` and
  `.env.local` backup/restore, AI-backend launchers installed into a
  disposable bin, exact-value launcher key assertions, CRLF and
  `CODEX_HOME`/override-path regressions, concurrent-writer serialization,
  all-or-nothing sync, and stubbed service restart/failure propagation.

Local verification: 17 env semantics tests + 28 Linux writer tests pass in
Debian bash 5.2 / Node 22 containers; ShellCheck warnings cleared; file-size
and LLM lints pass. The rebuilt ARM64 image passed the complete in-container
lifecycle (post-create → post-start → self-test): 211/211 checks green.
