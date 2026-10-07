# Session 059 — adopt the server's pinned game-data-format downgrade notice

Date: 2026-10-07
Branch: `server-downgrade-notice`
PR: (this session)

## Drift check

- origin/main already merged (working tree was clean at `31980fd`,
  #114); no stash; no open or draft PRs from earlier sessions.
- Main CI green on #114: dotnet, Docs, e2e, LLM Context all success.
- Dependencies: no vulnerable packages in any project; the only
  outdated entry is the test-only `Microsoft.Testing.Extensions.TrxReport`
  2.3.3 → 2.5.1 (left alone — the MTP lane was deliberately settled in
  #113).
- Open issues at start: #115 (operator-blocked: needs the npm account
  + `NPM_TOKEN`), #80 (dormant by its own text). Neither is
  agent-actionable, so the driver came from upstream protocol drift.
- Unity mirror was fresh; `lint-conventions` green at start.

## The surface: upstream protocol driver (server PR #742)

The server pins a handshake contract for an unsupported requested
`game_data_format`: it answers **one**
`Error(UNSUPPORTED_GAME_DATA_FORMAT)` frame **before** `Authenticated`,
downgrades the session to JSON, and the reference clients (native +
browser) were fixed to consume exactly that — non-fatal notice, JSON
wire shape for every post-handshake decision, fatal on repeat notices,
other error codes, and auth refusals (signal-fish-server #742, changelog
2026-10-06 block).

The .NET client survived that handshake only by coincidence: the notice
arrives before `ProtocolInfo`, where the delivery gate still runs its
initial v2-mode engine, whose `ObserveUnsupportedFormatError`
short-circuit accepted it silently. Conformance rested on engine-swap
timing, and it diverged from the pinned contract in two reachable ways:

- A **repeat** pre-negotiation notice was accepted silently too (the
  short-circuit is unconditional) — the contract keeps repeats fatal.
- The **refused token survived negotiation**: after the notice,
  `ResolveFormatNegotiation` still matched `_requestedFormatToken`
  against the advertisement, so a server that refused then advertised
  the same token would have negotiated the encoding it just refused.

## Red-green

- RED (`fast-check -Filter Downgrade`): 4 failed —
  `DowngradeNoticePinsJsonOverTheRequestedToken` (settled MessagePack
  after the notice) and the three
  `RepeatPreNegotiationDowngradeNoticeRefusesPerPolicy` cases (silent
  accept under every policy). The four accept-once / latch-reset tests
  were green from the start — they pin the correct current behavior so
  the redesign cannot regress it.
- GREEN: the same filter 8/8, then the full suite 942/942 on net8.0 and
  net10.0, `lint-conventions` 6/6, `lint-llm-instructions`,
  `lint-file-sizes`, and the Unity mirror re-synced (`sync-unity-package`
  + `-Check`).

## What shipped

- `DeliveryGate.ObserveUnsupportedFormatError`: an explicit
  pre-negotiation clause — the first notice is accepted once (not a
  delivery violation, any policy), clears the requested token so JSON
  is pinned against even a contradicting advertisement, and a repeat
  refuses per the violation policy. Post-negotiation behavior is
  untouched (armed-by-causal-report only). `ObserveTerminal` resets the
  per-connection latch, so a reconnection may legitimately downgrade
  again.
- 5 tests in `DeliveryGateTests` (9 cases: 6 data-driven + 3 singles),
  including one wire-level test that replays the server's exact
  handshake order (notice → golden `Authenticated` → golden
  `ProtocolInfo`) through `FramePipeline` and asserts JSON settlement
  with no violation.
- Docs: the pinned contract in
  `.llm/references/protocol-quick-reference.md` (new
  `UNSUPPORTED_GAME_DATA_FORMAT` section), the downgrade behavior on
  `SignalFishClientOptions.GameDataFormat`, `FramePipeline`'s
  gate-feed doc, a `CHANGELOG.md` Fixed entry, and the Unity mirror.

## Leftover / notes

- The public API surface is untouched (all changes internal + XML
  docs), so the api-compat gate is unaffected.
- #115 and #80 stay open, both operator-gated; see PLAN.md.
- Watch signal-fish-server for further client-contract pins — the
  improvement log carries the lesson (diff upstream reference-client
  changes explicitly).
