# Server Conformance

The client is validated against the server's client-author conformance
checklist (`docs/guides/building-a-client.md` in the server repo). Each
scenario runs the real client stack — `SignalFishPollingClient` over
`WebSocketTransport` — against the live server in open mode.

Run it locally with Docker: `pwsh -NoProfile -File scripts/run-e2e.ps1`.
CI runs the same suite whenever a PR or main push touches the client or
the suite (`e2e.yml`, service container). The full checklist passes
against the live server: items 1-6 and the client-side half of item 7
were first verified on PR #44's `e2e` run; the bidirectional partition
drill completes item 7 (first verified on this PR's `e2e` run).

## Checklist status

Checked items are covered by `tests/SignalFish.Client.E2E`
(`ServerConformanceTests`).

- [x] **Handshake policy + join + relay** — open-mode optional
      `Authenticate` first (answered by `Authenticated` + `ProtocolInfo`),
      room create + join, `GameData` round-trip in both directions with
      sender identity, clean `LeaveRoom`.
- [x] **Two-player lobby + start** — both players ready, `all_ready`
      observed, `StartGame`, both seats see `GameStarting`.
- [x] **StartGame rejection** — `GAME_START_NOT_READY` for a premature
      authority start; `GAME_START_FORBIDDEN` for a non-authority start in
      an authority room; both surfaced as typed `error_code` data.
- [x] **`all_ready` invalidation** — a later joiner (unready, no corrective
      broadcast) invalidates a cached `all_ready: true`, the next start
      fails with `GAME_START_NOT_READY`, and the lobby recovers once the
      joiner readies.
- [x] **Error handling** — `ROOM_FULL` on a join past the room ceiling;
      `ROOM_NOT_FOUND` on a spectator join to a missing room.
- [x] **Heartbeat** — a client pinging every second outlives the server's
      ping timeout in silence and still relays afterwards.
- [x] **Directional liveness** — both halves through an in-process TCP
      proxy (`PartitionProxy`): the silent-client half (a client that
      stops writing is dropped by the server's reapers), a severed
      client→server direction ending in the server's typed liveness close
      (the server's diagnosis reaches the client through the still-open
      reverse direction — never a synthetic 1006), and a severed
      server→client direction that keeps carrying outbound relay
      end-to-end until the client's own liveness clock declares death
      (local 1006). Reconnect-after-partition lands with M4.4
      reconnection.
- [ ] Reconnect, v3 negotiation, v3 dynamics, v3 delivery classes, v3 gap
      lifecycle — land with their milestones (M4.4+, M6). Floor note: the
      reconnection token rides `RoomJoined`/`Reconnected` only on v3+
      deployments (the v2 wire omits it — `docs/concepts/reconnection.md`
      in the server repo), so the *live-server* reconnection scenario
      waits for the v3 E2E work (M6.6); the v2-floor procedure is fully
      covered by the golden-driven integration tests.
