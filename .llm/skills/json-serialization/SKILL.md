---
name: json-serialization
description: Implement Signal Fish JSON (de)serialization with the hand-rolled zero-dependency UTF-8 codec (two-pass envelope decode, static snake_case field tokens, span-based escape-aware writer, fuzz-safe totality). Use when adding message types, touching the JSON layer, or debugging decode failures.
metadata:
  category: protocol
---

# JSON Serialization

The JSON layer is a **hand-rolled UTF-8 codec**: zero NuGet dependencies,
zero reflection. This supersedes all System.Text.Json guidance —
netstandard2.1 has no STJ and Unity lacks it (see
[unity-compatibility](../unity-compatibility/SKILL.md)). Do not add a JSON
package.

## Why hand-rolled

- netstandard2.1 + Unity 2021.2+ compatibility without shipping a JSON
  library (see [unity-compatibility](../unity-compatibility/SKILL.md)).
- Zero dependencies is a locked project decision; zero reflection keeps
  IL2CPP stripping a non-issue.
- Allocation discipline: no per-call strings, options, or intermediate
  document allocations on hot paths (idle-cost rules in
  [api-design](../api-design/SKILL.md)).

## Envelope decoding (two passes)

The envelope is adjacently tagged: `{ "type": "...", "data": {...} }`.

1. **Pass 1 — scan**: validate the frame's UTF-8/JSON structure; locate the
   envelope braces, the `type` token, and the `data` token; record field
   offsets. No values are decoded yet.
2. **Pass 2 — decode**: switch on `type` (PascalCase constants) and decode
   `data` into the concrete `readonly struct` for that message using the
   recorded offsets.

Never decode the entire envelope into one giant type; never use
inheritance-based polymorphic deserialization — it fights the additive
protocol and IL2CPP.

## Field tokens

- Payload fields use **static `snake_case` token constants** (precomputed
  UTF-8 byte spans) compared directly during scans — no per-call string
  allocations, no naming-policy magic.
- Discriminator `type` values are **PascalCase** strings exactly as the
  server sends them (`"JoinRoom"`).
- Numbers: server sends counters as JSON numbers (`seq`, `key`); keep them
  unsigned-capable (`ulong` for `epoch`) where the spec says so.

## Writing frames

- The writer is span-based: an escape-aware UTF-8 writer over byte spans
  (`ReadOnlySpan<byte>` in, `Span<byte>` out); it never emits invalid UTF-8.
- Each outbound message gets a **static per-message field writer**; no
  intermediate DOM, no allocations on hot paths.
- Outbound frames must be byte-identical to the golden fixtures — asserted
  by tests (see [create-test](../create-test/SKILL.md)).
- The writer is a `ref struct` with mutable position state (`_span`/`_pos`).
  **Always pass it `ref`** into helper methods that write through it —
  by-value passing compiles clean, mutates only the copy, and silently
  clobbers flushed buffer regions when the caller keeps writing (found the
  hard way in M1.3; only frames with helper writes after a flush boundary
  failed).
- **Never `stackalloc` inside a write loop** — frame memory accumulates
  per iteration and the resulting StackOverflowException is uncatchable.
  Hoist one scratch span above the loop and reuse it.
- `Try*` readers that assign `out` params eagerly must not be composed with
  `||` when the failure-path value is observable — branch explicitly
  instead.

## Decoding UUID text

Hand-rolled UUID text decode (`TryReadGuid`) has three trap classes when
pinning parity against `Guid.Parse`:

- **Field big-endianity**: the first three groups are MSB-first in text but
  serialize little-endian into the binary `Guid` — do not copy bytes
  verbatim.
- **Group boundaries**: 8-4-4-4-12 with hyphens at offsets 8/13/18/23 — the
  group after the third hyphen starts at 19, not 18.
- **Error-sentinel aliasing**: a `-1` failure sentinel can alias an
  all-FFFF field — validate each hex pair individually, never OR combined
  signed ints.

Required-field enforcement needs per-field seen flags — `default(Guid)` is
a valid value; absence is not.

## Forward compatibility (mandatory)

- Unknown `type` → `UnknownMessage` event, never an exception.
- Unknown fields inside a known message → skipped by the scanner.
- Optional server fields are nullable/omittable in our structs.

## Fuzz discipline (totality)

- The reader is **total**: malformed input yields a bounded error event
  (`DecodeFailed`-style), never an unbounded throw. Enforce depth/size
  bounds; treat truncation and over-deep nesting as decode errors.
- The writer never emits invalid UTF-8 for any struct input.
- These two invariants are SharpFuzz targets and FsCheck properties — see
  [create-test](../create-test/SKILL.md).

## MessagePack (payload only, negotiated)

Game data payloads may negotiate `game_data_formats: ["message_pack"]`.
The control-plane envelope stays JSON. The MessagePack payload codec is out
of scope for 0.1.0; if added later it must pass the zero-dependency and
[unity-compatibility](../unity-compatibility/SKILL.md) bar.

## Debugging decode failures

1. Capture the raw frame bytes first; diagnose from bytes, not assumptions.
2. Check casing: PascalCase `type`, snake_case fields.
3. Check bounds: truncated frames and over-deep nesting surface as a
   bounded decode error, not a crash.
4. One frame can pass a loose decoder and fail a stricter one. Layered
   decode (a mapper validating with one type, a consumer re-decoding
   with the full typed decoder) means "the fact decoded" does not imply
   "the typed re-decode succeeds" — the join decoder ignores
   `sender_watermarks`; the reconnect decoder validates them. Never let
   an ignored `TryDecode` result feed a `default` struct into
   authoritative logic: `default` of a message struct carries null
   reference members (the constructor's null-coalescing never ran).
   Guard the re-decode and refuse the frame as the routed-fact wire
   violation.

## Related Skills

- [protocol-messages](../protocol-messages/SKILL.md) - the message catalog and floor
- [unity-compatibility](../unity-compatibility/SKILL.md) - IL2CPP/stripping constraints
- [create-test](../create-test/SKILL.md) - golden wire fixture tests
