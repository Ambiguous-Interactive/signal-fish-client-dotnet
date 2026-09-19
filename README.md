# Signal Fish Client (.NET)

A C# client SDK for the [Signal Fish server](https://github.com/Ambiguous-Interactive/signal-fish-server)
— a lightweight, in-memory WebSocket signaling server for peer-to-peer
multiplayer games. This client connects a game to a Signal Fish server, joins
players into rooms, relays game data, and exposes server activity as typed
events.

- **Target**: `netstandard2.1`, explicitly Unity compatible (2021.2+, Mono /
  IL2CPP; WebGL via injected transport).
- **Scope**: signaling and relay only — your game owns simulation, rollback,
  and peer networking.

## Status

Early scaffold. The protocol layer (v2 relay floor + optional v3
negotiation) and public API are under active development.

## Development

```powershell
dotnet build
dotnet test
pwsh -NoProfile -File scripts/install-hooks.ps1   # one-time: activate git hooks
pwsh -NoProfile -File scripts/tests/run-all.ps1   # automation self-tests
```

## AI-assisted development

This repository uses a vendor-neutral agentic context system: `.llm/context.md`
is the single source of truth, with thin pointer files for each agent
front-end (`CLAUDE.md`, `AGENTS.md`, `GEMINI.md`, `.cursor/rules/`,
`.github/copilot-instructions.md`, `llms.txt`) and standards-compliant
[Agent Skills](https://agentskills.io) under `.llm/skills/`. A generated index,
pre-commit hooks, and CI keep everything validated.

## AI disclosure

Development of this repository is substantially assisted by AI coding agents,
reviewed and directed by human maintainers.

## License

MIT — see [LICENSE](LICENSE).
