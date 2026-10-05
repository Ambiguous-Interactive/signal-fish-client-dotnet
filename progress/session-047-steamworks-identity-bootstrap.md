# Session 047 — Steamworks.NET identity bootstrap (M8.6, first half)

Date: 2026-10-06
Branch: `steamworks-bootstrap-m8.6`

## What shipped

**M8.6 Wave 2, Steamworks.NET half** — `SignalFishSteamIdentityBootstrap`,
the SteamId64 exchange plus the fenced Steam P2P socket bootstrap,
shipped as the UPM source package
`com.ambiguous-interactive.signalfish.adapters.steamworksnet`
(`unity/Adapters/SteamworksNet`):

- Host: Signal Fish join → authority → open
  `SteamNetworkingSockets.CreateListenSocketP2P` → publish the host
  SteamId64 over the room's game-data lane
  (`{"signal_fish_steam_host": "<id>"}`, re-published on every join) →
  ready + start the game. Incoming Steam connections are accepted only
  after the peer's id arrived on the lane
  (`{"signal_fish_steam_peer": "<id>"}`), within a grace window
  (`AcceptGraceSeconds`), and refused with an app-range end reason
  otherwise.
- Client: Signal Fish join by code → ready → wait for the host id on
  the lane → publish its own id → `ConnectP2P` to the published host
  id; the start completes when the Steam connection establishes and
  the connected identity matches the published host id.
- The relay lane is a room broadcast, so the envelope is role-scoped
  (`signal_fish_steam_host` vs `signal_fish_steam_peer`) — a client
  can never read another client's id as the host's. Values are decimal
  strings (1-20 digits, no leading zero), so a 64-bit id survives
  every JSON decoder losslessly.
- The fence trusts Steam's rendezvous-authenticated
  `SteamNetworkingIdentity` and only checks room advertisement; no
  ConnectionInfo user data is involved.
- Honest tier, stated in the docs: the bootstrap establishes and
  fences connections; it is not a data plane (no poll group, no send
  helper) — the game owns the traffic, the same division as the PUN2
  and Fusion bootstraps.
- Steamworks.NET ships as a UPM package (UPM since 20.0.0), so the
  adapter asmdef's `versionDefines` own the define (the NGO pattern);
  the included editor detector fills the vendored gap with the same
  define.

## How the surface was pinned

Every pinned engine member was verified against the real Steamworks.NET
2025.165.0 sources (cloned from `rlabrecque/Steamworks.NET`), not
memory: the autogen `isteamnetworkingsockets.cs` (`CreateListenSocketP2P`,
`ConnectP2P`, `AcceptConnection`, `CloseConnection`,
`CloseListenSocket` — note: `CloseListenSocket`, not
`DestroyListenSocket`), `SteamCallbacks.cs`
(`SteamNetConnectionStatusChangedCallback_t`: `m_hConn`/`m_info`/
`m_eOldState`, standard `Callback<T>` dispatch),
`SteamStructs.cs` (`SteamNetConnectionInfo_t`: `m_identityRemote`,
`m_nUserData`, `m_hListenSocket`, `m_eState`, `m_szEndDebug`),
`SteamEnums.cs` (`ESteamNetworkingConnectionState` None=0,
Connecting=1, FindingRoute=2, Connected=3, ClosedByPeer=4,
ProblemDetectedLocally=5, FinWait=-1, Linger=-2, Dead=-3; `EResult`
None=0, OK=1), `SteamConstants.cs` (`k_nSteamNetworkingSend_Reliable`
= 8, not needed this round), the `types/SteamNetworkingTypes/` handle
structs (`Invalid`, the uint field, `==`/`!=`, `GetHashCode`),
`SteamNetworkingIdentity` (`SetSteamID64`/`GetSteamID64`),
`isteamuser.cs` (`SteamUser.GetSteamID` → `CSteamID.m_SteamID`), and
`Steam.cs` (`SteamAPI.Init`/`RunCallbacks`/`Shutdown`; the sockets
callback dispatches inside `SteamAPI_RunCallbacks`). The UPM-since
claim was verified against the `20.0.0` tag tree
(`com.rlabrecque.steamworks.net` exists there), which pins the
`versionDefines` expression.

## Validation

- `dotnet test`: 16 new envelope contract tests (both lane keys, round
  trip, byte-exact shape, foreign payloads decode as absent, key
  isolation, charset bounds, full unsigned-long range); green on
  net8.0 and net10.0.
- `lint-unity-adapter`: all seven packages green, including the new
  SteamworksNet bridge-compile lane; `test-lint-unity-adapter`
  extended with the SteamworksNet fixture across the negative loops
  (114 assertions, up from 100).
- lint-conventions and the remaining self-test suites green; docs page
  (`docs/adapters/steamworksnet.md`) + nav entry added; CHANGELOG
  entry added.

## Adversarial review round

(findings recorded here after the review loop)

## Open issues triage

- #88 (FsCheck.NUnit pins NUnit < 5): re-verified against nuget.org
  this session — FsCheck.NUnit 3.4.0 is still the latest release; the
  issue's wait-plan stands, no repo change.
- #80 (mkdocs extensions when first used): explicitly no-action by its
  own terms; the steamworksnet docs page uses no new extensions.

## Follow-ups

- M8.6 second half: the Facepunch Steamworks binding (separate
  package, same envelope contract; its `ConnectionInfo` surface needs
  the same pin-from-source discipline).
- M8.7 per-adapter validation runbook gains its Steamworks.NET entry
  when the licensed Unity seat lands (M7.4 block); live validation
  needs the Steam client running.
- The advertised-membership set only grows during a session; a
  room-membership-driven expiry is a candidate refinement if live
  validation shows it matters.
