# Session 042 — M8.3: NGO + Unity Relay coordinator

Date: 2026-10-04
Branch: `ngo-relay-coordinator-m8.3`
Closes: (no tracking issue; the PLAN M8.3 surface)

## What shipped

**M8.3 Wave 1 NGO + Unity Relay** — a third Wave-1 engine integration,
built as a coordinator rather than a transport bridge (NGO's transport
is Unity Transport; the room owns matchmaking and membership, not the
data path):

- `unity/Adapters/Ngo` (`com.ambiguous-interactive.signalfish.adapters.ngo`,
  asmdef `SignalFish.Adapters.Ngo`) ships `SignalFishRoomCoordinator`:
  the host joins/creates the room, takes the authority, allocates a
  Unity Relay server through a game hook (`RelayAllocationRequest`),
  and broadcasts the join code over the room's JSON game-data lane
  (re-broadcast on every player-joined, so late joiners never need an
  out-of-band channel); clients join by room code, bind the received
  code through `RelayJoinBinder`, and start the NGO client with their
  Signal Fish player id as the connection payload. **Connection
  approval is membership**: the coordinator approves an NGO connection
  only when its claimed player id (16 RFC-4122 bytes via the shared
  `AdapterWire` spelling) is a live room member.
- The pure core (`Runtime/Core`, engine- and NGO-free, compiled into
  the dotnet suite): `RelayJoinCodeEnvelope` (the exact quoted-property
  JSON envelope; foreign game payloads decode as absent, unknown fields
  are ignored, conservative [A-Za-z0-9_-] ≤ 128 charset so the codec
  needs no JSON parser), `SignalFishRoomRoster` (the membership the
  approval callback consults; snapshot-seeded, event-current), and
  `ConnectionApprovalPayload` (the 16-byte payload wrapper).
- Define plumbing follows the FishNet shape (NGO ships a real UPM
  package): `versionDefines` pin `com.unity.netcode.gameobjects` @
  `1.2.0` → `SIGNALFISH_NGO`, with the editor detector probing the
  compiled `Unity.Netcode.Runtime` assembly and the package marker as
  the vendored-install fallback.
- `lint-unity-adapter` generalizations (self-test grew to 72
  assertions): the per-SDK reference pattern is now a pin field
  (`SdkReferencePattern`) instead of a FishNet/Mirror if/else, the
  SDK-namespace vendoring probe regex-escapes the namespace, the
  shared-wire check is a per-package `WirePin` (`AdapterWire.` for the
  transport bridges, `ConnectionApprovalPayload.` for the coordinator),
  and the engine channel pin is optional — the coordinator has no
  engine channel mapping, so `ChannelPin = $null` skips that check
  honestly instead of pinning a lie.
- Tests: +21 dotnet cases (envelope round-trip/foreign-payload/charset/
  rescan, roster seed/events/empty-id refusal, approval-payload
  round-trip and byte-identity with `AdapterWire`). Suite: 876 per TFM
  (+21).
- Docs: `docs/adapters/ngo.md` + mkdocs nav; CHANGELOG Added entry;
  `Samples~/RelayRoomCoordinator` shows the full Unity Gaming Services
  Relay wiring for both hooks, marshaled to the engine main thread.

## Adversarial review round

The first review pass verified the bridge against the NGO 1.2.0 source
and found the connection-approval gate **non-functional**: NGO 1.2.0
only arms approval when `NetworkConfig.ConnectionApproval` is true —
without it the host auto-approves every connection and the client never
sends its ConnectionData, so the package's core contract was a silent
no-op. Eight further findings, all fixed and covered:

- The approval flag is set on both sides, and the **host presents its
  own id** as `ConnectionData` (NGO runs the host's own connection
  through the same callback — an empty payload would have made the host
  fail its own approval).
- `ConnectionApprovalCallback` is **assigned, not `+=`** — NGO 1.2.0's
  setter throws when the delegate has two targets and Shutdown never
  clears it, so a second session on the same NetworkManager would
  always fail.
- `StartHost()`/`StartClient()` **return values are checked** (they
  report refusal as `false` + a log, not an exception).
- The staged-completion await no longer masks engine-start failures as
  teardown cancellations (timeout check → exception propagation →
  freshness check), and a `Fail` during the host's join-code broadcast
  surfaces instead of reporting success.
- Concurrent starts are serialized: guard + generation bump under the
  lock, freshness re-checked after client creation (the Mirror
  bootstrap's pattern), so the loser disposes its own client.
- The roster became thread-safe and its doc says so — the bootstrap
  waits apply membership events off the main thread while the approval
  callback reads on it.
- The sample's UGS hooks marshal the Unity Transport binding through a
  main-thread queue (Unity object APIs throw off-thread); the README's
  inverted thread claim is fixed.

The second review pass verified all eight fixes against the NGO 1.2.0
source and found the fixes' own cleanup contract incomplete — also
fixed:

- A `Shutdown` while a Relay hook was still in flight no longer runs
  the engine start for a dead session: `Stage` refuses stale
  generations under the lock, `RunStagedStart` re-checks before it
  touches the engine (a stale staged start is disposed, never started),
  and the hook awaits are generation-guarded and deadline-bounded
  (`AwaitHook`) so a jammed hook cannot park a start forever.
- The start-failure path no longer tears down a sibling session: the
  publish-freshness throw moved inside the try (the client is disposed
  by the catch), and the catch's `Teardown` runs only while the
  generation is still ours.
- An engine start that succeeded before a later failure (a refused
  join-code broadcast) now shuts the NGO session down with the room
  session — the same discipline `Fail` follows — instead of leaving a
  live, uncoordinated host or client.
- The live drain re-checks liveness per event and passes the drained
  client to the broadcast, so a mid-drain disposal can neither throw
  out of `Update` nor send on a disposed client.
- Sample hygiene: `Start` is not `async`, the fire-and-forget UI
  handlers model try/catch around the starts, and the README documents
  the keep-the-component-enabled contract.

The third review pass verified the round-two fixes closed (against the
NGO 1.2.0 source again) and approved with one residual window + one
doc nit, both fixed: an `AwaitStaged` timeout now tears the session
down itself and the staged start re-checks its generation before
reporting success, so a stalled coordinator can never leave a live,
uncoordinated NGO session behind; `JoinCodeTimeoutSeconds`' contract
(it also bounds the relay hooks) and the ten-second engine-start
budget are documented on the property and in the docs table.

## PR feedback round (Cursor Bugbot, three findings)

1. **Host grant treated as migration** — confirmed against the E2E
   conformance suite: a granted `AuthorityResponse` is followed by a
   room-wide `AuthorityChanged` with `you_are_authority: true` for the
   grantee (either order), so the unconditional drain failure tore down
   every healthy host start on its first `Update`. The drain now fails
   only when `you_are_authority != _isHost` (grant echo benign;
   migration away from the host, or a client gaining the seat, fatal).
2. **Allocation hook result dropped** — confirmed: `joinCode = await
   AwaitHook(...)` awaited a void `Task`, a compile error invisible to
   every compiler in CI (`#if SIGNALFISH_NGO` keeps the bridge out of
   dotnet; the lint only compiled the pure core). `AwaitHook` gained a
   generic `Task<T>` overload. The class-level fix: `lint-unity-adapter`
   gained a **shape-stub bridge-compile lane** (`BridgeCompile` /
   `BridgeStubs`) that type-checks engine-gated bridges at the
   netstandard2.1 floor with the define set on every CI run.
3. **Sample invented `allocation.JoinCode`** — confirmed: Unity Relay
   `Allocation` carries no join code; the sample now calls
   `GetJoinCodeAsync(allocation.AllocationId)`.

The new lane's first own catch: `Environment.TickCount64` does not
exist on the netstandard2.1 API floor Unity compiles against — the four
deadline reads now use `SystemClock.Instance.ElapsedMilliseconds` (the
library's monotonic clock). Sweeps: no other netcore-only APIs in
`unity/`, no other unconditional event-fatal branches, no other
invented SDK members. Knowledge folded into the new
`author-engine-adapter` skill and two new sweep rows in
`address-pr-feedback`; the Mirror/FishNet stub-compile gap is filed as
#91.

## Decisions

- **Coordinator, not a transport bridge**: PLAN's "no relay-transport
  bridge (UTP coupling)" reads as: NGO keeps Unity Transport end to
  end; the adapter only brokers the join code and approves connections.
  The engine-start pattern is the Mirror staged-event shape — bootstrap
  runs background (`ConfigureAwait(false)` everywhere), engine calls
  stage to `Update()` via a one-shot staged bundle with a completion
  source, so starts may be awaited from any thread and never touch
  Unity APIs off the main thread.
- **The client's Relay bind hook is `Func<string, Task>`**, awaited in
  the bootstrap before the NGO client start stages — `JoinAllocationAsync`
  is inherently async, and a fire-and-forget binder would race
  `StartClient()` against `SetRelayServerData`.
- **JSON on the v2 floor**: the join-code lane needs no v3 binary
  negotiation, so the coordinator's session advertises no
  `protocolVersion`/`gameDataFormat` — the room's default JSON
  game-data lane carries the envelope.
- **Fail loudly over half-working**: a host start without
  `RelayAllocationRequest` throws; an authority move away from an NGO
  host tears the coordination down (NGO 1.x has no host migration);
  the join-code envelope refuses codes outside its charset instead of
  escaping them.

## Verification

- `dotnet build` + `dotnet test`: green on both TFMs (876/876).
- `lint-unity-adapter` full lane (with compiles): green — 8 core
  files, 3 bridges, all pinned members present.
- `scripts/tests/run-all.ps1`: all 15 self-test files pass (the adapter
  lint's own file grew to 72 assertions).
- `lint-conventions.ps1` (all six), CSharpier check (repo-wide),
  markdownlint on the new/changed docs, `sync-unity-package.ps1
  -Check`: clean.

## Follow-ups

- M8.7 live validation runbook must cover the coordinator (licensed
  seat; the NGO package pin 1.2.0 and the UGS hook wiring are the
  editor-side risks).
- Watch item: the NGO pin is 1.2.0 (the Unity 2021.3-compatible line);
  when the floor moves to 2022.3+, re-verify against NGO 2.x and
  re-pin (`PackageExpression` + detector probes are the two places).
- M8.4 Wave 1 generic BYOS sample is the next PLAN surface.
