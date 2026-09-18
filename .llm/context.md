# Signal Fish .NET Client — LLM Agent Instructions

You are working on the .NET client SDK for the Signal Fish multiplayer signaling
service. This file is the canonical context entrypoint for every AI agent
front-end (Claude Code, Codex/ChatGPT, Gemini CLI, Cursor, GitHub Copilot,
opencode, and others). Procedural skills live in the [skills/](./skills/)
directory — see [Skills Reference](#skills-reference).

---

## Repository Overview

- **Package**: `SignalFish.Client` — a C# client for the
  [Signal Fish server](https://github.com/Ambiguous-Interactive/signal-fish-server),
  a lightweight in-memory WebSocket signaling server for peer-to-peer
  multiplayer games (rooms, lobbies, reconnection, spectators, authority,
  WebRTC signaling, relay).
- **Target**: `netstandard2.1`, explicitly **Unity compatible** (Unity 2021.2+,
  Mono and IL2CPP). See [unity-compatibility](./skills/unity-compatibility/SKILL.md).
- **Root namespace**: `SignalFish.Client`.
- **Protocol**: JSON over WebSocket, `{ "type": ..., "data": ... }` envelopes.
  The client handles signaling and relay only — the game owns simulation.
- **Status**: early scaffold; the public surface is not frozen yet.

## Project Structure

```
.llm/                    Agent context (this folder) - context.md, skills/, references/
scripts/                 PowerShell automation (index generation, linters, hooks install)
scripts/tests/           Self-tests for the automation scripts
.githooks/               Git hooks (pre-commit: linters + index freshness)
.github/                 CI workflows + Copilot instructions
src/SignalFish.Client/   The library (netstandard2.1)
tests/                   Test projects (NUnit, net8.0 runner)
CLAUDE.md, AGENTS.md,    Thin pointer files that delegate to this file
GEMINI.md, llms.txt
```

## Skills Reference

Skills follow the [Agent Skills](https://agentskills.io) standard: each folder
in `.llm/skills/` contains a `SKILL.md` with `name` and `description`
frontmatter. Read the generated [Skills Index](./skills/index.md) for the full
list with trigger descriptions, and open a skill's `SKILL.md` only when its
description matches the current task. After adding or editing any skill, run
`pwsh -NoProfile -File scripts/generate-skills-index.ps1` (enforced by hooks
and CI).

## Critical Rules Summary

### C# Code Rules

1. **Target `netstandard2.1` only; stay Unity compatible.** No APIs outside the
   netstandard2.1 surface without a compatibility note (see
   [unity-compatibility](./skills/unity-compatibility/SKILL.md)).
2. **All I/O goes through the transport abstraction.** The protocol layer must
   not reference `ClientWebSocket` directly (see
   [websocket-transport](./skills/websocket-transport/SKILL.md)).
3. **Public async APIs are `Task`-based with a trailing
   `CancellationToken`** — no sync-over-async, no `async void` (see
   [api-design](./skills/api-design/SKILL.md)).
4. **Library code uses `ConfigureAwait(false)`** and never blocks on tasks
   (see [async-threading](./skills/async-threading/SKILL.md)).
5. **The v2 relay floor is sacred.** Mandatory v2 messages must always work
   byte-identically; v3 is additive opt-in (see
   [protocol-messages](./skills/protocol-messages/SKILL.md)).
6. **Wire format is `{ "type": PascalCase, "data": { snake_case fields } }`.**
   Never route on prose text; route on the `type` discriminator and
   `error_code` tokens (see
   [json-serialization](./skills/json-serialization/SKILL.md) and
   [error-handling](./skills/error-handling/SKILL.md)).
7. **Unknown message types and unknown fields must not throw** — the protocol
   is additive; surface them as forward-compatible events (see
   [json-serialization](./skills/json-serialization/SKILL.md)).
8. **Heartbeats are mandatory.** Send application-level `Ping` periodically or
   the server drops idle connections (see
   [async-threading](./skills/async-threading/SKILL.md)).
9. **Reconnection uses the server-issued token**, replays control events
   only, and never assumes `GameData` replay (see
   [reconnection](./skills/reconnection/SKILL.md)).
10. **Tests are NUnit, deterministic, and use fake transports** — no real
    network in unit tests (see [create-test](./skills/create-test/SKILL.md)).

### Documentation & Context Rules

11. **Every file under `.llm/` and every front-end pointer file must stay
    within the 300-line hard limit** (warn at 270). Run
    `scripts/lint-file-sizes.ps1` after editing (see
    [manage-skills](./skills/manage-skills/SKILL.md)).
12. **Pointer files (`CLAUDE.md`, `AGENTS.md`, `GEMINI.md`,
    `.cursor/rules/signal-fish.mdc`, `.github/copilot-instructions.md`,
    `llms.txt`) stay thin** — they only delegate here; never add content to
    them.
13. **`skills/index.md` is generated — never edit it by hand.** Regenerate
    after any skill change.
14. **Skill links are relative and must resolve** (`./*.md`,
    `../<skill>/SKILL.md`, `../../references/*.md`). Broken links fail CI.

## Protocol Essentials

Connect to `ws://<host>:3536/v2/ws` (v2 floor) or `/v3/ws` (negotiated v3).
Every frame is a JSON envelope; payloads use `snake_case` fields. The
mandatory v2 flow: `Authenticate?` → `JoinRoom` → `RoomJoined` →
`PlayerReady` → `StartGame` → `GameStarting` → `GameData` loop → `LeaveRoom`.
Handle `Error` / `*Failed` messages and close codes 4000-4007.
See [protocol-quick-reference](./references/protocol-quick-reference.md)
for the full tables, and
[protocol-messages](./skills/protocol-messages/SKILL.md) for implementation
guidance.

## Build & Development Commands

| Command | Purpose |
| --- | --- |
| `dotnet build` | Build the solution |
| `dotnet test` | Run the test suite |
| `pwsh -NoProfile -File scripts/install-hooks.ps1` | One-time: activate git hooks |
| `pwsh -NoProfile -File scripts/generate-skills-index.ps1` | Regenerate `.llm/skills/index.md` |
| `pwsh -NoProfile -File scripts/lint-file-sizes.ps1` | Enforce the 300-line limit |
| `pwsh -NoProfile -File scripts/lint-llm-instructions.ps1` | Validate the whole `.llm` system |
| `pwsh -NoProfile -File scripts/tests/run-all.ps1` | Run the automation self-tests |

## Naming Conventions

| Item | Convention | Example |
| --- | --- | --- |
| Types, methods, properties | PascalCase | `SignalFishClient` |
| Interfaces | `I` prefix | `ITransport` |
| Private fields | `_camelCase` | `_eventChannel` |
| Locals / parameters | camelCase | `roomCode` |
| Constants | PascalCase | `DefaultPort` |
| Files | one public type per file, name = type | `ITransport.cs` |
| Tests | `<Type>Tests` / `Method_Scenario_Expectation` | `ClientTests.JoinRoom_WithoutCode_CreatesRoom` |
| Skills | lowercase verb-noun kebab-case | `create-test` |

## Agent-Specific Rules

- **Single source of truth**: front-end pointer files only delegate here.
  Never duplicate rules into them; extend this file or a skill.
- **Verify before done**: run `dotnet build`, `dotnet test`, and
  `scripts/tests/run-all.ps1` when your change touches C# or the `.llm`
  system respectively. Do not declare success on unverified code.
- **No proactive git commits** unless the user explicitly asks.
- **Match upstream protocol behavior, never guess it**: when unsure about a
  message shape, consult
  [protocol-quick-reference](./references/protocol-quick-reference.md) and the
  server docs it links to — not assumptions from other SDKs.
- Keep local validation bounded: do not run network e2e loops or long-running
  servers inside an agent session without asking.
