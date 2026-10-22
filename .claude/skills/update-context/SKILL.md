---
name: update-context
description: Record something learned about this codebase - add or change a rule in a CLAUDE.md, write a new skill, or move a fact to the right file. Use when you had to re-derive something the context should already have told you.
---

# Updating the project context

Run this the moment you learn something, not later. Rot comes from knowing something and not writing it down
while you still know why it mattered.

## 1. Route the fact

| A fact that… | goes in |
| --- | --- |
| constrains how an edit must be written — an invariant, a forbidden import, a unit, a required companion edit | one imperative line in that directory's `CLAUDE.md` |
| is a procedure you would otherwise re-derive — commands, flags, a multi-file checklist | a skill in `.claude/skills/` |
| is *why*, or a number worth citing | `docs/design.md`, or `README.md` if user-facing |
| is true only today — a bug, a TODO, an idea | `docs/backlog.md` or a code comment. **Never a `CLAUDE.md`** |

That last row is the one that matters. A `CLAUDE.md` that accumulates status notes becomes a changelog, and a
changelog is not read.

## 2. Respect the caps

Root `CLAUDE.md` ≤ 60 lines · each directory file ≤ 40 · each skill body ≤ 120.

**If your addition breaks the cap, delete something first and say which line you removed and why.** Without
this, appending always beats triage and the files grow until nobody reads them.

## 3. Write it in the house format

One imperative line. **Every rule names what catches a violation** — a command, a test, a file — or is marked
`(unenforced)`. If you cannot say what catches it, you are writing prose, and prose belongs in `docs/`.

Prefer making the rule enforceable over writing it down: a check added to `tests/layering.test.ts` is worth
more than a paragraph nobody reads. See `templates/rule.md`.

## 4. Adding a new skill

Copy `templates/skill.md` to `.claude/skills/<name>/SKILL.md`. The `description` is what decides whether the
skill ever loads, so write it as **the phrases someone would actually type** ("screenshot the app", "debug a
black screen"), not as a summary of the contents. Add a `paths:` glob if it belongs to one layer.

## 5. Adding a new directory CLAUDE.md

Only when the directory owns a real layer *and* has at least one rule its parent does not. Remember the whole
ancestor chain loads, so a file that merely repeats the root file costs tokens on every edit and earns none.
Never use `@path` imports — they expand eagerly at startup and defeat lazy loading.

## 6. Close the loop

Check that the root `CLAUDE.md` router still lists every directory file that exists, and nothing that doesn't.
