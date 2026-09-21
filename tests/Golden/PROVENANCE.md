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
The mandatory v2 floor also includes `GameStarting`, `RoomLeft`, and the
`*Failed` family, which have no upstream wire samples yet. Request them
upstream; never hand-vendor replacements.
