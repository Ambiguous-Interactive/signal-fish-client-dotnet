# Golden Protocol Fixtures

Vendored wire samples for the Signal Fish protocol; the red-green source of
truth for the hand-rolled codec (`PLAN.md` M1). **Never hand-edit these
files** - resync via the sync script (`scripts/sync-protocol-fixtures.ps1`)
or fix upstream.

- Source: <https://github.com/Ambiguous-Interactive/signal-fish-server>
- Upstream path: `.llm/code-samples/protocol/`
- Pinned commit: `44c90905db4a7fc57a50ceed171278ad7d144732`
- Last synced: 2026-10-08 (UTC)

## Files

| File | Lines |
| --- | --- |
| `v2-client-messages.jsonl` | 15 |
| `v2-server-messages.jsonl` | 24 |
| `v3-client-messages.jsonl` | 10 |
| `v3-server-messages.jsonl` | 17 |

## Coverage gaps (at this pin)

The corpus is verbatim upstream and covers only what the server publishes.
At pin `44c90905` (server 0.10.0) the mandatory v2 floor has wire samples,
including `GameStarting`, `RoomLeft`, and the `*Failed` family, plus
the additive `JoinRoom.join_only` and
`ProtocolInfo.implementation_version` shapes. `game_data_limits` has no
upstream sample (the field only appears when the deployment configures
per-encoding caps), so its decode policy is pinned by inline wire strings in
the payload tests. If a future floor message lacks a sample here, request it
upstream; never hand-vendor replacements.
