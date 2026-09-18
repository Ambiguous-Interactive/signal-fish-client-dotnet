---
name: json-serialization
description: Serialize and deserialize Signal Fish envelopes with System.Text.Json on netstandard2.1 (type/data envelope decoding, snake_case mapping, forward compatibility, optional MessagePack game data). Use when adding message types, touching the JSON layer, or debugging deserialization failures.
metadata:
  category: protocol
---

# JSON Serialization

## Envelope decoding strategy

The envelope is adjacently tagged: `{ "type": "...", "data": {...} }`.
Decode in two passes:

1. Parse the frame into a small shim: `record Envelope(string Type, JsonElement Data);`
2. Switch on `Type` (a `switch` expression over known PascalCase constants)
   and deserialize `Data` into the concrete record.

Never deserialize the entire envelope into one giant type; never use
inheritance-based polymorphic serializers for this — they fight the
additive protocol and IL2CPP.

## Casing rules

- Discriminator `type` values are **PascalCase** strings exactly as the
  server sends them (`"JoinRoom"`).
- Payload fields are **`snake_case`**. Map with `[JsonPropertyName("game_name")]`
  per property. Do **not** rely on `PropertyNamingPolicy = SnakeCaseLower`
  (unavailable on netstandard2.1's System.Text.Json versions and easy to
  get subtly wrong); attributes are explicit and greppable.

## Options discipline

- One static, preallocated `JsonSerializerOptions` per context (inbound,
  outbound). Never allocate options per call (it nukes caching).
- `PropertyNameCaseInsensitive = false` — the attribute map is the contract.
- Numbers: server sends counters as JSON numbers (`seq`, `key`); keep them
  unsigned-capable (`ulong` for `epoch`) where the spec says so.

## Forward compatibility (mandatory)

- Unknown `type` → `UnknownMessageReceived` event, never an exception.
- Unknown fields inside a known message → ignored (default System.Text.Json
  behavior; do NOT add `JsonUnmappedMemberHandling.Disallow`).
- Optional server fields are nullable/omittable in our records.

## MessagePack (optional, negotiated)

Game data payloads may use MessagePack when the client negotiated
`game_data_formats: ["message_pack"]`. Treat it as a payload codec only —
the control-plane envelope stays JSON. Any MessagePack dependency is
optional and must pass the [unity-compatibility](../unity-compatibility/SKILL.md)
bar (IL2CPP-safe, netstandard2.1).

## IL2CPP notes

- Reflection-based serialization works under IL2CPP only when types are not
  stripped: document `link.xml` preservation for `SignalFish.Client.Protocol.*`.
- No source generators (`JsonSerializerContext`) as a hard dependency —
  Unity compilation support is not guaranteed across versions.

## Debugging deserialization

1. Capture the raw frame text first; diagnose from bytes, not assumptions.
2. Check casing: PascalCase `type`, snake_case fields.
3. Check nullability: `data` may be absent for payload-less messages.

## Related Skills

- [protocol-messages](../protocol-messages/SKILL.md) - the message catalog and floor
- [unity-compatibility](../unity-compatibility/SKILL.md) - IL2CPP/stripping constraints
- [create-test](../create-test/SKILL.md) - golden wire sample tests
