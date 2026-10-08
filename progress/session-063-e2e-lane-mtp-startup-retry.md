# Session 063 — e2e lane: retry the MTP startup crash too

Date: 2026-10-08
Branch: `e2e-mtp-startup-retry`
PR: (this session)
Issue: #120 (resolves its e2e-lane follow-up)

## Drift check

- origin/main already merged (working tree clean at `41e8544`, #121);
  main fully green (dotnet, Docs, LLM Context). No open or draft PRs.
- Open issues triaged: #122 (watch item, first actions land after the
  2026-10-19 runner migration), #115 (operator-blocked: npm names +
  `NPM_TOKEN`), #80 (dormant by its own text), #120 — this session's
  driver, the one actionable follow-up.
- Upstream check: server `main` is 30 commits past the `v0.10.0` pin,
  all metrics/capacity/internal (Prometheus session records, WASM
  Fortress drills); no wire change, no new tag — the client's 0.10.0
  parity stands, and the encoding-catalogue research (server #772) is
  still at its decision gate.

## Data: the e2e lane's "no observed occurrence" was wrong

#120 recorded the e2e lane as crash-free. A 40-run scan of `e2e.yml`
found one: run 37574311486 (2026-10-07 05:02 UTC, the
`m111-nunit-mtp-runner` PR — the first MTP-era e2e run) failed with
"Zero tests ran", one session-level error, exit 5, duration 2s 788ms,
and no TRX. Same failure family as the dotnet lane's exit-139 crashes:
the test app died at startup before running any test. The original
scan had focused on `dotnet.yml` and main; this run sat on a
since-deleted PR branch. Family total: 2 dotnet-lane hits, 1 e2e-lane
hit in the first ~40 MTP-era runs.

## Why retrying the e2e suite is safe (the idempotence audit)

#120's precondition was verifying scenario idempotence against a
shared server. The audit:

- Every scenario names its game `e2e-dotnet-<8 hex of GUID>` and joins
  through server-issued room codes; sealed-room passwords and the
  join-only probe codes are GUID-derived too. A re-run cannot collide
  with a prior attempt's rooms.
- Rate limits are per player per 60 s window (server
  docs/configuration.md). The keying is not pinned in this repo's SSOT,
  but two facts cover the coarse readings: parallel scenarios already
  share every candidate key (names, appId, IP) today and run green
  (39/40), and under the coarsest keying the window still bounds the
  exposure — a green-shaped attempt takes ~46 s, so a 60 s window holds
  at most two attempts' joins (~80, under the CI-set 100); under the
  documented per-player keying each attempt brings fresh budgets
  anyway.
- No scenario asserts server-global state (no metrics or room-count
  reads); the server is a throwaway in-memory container.

## What shipped

- `scripts/run-e2e.ps1`: the `dotnet test` invocation retries up to
  3 attempts. `::warning::` annotates each non-final attempt naming
  #120, and the last exit code is re-raised on exhaustion, so a
  repeatable failure still fails the lane. The server boot and
  readiness probe stay outside the loop. Keeping the loop in
  PowerShell rather than YAML bash sidesteps the shell-pinning failure
  class session 062 recorded.

## Adversarial review findings applied

- **TestResults is not wiped between attempts.** The first cut copied
  the wipe from `dotnet.yml`, but there it serves the coverage gate
  (stale cobertura), and the crash family leaves no TRX at all (the
  failed e2e run uploaded zero files). Wiping here only destroyed
  genuine-failure diagnostics: an intermittent real bug that greens on
  attempt 2 would leave no artifact evidence. MTP TRX names are unique
  per run, so accumulation is harmless evidence.
- Warning text now says "attempt N failed" — the throw's "Conformance
  suite failed" phrase stays unique for log greps.
- `$maxAttempts` pins the 3 in the loop, the warning gate, and the
  throw text.

## Verification

- Loop-semantics matrix on the exact shape: first-try success -> 1
  call, exit 0, no warnings; flake-then-success -> 2 calls, exit 0,
  warning on attempt 1 only; always-fails -> 3 calls, warnings on
  1-2 only, last exit code re-raised.
- End-to-end failure path through the real script: a local fake HTTP
  listener satisfied the readiness probe while the WS endpoint was
  dead; every scenario failed fast, attempts 1-3 ran with warnings on
  1-2, and the script threw "Conformance suite failed (exit 2) after
  3 attempts." — the last exit code preserved.
- Script parses clean (Language.Parser); hook self-tests
  (`test-pre-commit`, `test-install-hooks`) pass. CI re-proves the
  happy path: attempt 1 green -> exit 0, behavior unchanged.
- Timeout budget: a green "Run conformance suite" step is ~46 s, so
  three green-shaped attempts fit the 15-minute job easily; a degraded
  attempt stretches (tests wait out 10 s event timeouts) and the
  margin narrows, but 3 x worst-case still fits.

## Deliberately not done

- No retry classification (retry only on startup crashes): MTP's exit
  codes do not separate the crash family (139 on the dotnet lane, 5
  here) from ordinary failures, and parsing output for "Zero tests
  ran" is fragile. A repeatable failure staying red after at most 3
  attempts is the trade `dotnet.yml` already made.
- No readiness re-probe between attempts: a mid-loop server death is
  not the flake family, and attempts against a dead server fail fast
  to the same red.
- No `e2e.yml` change: the script is the single seam for CI and local
  runs.
- #120's upstream-report follow-up stays open — it fires only if the
  crash recurs despite retries. No recurrence observed on main since
  #121: every run green, and the dotnet-lane retry has not fired yet.
