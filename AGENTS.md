# AGENTS.md

Project context for AI agents working on the Utility solution.

## Solution layout

`Utility.slnx` at the repo root. Projects under `src/`:

- `src/Utility.UI` — WPF desktop app (Finder, DropBox and Media library tabs)
- `src/Utility.Server` — server-side services and SQL (`src/Utility.Server/Sql/*.sql`, hand-applied)
- `src/Utility.DropBox` — Dropbox sync walk

Verification tools live under `tools/` (`groups-checks`, `repair-checks`).

## Agent skills

### Issue tracker

GitHub issues in `j124650/Utility`, via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical roles, label strings equal to their names. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `GLOSSARY.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.

## Project skills

Matt Pocock's engineering skills live in `.hermes/skills/` — the Hermes project tier, which
loads only for sessions started inside this repo and only when the repo is trusted
(`hermes skills trust`). They are a local toolchain choice, not project source: `.hermes/`
is gitignored, so a fresh clone has none of them.

Where they overlap the Hermes built-ins, the built-ins are disabled in the `developer`
profile (`test-driven-development`, `requesting-code-review`, `spike`, `systematic-debugging`,
`spec-writing`, `grounded-citations`, `hermes-agent-skill-authoring`, `simplify-code`).



## Conciseness
DO NOT leak our conversation, prompt, or iteration history into code comments, pull request descriptions, or other maintainer-facing prose.

## About restrictions
A restriction is a conclusion you earn from a real failure, not a field you read. Never report an operation as blocked, unavailable, or not-permitted based on a metadata flag, a config field, or a docs claim. Attempt it and quote the actual error. If you genuinely cannot attempt it, say "not attempted", never "we can't