# Session 031 — M6.4: v3 binary game data + stale-main convergence

Date: 2026-10-02. Scope: converge local main with origin/main after
session 030's squash landing, then drive PLAN.md's next surface — M6.4
binary GameData — to a green PR.

## Starting state

- Local main was 5 commits ahead / 1 behind origin/main, mid-merge with
  four both-added conflicts: session 030's deliverable had landed as
  squash #67 while local main kept its pre-review iteration, and the PR
  branch had since gained three review-fix commits. Tree-diff
  archaeology (`git diff` local tip ↔ squash ↔ `origin/m6.3-wire-…`
  tip) proved local main held zero unique content, so the merge was
  abandoned and main reset onto origin/main — clean convergence, no
  resolution commit, nothing lost.
- origin/main CI fully green (dotnet/Docs/e2e/LLM Context); no open
  issues, no open PRs.

## Delivered

- **Strict MessagePack decode** (`Protocol/BinaryGameDataFrame.cs`):
  the server's binary relay envelope — `from_player` as the 16 RFC-4122
  bytes, `encoding` token, opaque `payload`, paired non-zero
  `seq`/`epoch` on v3 (3-field shape on v2) — decodes with strict
  rules (exact key set, no duplicates, no trailing bytes, any integer
  width, bin/str/map widths). Failures are structured (`DecodeError`
  grew `NotAMap`/`UnknownField`/`DuplicateField`/`MissingField`/
  `InvalidFieldValue`) with the failing byte offset, surfaced as
  bounded `DecodeFailed` events — never a policy teardown.
- **Format negotiation** lives in the gate, learned from the wire:
  `SendAuthenticate` notes the requested encoding;
  `ProtocolInfo` validates the canonical advertisement
  (`[json]` / `[json, message_pack]`; absent/empty tolerated as legacy
  JSON) and settles the effective encoding — requested when advertised,
  JSON otherwise. A non-canonical list rejects the frame before
  negotiation applies (version not applied), matching the Rust
  client's lifecycle rule.
- **Pipeline routing**: binary frames admit only after negotiation
  settled and only against a non-JSON encoding; decoded frames pass the
  representation check (envelope encoding == negotiated) and then the
  same delivery-accountability gate as JSON — binary is reliable by
  definition and shares the sender's one seq stream. The v2 floor is
  untouched (JSON-only, binary still a violation). Text JSON `GameData`
  stays legal under a negotiated binary encoding.
- **The binary send lane**: `SendBinaryGameData` on both drivers relays
  one raw payload as a single binary frame — the server builds the
  relay envelope. Admission: player role + negotiated v3 (machine)
  then a non-JSON encoding (`BinaryFormatNotNegotiated`).
  `ITransport.SendBinaryAsync` carries it; the async command queue tags
  each frame with its lane;
  `SignalFishClientOptions.GameDataFormat` rides the automatic-
  reconnect Authenticate so a fresh round re-requests the encoding.
- **Verification**: 723 unit tests green on net8.0 + net10.0 (47 new:
  decoder strictness matrix incl. integer/container widths and key
  permutations, negotiation resolution matrix, admission/representation/
  gating paths per policy, send-lane wire pins on both drivers,
  reconnect replay, allocation gate proving the binary receive hot path
  stays 0 B). All linters/hooks green.

## What the adversarial review caught

- **Blocker, confirmed empirically**: the decoder bound `from_player`
  and `payload` to the same slice local — any key order other than the
  current server's silently delivered the UUID bytes as the payload.
  MessagePack maps are unordered; each bin now binds to its own key,
  pinned by a key-permutation test the old suite structurally could
  not catch (every fixture emitted `from_player` first).
- **Contract break**: admission-refused frames under Observe continued
  to decode and could advance the cursors (phantom gaps from
  out-of-contract bytes). They now stop at the refusal; the session
  keeps flowing, cursor continuity pinned per policy.
- Also fixed: honest threading docs on the gate's negotiation flag
  (settles once per connection on the receive loop; sends read it
  tear-free), the options' `json` → null normalization, the
  intentional policy bypass for non-canonical `ProtocolInfo`, and
  wider width/epoch-range/disconnect-decode-failure tests.

## Not done here (deliberately)

- The live two-way binary E2E round trip needs the Docker server's
  message_pack lane exercised end-to-end — folded into the M6.6
  scripted scenarios (conformance row updated).
- A coverage-guided fuzz target for the MessagePack scanner (the JSON
  codec's fuzz lane is the template) — filed as an issue.
