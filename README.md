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

Built by [Ambiguous Interactive](https://github.com/Ambiguous-Interactive).

> **AI disclosure:** This project was developed with substantial assistance
> from AI coding agents (Codex, Gemini, GLM, and others). Humans created the
> protocol concepts and core design and retained oversight of architecture
> and code review.

## Status

Feature-complete for the 0.1.0 milestone: the v2 relay floor and the v3
extensions (negotiation, classified delivery with accountability, binary
game data, mesh signaling) are done and covered by unit, fuzz, and live
E2E lanes. Tagged releases ship the NuGet package and all nine Unity UPM
tarballs — `com.ambiguous-interactive.signalfish` installs from a
release tarball or from source
([Unity](docs/unity.md), [Releasing](docs/releasing.md)). The
[documentation site](https://Ambiguous-Interactive.github.io/signal-fish-client-dotnet/)
mirrors the Rust client's doc set.

## Development

```powershell
dotnet build
dotnet test
pwsh -NoProfile -File scripts/install-hooks.ps1   # one-time: activate git hooks
pwsh -NoProfile -File scripts/tests/run-all.ps1   # automation self-tests
dotnet tool restore                               # one-time: restore CSharpier
dotnet tool run csharpier -- format .             # deterministic C# formatting
```

The client library builds with warnings-as-errors, the full built-in analyzer
set, and a LINQ ban (`src/` only); CSharpier and the pre-commit hooks keep
formatting and the lints enforced locally.

## AI-assisted development

This repository uses a vendor-neutral agentic context system: `.llm/context.md`
is the single source of truth, with thin pointer files for each agent
front-end (`CLAUDE.md`, `AGENTS.md`, `GEMINI.md`, `.cursor/rules/`,
`.github/copilot-instructions.md`, `llms.txt`) and standards-compliant
[Agent Skills](https://agentskills.io) under `.llm/skills/`. A generated index,
pre-commit hooks, and CI keep everything validated. The disclosure blockquote
at the top of this file states the project's AI-assistance policy.

## License

MIT — see [LICENSE](LICENSE).
