---
name: address-pr-feedback
description: Fetch and resolve all PR feedback (human and bot) mechanically - pull reviews and inline comments via gh, verify every claim against the code, sweep for the failure class, fix red-green, and reply with a finding-to-fix map.
metadata:
  category: core
---

# Address PR Feedback

Feedback left on a PR is a work queue, not a verdict. Machines and humans
review different commits, hallucinate, and misread diffs — every finding is
a hypothesis until verified against the code.

## When to Use

- A review, comment, or bot report (e.g. Cursor Bugbot, CodeRabbit) lands on
  an open PR.
- GOAL-driven sessions instruct "address all reviewer feedback before
  moving on".
- Re-review rounds after pushing fixes.

## When NOT to Use

- Feedback on merged/closed PRs — open an issue referencing it instead.
- Style preferences that conflict with repo conventions — note the conflict
  on the PR and follow the repo.

## The loop

### 1. Fetch everything mechanically

Do not rely on the UI or memory; pull the full set:

```bash
gh pr view <N> --json reviews,comments        # review summaries + issue comments
gh api repos/<org>/<repo>/pulls/<N>/comments  # inline review comments (file:line)
gh pr view <N> --json statusCheckRollup       # check conclusions
```

Note the reviewed commit SHA: findings reference the diff at that commit,
which may predate your latest push. Re-check each finding against the
current tree before acting.

### 2. Triage

Order by impact (correctness > usability > performance), and weigh source:
recent human review > bot findings > stale comments. For each finding,
classify like a pre-landing review:

- **AUTO-FIX** — mechanical, no judgment (missing brackets, silent failure
  in a diagnostic tool, missing test). Fix immediately.
- **ASK** — tradeoffs or behavior changes. Present the recommendation on
  the PR and wait, unless a goal explicitly authorizes the change.
- **REJECT** — factually wrong (the code already handles it, or the cited
  line does not do what the finding claims). Say so on the PR with
  evidence; never fix to appease.

### 3. Verify before believing

For each finding: open the cited file:line, reproduce the failure if cheap
(a test, a command). A wrong-but-plausible finding still teaches something —
if the reviewer misread, the code is probably not clear enough; consider a
comment or rename.

Reproduction rules that have caught real bugs:

- **Execute the cited command on the CI-pinned toolchain** (pin with a
  `global.json` if the local SDK differs), not whatever is newest locally —
  parser shapes and semantics drift between SDKs.
- **Verify effects, not exit codes.** A command can parse, exit 0, print
  success, and silently skip its input (real case: `dotnet nuget push
  a.nupkg a.snupkg` accepted both paths, pushed one, skipped the symbol
  package without a warning). Observe what actually happened — files
  written, requests served, artifacts produced.
- **Re-execute any "empirically confirmed" claim yourself** before
  trusting it — bot findings and sub-agent verdicts alike are hypotheses;
  neither has executed anything. If two sources disagree, run the
  experiment; one cheap local run outranks both.

### 4. Sweep for the class

Findings are instances of failure classes. After verifying one, grep the
codebase for siblings:

| Reported instance | Sweep for |
| --- | --- |
| Script aborts on first lint error | Other `Write-Error`/`throw` inside loops in tooling |
| Bad URI for one host shape | Every string-interpolated URI/site that takes external input |
| Helper mangles input type | Other narrowly-typed params fed arrays by callers |
| Hook blesses a file CI rejects | Every gate's hook selector vs its linter's accepted inputs vs CI's trigger (see [add-quality-gate](../add-quality-gate/SKILL.md)) |
| Generator/switch breaks after enum renumbering | Every numeric enum construction (`(Enum)(byte % N)`), ordinal loop, and sentinel-guard added in the same change — test/fuzz generators and perf harnesses included |
| Repro artifact lands in the repo tree | Tracked files matching fuzz/crash/seed patterns; artifacts belong in gitignored persistence dirs (`.fuzz/`), never the repo root |
| Workflow `run:` command is wrong (fails or silently no-ops) | Every `run:` line in tag-only/schedule-only workflows — those jobs never self-verify in CI, so PR review is their only execution check; run each new command locally on the CI-pinned toolchain and confirm its effect |
| Struct `GetHashCode` throws on a `default` instance | Every `Equals`/`GetHashCode` on structs with nullable members — `default(T)` is legal; hash via `System.HashCode` or `?.GetHashCode() ?? 0` |
| Twin resolvers drift (shell + Node both implement one rule) | Every pair of implementations of the same resolution/protocol — precedence, candidate lists, and refusals must stay in lockstep, pinned by a parity test on both sides (real case: `paths.sh` vs `write-mcp-configs.mjs` OpenCode path order) |
| Lock takeover bound exceeds one waiter timeout | Every lock: takeover/staleness bound must be ≤ one waiter's wait, or a crashed holder wedges every waiter for the whole window; prefer owner-liveness takeover (PID in the token) over pure age |
| Default struct from an ignored typed `TryDecode` reaches authoritative logic | Every re-decode of a mapper-validated frame with a *different* typed decoder — the mapper's decoder can be looser (it ignored members the typed decoder validates), so "the fact validated it" is false for the re-decode; guard the result and refuse the frame (real case: `Reconnected` mapped via the join decoder, malformed `sender_watermarks` → NRE in the gate feed) |
| Per-connection state reused after an automatic reconnect | Every "swap/latch once" guard — its latch must reset at the terminal boundary, or the fresh connection inherits dead counters and fails its own monotonicity checks (real case: same-version `ProtocolInfo` echo absorbed after reconnect; the stale engine's `interval_ms` pinned the fresh room into quarantine) |
| Nullable value type used as a proceed/fail signal | Every `return default;` in a method returning `T?` — it yields `null` (fail), not "empty but valid"; use the Try/out idiom (`bool Try...(out T verdict)`) so proceed and fail cannot collide (real case: a proceed path returned `default(GateVerdict?)`, and every routed fact surfaced a violation) |
| Wire-declared length/count checked with bounded-int arithmetic | Every length prefix read from external bytes — compare against the remaining span in `long` before any slice or cursor advance; a declared `0x7FFFFFFF` wraps `_offset + length` negative, passes the check, and faults the loop on a tiny frame (real case: the M6.4 MessagePack scanner's bin32/str32/map32 lengths) |
| State mutated before the validation chain completes | Every multi-step negotiator/resolver: resolve into locals and assign fields only after the last check passes — a write-then-reject path leaves the settled state clobbered (real case: `ResolveFormatNegotiation` reset the negotiated encoding to JSON before the canonical-list check refused a version-changing re-echo) |
| Engine-gated source has a type error only the editor would catch | Every line of SDK-gated adapter code — no compiler in CI sees the real SDK. Keep the adapter lint's shape-stub compile lane (`BridgeCompile`) green, verify every external-SDK member name against the engine's actual source or docs (never memory), and keep the pure-core API floor honest: `netstandard2.1` lacks `Environment.TickCount64` and friends even where dotnet builds pass (real case: `joinCode = await AwaitHook(...)` awaited a void `Task` in the NGO bridge; `TickCount64` rode in with it and both were invisible until the stub lane landed) |
| A benign server broadcast variant treated as fatal in a drain loop | Every unconditional fatal branch on an event kind — classify the payload's variants first (grant echo vs. genuine migration). The conformance suite is the executable spec of what the server actually broadcasts and in what order; `AuthorityChanged` follows a successful `AuthorityResponse` for the grantee too (real case: the NGO coordinator tore down every healthy host start on the grant echo) |

Fix every sibling in the same change. One-off fixes guarantee the reviewer
finds the sibling next round.

### 5. Red-green the fix

Write the failing test that reproduces the reported issue (and one for a
sibling), watch it fail, then fix, then watch it pass. A finding closed
without a test will regress. See [create-test](../create-test/SKILL.md).

### 6. Reply with the map

One concise PR comment mapping each finding to its disposition — fixed
(with file/test), rejected (with evidence), or pending (with the issue
link). Reviewers should never have to diff the branch to learn what
happened to their feedback.

### 7. Capture the class

If the failure class is new knowledge (not already in a skill), fold it
into a skill or rule via the
[reflect-improve](../reflect-improve/SKILL.md) loop before closing out.

## Related Skills

- [reflect-improve](../reflect-improve/SKILL.md) - knowledge capture gate
- [create-test](../create-test/SKILL.md) - red-green regression tests
- [manage-skills](../manage-skills/SKILL.md) - authoring/updating skills
- [powershell-tooling](../powershell-tooling/SKILL.md) - recurring PS failure classes in this repo's tooling
- [add-quality-gate](../add-quality-gate/SKILL.md) - scope contract for gate/hook/CI tooling
