# Protocol Versioning

The Signal Fish protocol has two generations: the **v2 JSON relay floor**
and **v3**. Negotiation is additive, opt-in, and cap-down — a client that
opts into nothing sends byte-identical v2 handshake bytes and stays on the
relay floor, while a server older than the client can only negotiate
downward, never upward.

## What v3 adds

Everything on the v2 floor keeps working unchanged. v3 layers on top of
it (all names from `SignalFish.Client.Protocol.MessageKind`):

- **Raw binary game data** — a second relay lane: raw WebSocket binary
  frames (never the JSON envelope) carrying a strict MessagePack map
  (`from_player` as the 16 RFC-4122 UUID bytes, `encoding`, opaque
  `payload`, plus the server-stamped `seq`/`epoch`). Binary game data is
  always reliable and shares the sender's per-room sequence stream with
  their JSON frames (`BinaryGameDataFrame`).
- **Classified delivery** — the `class`/`key` metadata on JSON
  `GameData` (`reliable`/`latest`/`volatile`) with per-class delivery
  accounting: `DeliveryReport`, `RelayStats`, and the `GoingAway` drain
  notice. See [Delivery](delivery.md).
- **Mesh signaling** — `SessionPlan`, `NewPeer`,
  `PeerTransportStatus`, the bidirectional `Signal` relay, and the
  client's `TransportStatus` report, folded by `MeshSession`. Also v3:
  `RoomOperation`/`RoomOperationResult` (room admin operations with a
  client-generated `operation_id`).

## Negotiation flow

Negotiation is one round trip layered onto the existing handshake:

1. The client advertises in `Authenticate`. The wire field is
   `protocol_version` (the highest version it speaks); absent means the
   endpoint default. `AuthenticateMessage` also carries the v3
   negotiation fields `supported_transports`,
   `supported_topologies`, and additive `requested_capabilities`
   (unknown tokens are ignored by the server).
2. The server caps the advertised version at its ceiling and echoes the
   result in `ProtocolInfo` (`ProtocolInfoMessage`): `protocol_version`
   (the client's advertisement capped down), `min_protocol_version`,
   `max_protocol_version`, `transports`,
   `max_outbound_message_size`, `implementation_version` (the exact
   server release, so a session can pin the deployment it tested
   against), and `game_data_limits` (per-encoding payload ceilings,
   present only when the deployment configures caps). A v2 negotiation
   omits these fields entirely; an explicit JSON `null` decodes as
   absent. The always-present `capabilities` and `game_data_formats`
   lists remain.
3. The client stores the echo verbatim: the `ProtocolInfo` session event
   sets `NegotiatedProtocolVersion` in the state machine and on the
   client snapshot. A re-echo replaces; the value is per connection and
   cleared at teardown.

### When the server is older

If the server never sends the extended fields — a v2 deployment, or a
pre-negotiation `ProtocolInfo` — the negotiated version is `null` and the
client stays on the v2 relay floor. Nothing upgrades: the client keeps
emitting the v2 wire forms and refuses its own v3-only sends locally (see
below). The floor is also the SDK default:

```csharp
// v2 relay floor — the default. Handshake bytes are byte-identical
// to a v2 client because protocol_version is omitted.
var options = new SignalFishClientOptions();
```

Setting `SignalFishClientOptions.ProtocolVersion` advertises on every
automatic-reconnect `Authenticate`; mirror the same value in your
explicit first `SendAuthenticate` — a revived round advertising less
would silently renegotiate down (a v3 session would land on the v2
floor).

## The send gate

Both clients enforce the negotiated version at admission time, before
anything reaches the wire (`SignalFishStateMachine.RequiresNegotiatedV3`).
On a connection that has not negotiated v3 — including the window before
`ProtocolInfo` arrives — these sends are refused locally with
`CommandSend.Accepted == false` and `CommandSend.Refusal ==
AdmissionError.ProtocolUnsupported`:

| Send | Refused on v2 because |
|------|-----------------------|
| `SendGameData` with `GameDataClass.Latest` or `GameDataClass.Volatile` | Classified delivery is v3-only. |
| `SendBinaryGameData` | The raw binary lane is v3-only (and additionally requires a non-JSON negotiated encoding — `AdmissionError.BinaryFormatNotNegotiated`). |
| `SendSignal` | Mesh signaling is v3-only (plus the session-plan gates). |
| `SendTransportStatus` | Mesh signaling is v3-only. |

Never gated: `SendGameData` with `GameDataClass.Reliable` — it
reproduces the v2 wire form — and `SendProvideConnectionInfo`, which
stays on the v2-compatible floor. The version refusal is checked last:
membership and role verdicts take precedence (Rust parity), so once
those are satisfied `ProtocolUnsupported` describes the remaining
failure precisely.

## Checking the version at runtime

Both clients expose the negotiated version on the coherent snapshot:

```csharp
uint? version = client.Snapshot.NegotiatedProtocolVersion;
// non-null  : ProtocolInfo arrived and negotiated that version.
// null      : ProtocolInfo has not arrived yet, or negotiated v2.
```

| API | Meaning |
|-----|---------|
| `SignalFishClientOptions.ProtocolVersion` | The advertisement (`uint?`, default `null` = omit the field). |
| `ClientSnapshot.NegotiatedProtocolVersion` | The server's cap-down echo; `null` before `ProtocolInfo` or on a v2 negotiation. |
| `ProtocolInfoMessage.ProtocolVersion` | The negotiated version as the wire carried it. |
| `ProtocolInfoMessage.MinProtocolVersion` / `MaxProtocolVersion` | The deployment's supported range. |
| `ProtocolInfoMessage.Transports` | The server-side data-path transport tokens. |
| `ProtocolInfoMessage.MaxOutboundMessageSize` | The server's outbound per-message byte bound (the client's receive bound; probe fallback default 8 MiB). |
| `PollEventKind.ProtocolInfo` | The event that carries the payload on both clients. |

Write feature detection against the send verdicts, not against a cached
version: branch on `CommandSend.Refusal ==
AdmissionError.ProtocolUnsupported` and treat it as "wait for
`ProtocolInfo`, or the endpoint is v2-only".

## Conformance coverage

The live-server conformance suite pins the behavior described here:
`V3NegotiationEchoesTheCappedDownResult` (the `/v2` endpoint keeps the
relay floor, a `/v3` advertisement receives the capped-down result),
`V3DeliveryClassesRoundTripWithClassSurfaced`,
`BinaryGameDataRoundTripsOnALiveV3Room`, and the mesh scenarios in
`MeshConformanceTests` (webrtc session with relay fallback, host
topology, host failover, and the explicit relay-floor plan). See
[Server Conformance](conformance.md).

## See also

- [Delivery](delivery.md) — the v3 delivery classes, reports, and
  accountability.
- [Client API](client-api.md) — the send methods and `CommandSend`
  verdicts.
- [Polling client](polling-client.md) — the same gates on the
  single-threaded surface.
- [Errors](errors.md) — `AdmissionError` and the wire error codes.
- [Events](events.md) — the v3 event kinds.
