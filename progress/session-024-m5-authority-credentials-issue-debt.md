# Session 024 — M5 (spectators, authority, credentials) + issue debt

Date: 2026-09-22. Scope: the full M5 milestone red-green, plus driving the
open issue queue to zero (#53, #49) and a wire-truth sweep that found and
fixed a decoder bug (#54). One deliverable PR.

## Delivered

- **#53 (dispose races)** — `WebSocketTransport`: `CloseStatus` reads on
  the terminal-close and fault paths go through one guarded helper
  (null/disposed socket → abnormal fallback; netstandard2.1/Mono can
  throw where net8 returns null), and the connect CAS-failure path now
  disposes the local socket so a raced dispose can never leak a connected
  socket by construction. Contract pins: mid-upgrade dispose (upgrade
  gate in `TestWsServer`) stays terminal + idempotent; concurrent
  server-close/dispose always ends in a bounded close frame (4007 or
  1006, never a raw exception). Underlying TCP teardown timing during a
  mid-upgrade dispose is `ClientWebSocket`-internal and deliberately not
  asserted.
- **#49 (live reconnection scenario)** — resolved by wire truth: the
  server docs state the token rides `RoomJoined`/`Reconnected` for v3+
  only; the v2 wire omits it. Floor note added to `docs/conformance.md`;
  scenario deferred to the M6.6 v3 E2E set. Closed as completed.
- **#54 (found + fixed in-session)** — the AsyncAPI spec declares
  `AuthorityChanged.authority_player` required-but-nullable (null = seat
  vacated); the decoder accepted only quoted UUIDs, so a legal vacated
  broadcast surfaced as `ProtocolViolation`. Same class as the M3.5
  `reconnection_token` null sweep; v2 sweep found no other instance.
- **M5.1** — password sealing: `ToString` redaction on
  `JoinRoomMessage`/`JoinAsSpectatorMessage`; data-driven pins that the
  three sealed-room failure classes all carry `PASSWORD_REQUIRED`; E2E
  seals a room and walks passwordless → wrong → correct.
- **M5.2** — `SendAuthorityRequest` on both clients; admission
  player-only with Rust-parity `AuthorityRequired` for a relinquish
  without hold (never fenced — `PendingOperationFor` stays null, so the
  five directed ops keep their own fences); `is_authority` tracked in the
  machine (baseline seed, broadcast update, leave/terminal clear) and
  mirrored as `ClientSnapshot.IsAuthority`; `AuthorityChanged` is now a
  session fact (malformed → violation, nothing applied). E2E: claim →
  both snapshots update → old authority's start refused
  (`GAME_START_FORBIDDEN`) → holder starts.
- **M5.3** — `SignalFishClientOptions.AppId`/`ConnectToken`
  (token redacted in `ToString`) with `SdkVersion`/`Platform` from
  `SignalFishClientInfo`; the per-round auto-handshake re-authenticates
  with the configured credentials (the payload-less default shape is
  pinned unchanged by the existing reclaim test modulo the SDK identity
  the milestone adds).

## Verification

- `dotnet build` clean; 544 unit tests green on net8.0 and net10.0 (+21).
- Six convention lints + csharpier clean; E2E/tooling projects compile.
- Red-checks: the #54 decode test fails against the reverted decoder;
  the admission matrix caught a real parity subtlety —
  `RequestAuthority` is not one of the five directed ops, so
  unauthenticated queueing is `NotInRoom`, not `NotAuthenticated`.
- CI-time: dotnet cells +~2 s (21 tests), e2e +~10 s (2 scenarios);
  net-flat against the 1 m20 s / 1 m14 s mains.

## Findings / decisions

- The two #53 races are unreachable on net8/net10 (verified by trace +
  probe: `CloseStatus` after dispose returns null there) — the fixes are
  structural for the shipped netstandard2.1 TFM, and the issue is closed
  with that honest framing.
- Mid-upgrade dispose can leave the underlying TCP open for seconds; the
  transport cannot own that (runtime-internal), so the test asserts only
  transport-owned behavior (terminal, idempotent, ODE on use).
- Polling-client credentials stay explicit-first (`SendAuthenticate`
  accepts the full message); auto-credentials are the async client's
  reconnect-round concern. Revisit with M7 if Unity samples want
  config-carried credentials there.

## Leftover (tracked as issues)

- None open. Next surface: M6.1 negotiation (`Authenticate` capabilities
  + `ProtocolInfo` cap-down).
