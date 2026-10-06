# Session 049 — Unity validation runbook (M8.7)

Date: 2026-10-06
Branch: `unity-validation-runbook-m8.7`

## What shipped

**M8.7 — the per-adapter validation runbook**, closing milestone M8:

- New `docs/unity-validation.md`: the runbook the scripted MCP
  pipeline follows once a licensed Unity seat lands. It records the
  shared drill shape — pin the environment, two-client loopback,
  failure paths, budgets, record dated findings — and one entry per
  Unity surface: the M7.4 core/WebGL smokes, every M8 adapter
  (FishNet, Mirror, NGO + Relay, BYOS, PUN2, Fusion, Steamworks.NET,
  Facepunch), and the cross-binding Steam room (one peer per binding
  on the byte-identical lane keys). Each entry pins the SDK versions,
  the roles, and the watch list — taken from each adapter page's
  documented behavior and limitations, which are exactly the
  behaviors live validation must confirm.
- Shared validation section shape: every adapter page (and the Unity
  and WebGL pages — ten pages carried the placeholder) now points its
  live-validation sentence at its runbook entry instead of carrying
  the open "M8.7 runbook item" placeholder. The prose keeps each
  page's own contract-checked story; the drill link is the shared
  part.
- The `author-engine-adapter` skill's validation-honesty rule now
  says a new adapter ships its runbook entry with the package, so the
  page set stays closed under future adapters.

## Why this shape

Unity never builds in CI (locked decision), so live behavior was
tracked as prose placeholders scattered across nine pages. The
runbook moves the drill steps to one page, makes each adapter's gap a
link instead of a dangling promise, and gives the licensed seat a
checklist it can execute session one — pins first, so every result is
reproducible and an SDK upgrade re-runs the drill.

## Validation

- Docs gates locally: markdownlint, typos, and `mkdocs build
  --strict` all clean; every runbook anchor link matches its heading
  slug.
- No C# changes; the dotnet suite was not touched.

## Open issues triage

- This deliverable: PR opened from `unity-validation-runbook-m8.7`;
  close on merge.
- #88 (FsCheck.NUnit pins NUnit < 5): unchanged — external wait, no
  new FsCheck.NUnit release.
- #80 (mkdocs markdown extensions when first used): no-action by its
  own terms; the runbook page uses no new extensions.

## Follow-ups

- M9.1: MinVer stamps the version from git tags into
  `SignalFishClientInfo.SdkVersion` (the next PLAN surface).
- The drills run when the licensed Unity seat lands (M7.4 block);
  findings fold into `.llm` skills and dated session-log entries on
  the runbook page.
- Fusion: an SDK upgrade that grows `INetworkRunnerCallbacks` needs
  the lint stub lane re-pinned in the same change (recorded in the
  Fusion drill entry).
