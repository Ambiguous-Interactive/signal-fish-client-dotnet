---
name: reflect-improve
description: Mandatory post-work retrospective and self-improvement loop - analyze a completed change, root-cause problems, and fold findings back into skills, references, context rules, and the improvement log. Use after finishing any large feature, refactor, protocol or infra change, a debugging effort that took several attempts, or when the user asks to reflect.
metadata:
  category: core
---

# Reflect and Improve

Weights are never updated here — the knowledge system is. Every substantial
piece of work ends by converting what happened into durable, retrievable
knowledge (verbal-reinforcement learning, Reflexion-style), gated by
self-verification (Voyager-style: only verified knowledge enters the
library).

## When this is mandatory

Run the full loop when any of these is true:

- A new feature or module, or a public API surface change.
- A protocol, serialization, transport, or packaging change.
- Build, test-infrastructure, or `.llm` system changes.
- Debugging took three or more failed attempts, or needed a user correction.
- The change spans more than ~10 files or ~300 changed lines.
- The user asks for reflection, or the same friction recurred.

When in doubt, run the lightweight pass: steps 1-2 plus one honest log
entry. Silently skipping a qualifying change violates
[context.md](../../context.md) rule 15.

## The loop

### 1. Evidence (data-backed)

Collect before judging; every finding must cite an artifact, not memory:

- `git diff --stat` or the file list; build/test output; the failing
  command and its error text.
- The attempts ledger: what was tried, in order, what failed, and why.

### 2. Root cause (blameless)

For each problem and near-miss, ask "why" until the answer is a systemic
gap: missing knowledge, a wrong or missing procedure, a missing guard, or
a deficient tool. "Be more careful" is not a root cause.

### 3. Triage (route each finding to its artifact)

| Finding | Destination |
| --- | --- |
| Reusable multi-step technique or procedure | New/updated skill via [manage-skills](../manage-skills/SKILL.md) |
| Facts, limits, measurements, upstream behavior | A file in `.llm/references/` |
| Invariant that must always hold repo-wide | Critical rule in [context.md](../../context.md) |
| Gap in this loop or in skill management | Edit this skill or [manage-skills](../manage-skills/SKILL.md) |
| Failed approach worth remembering | Negative-result entry in [improvement-log.md](../../improvement-log.md) |
| Nothing of value | A log entry saying exactly that (honest null result) |

Never restate an existing skill — extend or cross-link it instead.

### 4. Red-green gate (verify before promotion)

A finding may enter a durable artifact (skill, reference, rule) only with:

- **RED**: the concrete failure it prevents, reproduced or cited from this
  session (file:line, error text, measurement).
- **GREEN**: proof the artifact fixes it — a passing test, a command that
  now succeeds, or a before/after measurement.

Unverified findings stay in [improvement-log.md](../../improvement-log.md)
as `Open` items. Promoting them on faith is how folklore pollutes context.

### 5. Apply

Create or edit the artifacts, then run the manage-skills workflow: size
lint, index regeneration, instruction lint, and
`pwsh -NoProfile -File scripts/tests/run-all.ps1`.

### 6. Log

Append to [improvement-log.md](../../improvement-log.md), newest first: a
dated `## YYYY-MM-DD - <scope>` header plus Trigger / Evidence / Findings /
Applied / Open bullets. This is the loop's episodic memory — small, dated,
high-signal structured notes.

## Log discipline

- The log is staging, not storage. Once an entry's knowledge has graduated
  into durable artifacts and `Open` is empty, prune the entry.
- Target under ~150 lines; the 300-line lint ceiling is the hard bound.

## When NOT to Use

- Trivial one-file fixes with no friction — skip silently, no entry needed.
- Storing unevidenced "nice to know" trivia — do not write it down.
- Protocol facts — they belong in
  [protocol-quick-reference](../../references/protocol-quick-reference.md).

## Related Skills

- [manage-skills](../manage-skills/SKILL.md) - authoring and validating skills
- [create-test](../create-test/SKILL.md) - turn RED findings into regression tests
- [api-design](../api-design/SKILL.md) - promoted techniques must respect it
