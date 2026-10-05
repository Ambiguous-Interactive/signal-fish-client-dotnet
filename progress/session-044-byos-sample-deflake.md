# Session 044 — BYOS sample (M8.4) + the wire-wait deflake

Date: 2026-10-05
Branch: `byos-sample-m8.4-issue-93`
Closes: #93

## What shipped

**Issue #93** — the teardown test's flake class is gone. The wire waits
polled `transport.Sent*.Count` against a 10 s wall-clock deadline; a
saturated CI runner starved the driver loop once, the deadline expired,
and a healthy session failed. Sweep for the whole class:

- `FakeTransport.WaitSentTextAsync` / `WaitSentBinaryAsync`: the
  event-based wire wait — the fake completes a task the moment the
  driver loop records a frame, so tests park instead of polling.
  Disposal releases unmet waits so a test whose session died mid-wait
  asserts on the real count instead of hanging.
- All 18 count-based wire waits across the three async test files moved
  to the event waits (per-file `WaitForSentTextAsync`/`…Binary…`
  helpers, 60 s anti-hang backstop).
- The remaining readiness polls keep their 1 ms shape but the budget is
  now 60 s in all three files — an anti-hang backstop a runner cannot
  starve out, not a timing assert.
- The teardown test itself is now fully deterministic: the wire wait
  events on the recorded frame, the full-queue park is synchronous, and
  the unblock already rides the completed command queue (no loop
  scheduling between test steps).

**M8.4 Wave 1 generic BYOS sample** — the bring-your-own-stack
template, shipped as a docs page (`docs/adapters/byos.md`, nav under
Unity) rather than an UPM package:

- Host: join → authority → start your listener → publish the endpoint
  via `SendProvideConnectionInfo` (re-published on joins so late
  joiners are independent of server repeat timing).
- Client: join by code → read the authority's `ConnectionInfo` off
  `GameStarting` → echo the room join code over `GameData`.
- The host's connection gate is live roster **plus** a matching echo
  from that player id — the BYOS analogue of the NGO approval, with the
  stack-specific seam named (`playerHint`).
- The join-code echo envelope rides the v2 floor and scans bytes (no
  JSON parser), the same shape as the NGO relay envelope.

## Validation

- `dotnet test`: 876 passed per TFM (net8.0 + net10.0), the three
  converted files re-run green after the sweep.
- lint-conventions, CSharpier clean; pre-commit hooks green.
- Docs: nav entry added; cross-link uses `../webgl.md` (strict-build
  safe). Page is the deliverable — no package, no files.

## Not carried (deliberate)

- #88 (NUnit 5) stays blocked on an FsCheck.NUnit release that allows
  NUnit ≥ 5.0.0; the dependabot ignore is in place.
- #80 (mkdocs markdown extensions) stays "enable on first use" — no
  page needs one yet.
