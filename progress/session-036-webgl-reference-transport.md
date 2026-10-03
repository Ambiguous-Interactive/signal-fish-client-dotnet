# Session 036 — M7.3: the WebGL reference transport

Date: 2026-10-03. Scope: the M7.3 milestone item — the browser-WebSocket
`ITransport` for WebGL builds, shipped inside the Unity package as the
`Plugins/SignalFishWebGL/` folder. Routine drift check first: local main
matched `origin/main` (`c237099`), no fetch drift, working tree clean,
no open/draft PRs, no open issues, no stashes, main CI green (all four
workflows). All local branches from sessions 001-035 checked with
`git diff main <branch>` — only stale pre-squash trees, no unique work
to carry forward.

## Starting state

The library and package were complete through M7.2, but WebGL builds
had no transport story: `WebSocketTransport` wraps
`ClientWebSocket`, which does not exist on WebGL, and `docs/unity.md`
said a reference `*.jslib` transport was "planned for the next
milestone". `ITransport` was already the injection point (client ctor +
`ReconnectPolicy.TransportFactory`), so the library needed zero
changes.

## Delivered

- **The plugin** (`unity/Packages/com.ambiguous-interactive.signalfish/Plugins/SignalFishWebGL/`):
  - `SignalFishWebSocket.jslib` — the Emscripten plugin: owns the
    browser `WebSocket`, queues incoming frames JavaScript-side
    (text UTF-8-encoded via `TextEncoder`, binary as `Uint8Array`),
    reports observed close codes (missing ones normalized to 1006),
    and never throws across the interop boundary — failures are return
    codes so a browser fault can never unwind into IL2CPP. Shared
    state lives in a `$SignalFishWebSocket` dependency registered with
    `autoAddDeps`; handles let multiple transports coexist (the
    reconnect path creates a fresh transport while the old one drains).
  - `SignalFishWebGLTransport.cs` — the `ITransport` over seven
    `[DllImport("__Internal")]` entry points: connect-at-most-once,
    single reader, terminal close surfaced exactly once with the
    observed wire code, sends serialized by the browser's single
    thread and capped at the 64 KiB inbound limit, 8 MiB receive bound
    (no `client-config` probe exists for the browser), idempotent
    dispose that initiates the polite close (1000). Receives poll the
    JS queue (~1 ms `Task.Delay` cadence — a page has no threads and
    callbacks into IL2CPP need fragile reverse interop; the poll
    design keeps the plugin pure JS and the C# pure BCL). Payloads
    cross the boundary through GCHandle-pinned buffers — no
    `unsafe`, so the asmdef stays `allowUnsafeCode: false` and the
    file uses zero engine APIs.
  - `SignalFish.Client.WebGL.asmdef` — `includePlatforms: ["WebGL"]`,
    references the `SignalFish.Client` runtime asmdef, no engine
    references: the transport compiles only on the WebGL player and
    other builds never see it.
- **The red gate** (`scripts/lint-webgl-plugin.ps1`, dotnet-CI step +
  self-tested): no compiler in this repo sees the C#/jslib pair
  together, so the lint is the contract:
  1. every `DllImport("__Internal")` entry must exist as an exported
     function in a sibling `*.jslib`, and a `*.jslib` without a
     sibling interop `*.cs` is a dead plugin;
  2. the plugin C# plus the library sources compile as one
     netstandard2.1 assembly with `UNITY_WEBGL` defined, warnings as
     errors (the shape Unity compiles on the player);
  3. `node --check` syntax-checks each `*.jslib` when node is on PATH
     (probed through a temp `.js` copy — node rejects the extension).
  Red-green verified by breaking each side by hand (entry rename →
  named failure; C# typo → compile failure), and self-tested in
  `scripts/tests/test-lint-webgl-plugin.ps1` (10 assertions over
  fixture repos: matching pair, missing entry, extra exports harmless,
  dead plugin, commented-out export, syntax-broken jslib, and the real
  repository plugin).
- **Docs**: `docs/unity.md` (WebGL section: plugin layout, usage with
  the reconnect factory, limits — no threads/poll cadence, no
  client-config probe, `#if UNITY_WEBGL` guarding, validation status)
  and `docs/transport.md` (the transport now ships; contract parity
  summary). `docs/unity.md` "what ships" list gained the plugin row;
  `CHANGELOG.md` gained the user-visible entry.
- **Context**: `.llm/skills/unity-compatibility/SKILL.md` WebGL section
  updated (the reference transport ships; custom `ITransport` remains
  an option; the lint pins the contract). Skills index regenerated.

## Adversarial review round

A critical review sub-agent (plus verification of the Unity-2021.2-era
Emscripten sources) found two blockers and a tail of smaller items, all
fixed in this session:

- **Text sends corrupted on the minimum Unity version** (blocker): the
  jslib called `UTF8ArrayToString(view)` without the `idx` argument,
  which Emscripten 2.0.19 does not default (`endIdx = idx +
  maxBytesToRead` with both undefined) — every text send would have
  gone out as `""` behind a green CI, config-dependently masked in
  some builds. Rather than index into compiler-internal string
  helpers whose signatures shift across versions, string bytes now
  cross the boundary through the standard `TextEncoder`/`TextDecoder`
  pair (hoisted once per plugin, which also stops the per-message
  encoder allocation).
- **The `-Pack` tarball shipped a broken asmdef graph** (blocker,
  dormant bug armed by this change): the pack stage skipped `Runtime/`
  wholesale and re-laid only the `*.cs` mirror, so the hand-written
  `Runtime/SignalFish.Client.asmdef` never reached the tarball —
  harmless until the new WebGL asmdef referenced it by name, which
  made the shipped-from-tarball package non-compilable on exactly the
  platform this milestone targets. The pack stage now copies `Runtime/`
  verbatim before the mirror overlay (orphan removal stays `*.cs`-only)
  and pins the shipped asmdef graph: every asmdef referenced by a
  shipped asmdef must itself ship (name-based resolution). Pinned
  red-green in `scripts/tests/test-sync-unity-package.ps1` (the
  dangling-reference pack fails; a resolving reference recovers; the
  tarball listing includes both asmdefs) — 29 assertions.
- Zero-length queued messages were silently consumed and dropped by
  `poll`; they are now skipped explicitly (documented in the contract
  header). The terminal close now releases the plugin entry immediately
  (parity with the .NET transport releasing the socket; on the
  oversized-frame path it also stops an open browser socket from
  buffering into the JS queue). The lint's entry regex no longer
  over-anchors on `private` (a visibility edit cannot silently drop an
  entry from the contract). The docs usage snippet was rewritten to
  the positional options constructor (`ReconnectPolicy` is get-only;
  the object-initializer form did not compile).

## Scope decision

Live in-browser validation stays M7.4 (needs a licensed Unity seat);
CI pins everything CI can pin, and `docs/unity.md` says exactly that.
The mkdocs `webgl` page split-out is left to M7.5 (the next surface)
rather than growing this PR.

## Verification

- `dotnet build -warnaserror` clean; 815 unit tests green on both
  net8.0 and net10.0 (library untouched); all six convention lints
  green; CSharpier check green on the new transport file; sync mirror
  `-Check` green (the plugin lives outside `Runtime/`, so the mirror
  contract is untouched); a fresh `-Pack` tarball contains the plugin
  folder and both asmdefs, and its asmdef graph is pin-checked.
- `scripts/tests/run-all.ps1` green (14 self-test files, including the
  new WebGL-plugin one and the extended sync one); 
  `lint-webgl-plugin.ps1` full lane (compile + syntax) green.
- `markdownlint-cli2` (12 files), `typos` (v1.50.2, aarch64), and
  `mkdocs build --strict` all green.

## Deferred (tracked)

- M7.4 live Unity validation (IL2CPP + WebGL smokes, allocation pass)
  — needs a licensed Unity seat; the runbook lands with it.
- M7.5 the full mkdocs page set (the `webgl` page can split out of
  `docs/unity.md`).
- Relay-throughput on WebGL is poll-cadence-bounded by design; if a
  real workload needs more, the follow-up is a receive pump on
  `Application.onFrame`-style driving, not a plugin rewrite (noted
  here; no issue opened — no current demand).
