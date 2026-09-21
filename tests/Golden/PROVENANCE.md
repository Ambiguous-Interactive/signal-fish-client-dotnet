# Golden Protocol Fixtures

Vendored wire samples for the Signal Fish protocol; the red-green source of
truth for the hand-rolled codec (`PLAN.md` M1). **Never hand-edit these
files** - resync via the sync script (`scripts/sync-protocol-fixtures.ps1`)
or fix upstream.

- Source: <https://github.com/Ambiguous-Interactive/signal-fish-server>
- Upstream path: `.llm/code-samples/protocol/`
- Pinned commit: `07a6fd087ea924034dfec126cf9acb8357858aa9`
- Last synced: 2026-09-21 (UTC)

## Files

| File | Lines |
| --- | --- |
| `v2-client-messages.jsonl` | 13 |
| `v2-server-messages.jsonl` | 24 |
| `v3-client-messages.jsonl` | 9 |
| `v3-server-messages.jsonl` | 16 |

## Coverage gaps (at this pin)

The corpus is verbatim upstream and covers only what the server publishes.
At pin `07a6fd08` (server 0.9.2) the full mandatory v2 floor has wire
samples, including `GameStarting`, `RoomLeft`, and the `*Failed`
family. If a future floor message lacks a sample here, request it upstream;
never hand-vendor replacements.
