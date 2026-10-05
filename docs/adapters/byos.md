# Bring Your Own Stack

Most engines already have a netcode. The BYOS template (M8.4) puts one
on a Signal Fish room **without an adapter**: the room owns the parts it
is good at — matchmaking, membership, heartbeats, reconnection-ready
sessions — and your own stack keeps owning the connection. There is no
UPM package here; this page is the sample. Copy the two components, wire
them to your listener/connect calls, and you have a room-backed session.

The choreography is two moves, both over the room you already have:

- The **host publishes its endpoint** with
  `SendProvideConnectionInfo`. The server repeats it to peers, and it
  lands on their `GameStarting` event's peer connections — no extra
  lane, no out-of-band channel.
- **Clients echo the room join code** over the game-data lane as a tiny
  JSON envelope. The host sees the echo arrive from a live room member
  (`GameData` carries the sender's player id) and only then lets its
  engine accept that peer — membership is the connection gate, the same
  rule the NGO coordinator enforces with connection approval.

Everything rides the v2 floor, so this works before and without a v3
negotiation.

## The flow

```text
host                                  client
----                                  ------
join room ──────────────────────────► join room (by code)
take authority
start your listener
publish endpoint (ProvideConnectionInfo)
                                      GameStarting: read the authority's endpoint
                                      echo the join code (GameData)
echo seen from a member ─────────────► your connect call goes out
your listener accepts (gate: roster + echo)
```

The gate has two inputs, both already tracked by the room: the roster
(`PlayerJoined` / `PlayerLeft` keep it current) and the echo set (a
player id lands there when its join-code echo arrives). A peer must be
in both — a room member who never echoed has not proven it is the one
connecting, and an echo from a player who left is stale.

## The join-code echo envelope

One JSON property, matched by its exact quoted name, so the game's own
game-data traffic passes through untouched. The envelope accepts room
codes in `[A-Za-z0-9_-]` (≤ 128 chars) — the template's own constraint,
not a server promise — which lets the scan stay a byte scan, no JSON
parser, Unity-safe. If a deployment hands out codes outside that
charset, the host validates its code at start and fails loudly rather
than running a gate that can never open:

```csharp
using System;
using System.Text;

/// <summary>
/// The BYOS membership echo: { "signal_fish_room_join_code": "<code>" }.
/// A foreign or malformed payload decodes as absent, never as an error.
/// </summary>
public static class RoomJoinCodeEnvelope
{
    public const string PropertyName = "signal_fish_room_join_code";

    public const int MaxRoomCodeLength = 128;

    public static bool IsValidRoomCode(string? roomCode)
    {
        if (string.IsNullOrEmpty(roomCode) || roomCode.Length > MaxRoomCodeLength)
        {
            return false;
        }

        foreach (char c in roomCode)
        {
            if (!(c >= 'a' && c <= 'z')
                && !(c >= 'A' && c <= 'Z')
                && !(c >= '0' && c <= '9')
                && c != '-'
                && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryWrite(string roomCode, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (!IsValidRoomCode(roomCode))
        {
            return false;
        }

        payload = Encoding.UTF8.GetBytes(
            "{\"" + PropertyName + "\":\"" + roomCode + "\"}"
        );
        return true;
    }

    public static bool TryRead(ReadOnlySpan<byte> data, out string roomCode)
    {
        roomCode = string.Empty;
        byte[] key = Encoding.ASCII.GetBytes("\"" + PropertyName + "\"");
        int keyStart = data.IndexOf(key);
        while (keyStart >= 0)
        {
            int scanner = keyStart + key.Length;
            if (TryReadValue(data, ref scanner, out string candidate))
            {
                roomCode = candidate;
                return true;
            }

            int next = data.Slice(scanner).IndexOf(key);
            keyStart = next < 0 ? -1 : scanner + next;
        }

        return false;
    }

    private static bool TryReadValue(ReadOnlySpan<byte> data, ref int scanner, out string value)
    {
        value = string.Empty;
        while (scanner < data.Length && IsSpace(data[scanner])) { scanner++; }
        if (scanner >= data.Length || data[scanner] != (byte)':') { return false; }
        scanner++;
        while (scanner < data.Length && IsSpace(data[scanner])) { scanner++; }
        if (scanner >= data.Length || data[scanner] != (byte)'\"') { return false; }
        scanner++;
        int start = scanner;
        while (scanner < data.Length && data[scanner] != (byte)'\"')
        {
            if (scanner - start >= MaxRoomCodeLength) { return false; }
            scanner++;
        }

        if (scanner >= data.Length || scanner == start) { return false; }

        value = Encoding.ASCII.GetString(data.Slice(start, scanner - start));
        return IsValidRoomCode(value);
    }

    private static bool IsSpace(byte b)
    {
        return b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n';
    }
}
```

## The host component

The host runs the room session, publishes its listener's endpoint, and
exposes the gate its listener asks before accepting a peer:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SignalFish.Client.Async;
using SignalFish.Client.Core;
using SignalFish.Client.Polling;
using SignalFish.Client.Protocol;
using SignalFish.Client.Transport;
using UnityEngine;

public sealed class ByosHost : MonoBehaviour
{
    [SerializeField]
    private string endpoint = "ws://127.0.0.1:3536/v2/ws";

    [SerializeField]
    private string gameName = "byos-game";

    [SerializeField]
    private string playerName = "host";

    // What your listener actually binds; published verbatim as the
    // connection-info JSON {"type": "direct", "host": ..., "port": ...}.
    [SerializeField]
    private string listenHost = "127.0.0.1";

    [SerializeField]
    private ushort listenPort = 7777;

    private SignalFishClient? room;

    // The bootstrap owns the event queue until it finishes: Update() must
    // not drain while StartAsync is awaiting room events, or the two
    // consumers steal each other's frames.
    private volatile bool ready;

    private readonly HashSet<Guid> roster = new HashSet<Guid>();

    private readonly HashSet<Guid> cleared = new HashSet<Guid>();

    public string? JoinedRoomCode { get; private set; }

    /// <summary>
    /// The connection gate. Ask this from your listener's accept path;
    /// <paramref name="playerHint"/> is whatever your stack passes at
    /// connect time — pass the peer's Signal Fish player id and the gate
    /// closes on the room's truth.
    /// </summary>
    public bool IsConnectionCleared(Guid playerHint)
    {
        return roster.Contains(playerHint) && cleared.Contains(playerHint);
    }

    public async Task StartAsync(string? roomCode = null, CancellationToken ct = default)
    {
        room = new SignalFishClient(
            new WebSocketTransport(),
            SystemClock.Instance,
            new SignalFishClientOptions()
        );

        /*
            A bootstrap that throws must not leave a live room session
            nobody drains: Update() stays gated off until ready, so the
            failure path tears the session down itself.
        */
        try
        {
            await room.ConnectAsync(new Uri(endpoint), ct);
            ThrowIfRefused(room.SendAuthenticate(new AuthenticateMessage()));
            await ExpectAsync(room, PollEventKind.Authenticated, ct);
            ThrowIfRefused(
                room.SendJoinRoom(
                    new JoinRoomMessage(gameName, playerName, roomCode, supportsAuthority: true)
                )
            );
            PollEvent joined = await ExpectAsync(room, PollEventKind.RoomJoined, ct);
            JoinedRoomCode = joined.Membership.RoomCode;
            if (!RoomJoinCodeEnvelope.IsValidRoomCode(JoinedRoomCode))
            {
                /*
                    The echo can never encode this code; fail now rather
                    than run a gate that can never open.
                */
                throw new InvalidOperationException(
                    "The server's room code does not fit the echo envelope's charset."
                );
            }

            ThrowIfRefused(room.SendAuthorityRequest(becomeAuthority: true));
            PollEvent granted = await ExpectAsync(room, PollEventKind.AuthorityResponse, ct);
            if (!granted.AuthorityResponse.Granted)
            {
                throw new InvalidOperationException(
                    "The room denied the authority request: "
                        + (granted.AuthorityResponse.Reason ?? "unspecified")
                        + "."
                );
            }

            /*
                Your listener starts here, before the endpoint is
                published: a client that reads the endpoint must find a
                live listener.
            */
            // StartYourListener(listenHost, listenPort);

            PublishEndpoint();
            ready = true;
        }
        catch
        {
            TearDown(reason: null);
            throw;
        }
    }

    private void Update()
    {
        SignalFishClient? live = room;
        if (live is null || !ready)
        {
            return;
        }

        while (live.TryDequeueEvent(out PollEvent pollEvent))
        {
            switch (pollEvent.Kind)
            {
                case PollEventKind.PlayerJoined:
                    roster.Add(pollEvent.PlayerJoined.Player.Id);
                    PublishEndpoint();
                    break;
                case PollEventKind.PlayerReconnected:
                    roster.Add(pollEvent.LeftPlayerId);
                    PublishEndpoint();
                    break;
                case PollEventKind.PlayerLeft:
                    roster.Remove(pollEvent.LeftPlayerId);
                    cleared.Remove(pollEvent.LeftPlayerId);
                    break;
                case PollEventKind.GameData:
                    if (
                        RoomJoinCodeEnvelope.TryRead(
                            pollEvent.GameData.Payload.Span,
                            out string code
                        )
                        && code == JoinedRoomCode
                    )
                    {
                        cleared.Add(pollEvent.GameData.FromPlayer);
                    }

                    break;
                case PollEventKind.AuthorityChanged:
                    /*
                        An authority loss orphans the published endpoint:
                        the room now speaks with another host. Fail loudly
                        rather than serving a stale address.
                    */
                    TearDown("The room's authority moved; re-host the room.");
                    return;
                case PollEventKind.Disconnected:
                    TearDown("The room connection closed.");
                    return;
            }
        }
    }

    private void OnDestroy()
    {
        TearDown(reason: null);
    }

    /// <summary>
    /// Re-publishes on joins too: the server's stored info stays fresh
    /// for every member the server next fan-outs to. Delivery itself
    /// rides the server's GameStarting emission — the client-side wait
    /// is where that timing lives.
    /// </summary>
    private void PublishEndpoint()
    {
        string json =
            "{\"type\": \"direct\", \"host\": \"" + listenHost
            + "\", \"port\": " + listenPort + "}";
        ThrowIfRefused(
            room!.SendProvideConnectionInfo(
                new ProvideConnectionInfoMessage(Encoding.UTF8.GetBytes(json))
            )
        );
    }

    private async Task<PollEvent> ExpectAsync(
        SignalFishClient client,
        PollEventKind expected,
        CancellationToken ct
    )
    {
        long deadline = SystemClock.Instance.ElapsedMilliseconds + 10_000;
        while (SystemClock.Instance.ElapsedMilliseconds < deadline)
        {
            ct.ThrowIfCancellationRequested();
            while (client.TryDequeueEvent(out PollEvent current))
            {
                if (current.Kind == expected)
                {
                    return current;
                }

                /*
                    A dead session must fail the wait now, not at the
                    deadline: Disconnected and the failure events all
                    throw here.
                */
                ThrowForFailure(current);
                if (current.Kind == PollEventKind.Disconnected)
                {
                    throw new InvalidOperationException(
                        "The room connection closed while waiting for " + expected + "."
                    );
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for " + expected + ".");
    }

    private void TearDown(string? reason)
    {
        SignalFishClient? live = room;
        room = null;
        ready = false;
        roster.Clear();
        cleared.Clear();
        JoinedRoomCode = null;
        if (live is not null)
        {
            _ = DisposeQuietlyAsync(live);
        }

        // StopYourListener();
        if (reason is not null)
        {
            Debug.LogWarning("[ByosHost] " + reason);
        }
    }

    private static void ThrowIfRefused(CommandSend send)
    {
        if (!send.Accepted)
        {
            throw new InvalidOperationException("The room refused a command: " + send.Refusal + ".");
        }
    }

    private static void ThrowForFailure(in PollEvent pollEvent)
    {
        if (
            pollEvent.Kind == PollEventKind.RoomJoinFailed
            || pollEvent.Kind == PollEventKind.ServerError
        )
        {
            throw new InvalidOperationException(
                "The Signal Fish session failed: " + pollEvent.Failure.Reason + "."
            );
        }
    }

    private static async Task DisposeQuietlyAsync(SignalFishClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) { }
    }
}
```

## The client component

The client joins by room code, reads the authority's endpoint off
`GameStarting`, echoes the code, and hands the endpoint to your connect
call:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using SignalFish.Client.Async;
using SignalFish.Client.Core;
using SignalFish.Client.Polling;
using SignalFish.Client.Protocol;
using SignalFish.Client.Transport;
using UnityEngine;

public sealed class ByosClient : MonoBehaviour
{
    [SerializeField]
    private string endpoint = "ws://127.0.0.1:3536/v2/ws";

    [SerializeField]
    private string gameName = "byos-game";

    [SerializeField]
    private string playerName = "player";

    private SignalFishClient? room;

    // The bootstrap owns the event queue until it finishes: Update() must
    // not drain while StartAsync is awaiting room events, or the two
    // consumers steal each other's frames.
    private volatile bool ready;

    public Guid LocalPlayerId { get; private set; }

    public async Task StartAsync(string roomCode, CancellationToken ct = default)
    {
        room = new SignalFishClient(
            new WebSocketTransport(),
            SystemClock.Instance,
            new SignalFishClientOptions()
        );

        /*
            A bootstrap that throws must not leave a live room session
            nobody drains: Update() stays gated off until ready, so the
            failure path tears the session down itself.
        */
        try
        {
            await room.ConnectAsync(new Uri(endpoint), ct);
            ThrowIfRefused(room.SendAuthenticate(new AuthenticateMessage()));
            await ExpectAsync(room, PollEventKind.Authenticated, ct);
            ThrowIfRefused(
                room.SendJoinRoom(new JoinRoomMessage(gameName, playerName, roomCode))
            );
            PollEvent joined = await ExpectAsync(room, PollEventKind.RoomJoined, ct);
            LocalPlayerId = joined.Membership.PlayerId;

            // The authority's published endpoint arrives with the game start.
            ConnectionEndpoint where = await ExpectAuthorityEndpointAsync(room, ct);

            /*
                The echo is the membership proof the host's gate reads.
                Echo the server's canonical code (this client's
                membership), not the requested string: servers match
                codes case-insensitively, and the host compares
                ordinally.
            */
            if (RoomJoinCodeEnvelope.TryWrite(joined.Membership.RoomCode, out byte[] echo))
            {
                ThrowIfRefused(room.SendGameData(new GameDataMessage(echo)));
            }
            else
            {
                Debug.LogWarning(
                    "[ByosClient] The room code does not fit the echo envelope; "
                        + "the host's gate will refuse this member."
                );
            }

            /*
                Your connect call goes here, after the echo is queued: the
                host may see the echo first, and the gate refuses an engine
                connection it cannot match to a cleared member.
            */
            // ConnectYourStack(where.Host, where.Port, LocalPlayerId);
            ready = true;
        }
        catch
        {
            TearDown(reason: null);
            throw;
        }
    }

    private void Update()
    {
        SignalFishClient? live = room;
        if (live is null || !ready)
        {
            return;
        }

        while (live.TryDequeueEvent(out PollEvent pollEvent))
        {
            if (pollEvent.Kind == PollEventKind.Disconnected)
            {
                TearDown("The room connection closed.");
                return;
            }
        }
    }

    private void OnDestroy()
    {
        TearDown(reason: null);
    }

    private static async Task<ConnectionEndpoint> ExpectAuthorityEndpointAsync(
        SignalFishClient client,
        CancellationToken ct
    )
    {
        long deadline = SystemClock.Instance.ElapsedMilliseconds + 30_000;
        while (SystemClock.Instance.ElapsedMilliseconds < deadline)
        {
            ct.ThrowIfCancellationRequested();
            while (client.TryDequeueEvent(out PollEvent current))
            {
                if (current.Kind == PollEventKind.Disconnected)
                {
                    throw new InvalidOperationException(
                        "The room connection closed before the host's connection info arrived."
                    );
                }

                if (current.Kind != PollEventKind.GameStarting)
                {
                    continue;
                }

                foreach (PeerConnection peer in current.GameStart.PeerConnections)
                {
                    if (peer.IsAuthority && peer.ConnectionInfo is ConnectionEndpoint direct)
                    {
                        return direct;
                    }
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for the host's connection info.");
    }

    /* ExpectAsync, TearDown, ThrowIfRefused, ThrowForFailure and
       DisposeQuietlyAsync match the host component (TearDown clears
       ready with room) — share them in a small base class when you
       paste both in. */
}
```

## Wire contract

| Fact | Value |
| --- | --- |
| Endpoint publication | `SendProvideConnectionInfo` with `{"type": "direct", "host": ..., "port": ...}` (v2-compatible, player role). |
| Where clients read it | `GameStarting` → `PeerConnections[IsAuthority].ConnectionInfo`. |
| Membership echo | `GameData` JSON `{"signal_fish_room_join_code": "<room code>"}`, charset `[A-Za-z0-9_-]`, ≤ 128 chars. |
| Gate inputs | Live roster (player events) **and** a matching echo from that player id. |
| Roles | Players only; spectators have no game-data lane in the v2 floor. |

## Notes and limits

- The endpoint is **self-declared**: the room relays it without
  checking reachability. Publish what your listener actually binds, and
  keep NAT/port-forwarding reality in mind — that part is unchanged
  from any hand-rolled direct connect.
- The host's re-publish on `PlayerJoined` keeps the server's stored
  connection info fresh for whoever the server fan-outs to next.
  Delivery itself rides the server's `GameStarting` emission — that
  timing belongs to the server, and the client's wait is where it shows
  up.
- A member that drops and reconnects re-enters the roster but not the
  cleared set: its echo died with the old connection. The template's
  client has no re-echo path yet — a real game should echo again on
  reconnect (the host's gate reopens on the fresh echo).
- The `playerHint` mapping in `IsConnectionCleared` is the one
  stack-specific seam: your stack must tell you which Signal Fish
  player is at the engine door. Passing the player id as your stack's
  connect token is the smallest honest version.
- WebGL builds have no `ClientWebSocket`; inject the package's browser
  WebSocket transport instead (see [WebGL](../webgl.md)).
- Like every M8 surface, this template is authored against the library's
  conformance suite; live validation in a real engine project is the
  M8.7 runbook item.
