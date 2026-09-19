---
name: manage-skills
description: Create, update, split, or remove agent skills in this repository (.llm/skills/<name>/SKILL.md folders). Use when adding a skill, changing a skill's trigger description, fixing a broken skills index, or when the 300-line limit forces a split.
metadata:
  category: core
---

# Manage Skills

Skills follow the [Agent Skills](https://agentskills.io) standard. This skill
defines the repository-specific rules on top of that standard.

## Skill anatomy

```
.llm/skills/<name>/
└── SKILL.md        Required; YAML frontmatter + markdown body
```

Frontmatter (validated by `scripts/lint-llm-instructions.ps1`):

```yaml
---
name: <name>              # must equal the folder name; lowercase-hyphen; max 64 chars
description: <one line>   # what the skill does + when to use it; max 1024 chars; no '|' or tabs
metadata:
  category: core          # one of: core, protocol, testing
---
```

The `description` is the trigger: agents load skills by matching it against
the task. Write it like a good search query — concrete nouns and verbs
("reconnection token", "WebSocket close codes"), not "helps with stuff".

## Hard rules

1. **300-line hard limit** for every `.llm/**` file (warn at 270). Check
   immediately after editing:
   `pwsh -NoProfile -File scripts/lint-file-sizes.ps1`
2. **`index.md` is generated.** After any skill add/edit/remove:
   `pwsh -NoProfile -File scripts/generate-skills-index.ps1`
3. **Names are kebab-case verb-noun**: `create-test`, `use-pooling`,
   `debug-reconnect`. Folders and `name:` must match exactly.
4. **Body structure** (recommended): `# Title`, `## When to Use`,
   `## When NOT to Use`, content sections, `## Related Skills`.
5. **No duplication**: link to other skills instead of restating them.
   Inline code samples stay under ~20 lines; longer ones belong in
   `.llm/code-samples/`.
6. **Relative links only** between `.llm` files: from a skill use
   `../<other-name>/SKILL.md`, `../../context.md`,
   `../../references/<file>.md`, `../../improvement-log.md`. The link
   checker fails CI on broken links.
7. **Descriptions are single-line ASCII-ish** — no `|`, no tabs, no newlines
   (they break the generated index table).

## Categories

| Category | Meaning |
| --- | --- |
| `core` | Applies to most tasks in this repo (API design, Unity constraints, this meta-skill) |
| `protocol` | Signal Fish wire protocol, transport, serialization, runtime behavior |
| `testing` | Writing and running tests |

## Workflow (do these in order)

1. Create `.llm/skills/<name>/SKILL.md` (or edit an existing one).
2. `pwsh -NoProfile -File scripts/lint-file-sizes.ps1` — fix oversize files
   by splitting into a new skill and cross-linking.
3. `pwsh -NoProfile -File scripts/generate-skills-index.ps1`
4. `pwsh -NoProfile -File scripts/lint-llm-instructions.ps1` — must pass.
5. `pwsh -NoProfile -File scripts/tests/run-all.ps1` — the automation's own
   tests must pass.

The pre-commit hook runs steps 2-4 automatically for staged `.llm` files and
auto-regenerates a stale index; CI re-validates everything on Ubuntu and
Windows.

## Splitting an oversized skill

1. Identify two distinct concerns inside the file.
2. Move one concern into a new skill folder with its own frontmatter.
3. Leave a one-paragraph summary + `Related Skills` link in both directions.
4. Re-run the workflow above.

## Related Skills

- [reflect-improve](../reflect-improve/SKILL.md) - the mandatory retrospective loop that produces most skill updates
- [api-design](../api-design/SKILL.md) - rules that most new content should respect
- [create-test](../create-test/SKILL.md) - when documenting test procedures
