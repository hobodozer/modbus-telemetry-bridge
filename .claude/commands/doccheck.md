---
description: Audit the documentation against what the tree actually does, and report before editing
---

Audit this project's documentation against reality. $ARGUMENTS

Stale documentation is worse than none, because it gets believed and then re-derived at cost. This
has already happened here repeatedly, so start with the automated part and then check what no
script can.

## 1. Automated

```powershell
python tools\repo-check.py --self-test    # the checker's own patterns still match
.\rig.ps1 check                           # escape damage, solution, gitignore, doc paths, claims
```

`--self-test` first and not as ceremony: the schema-chunking pattern was case-sensitive and walked
straight past `CLAUDE.md`'s own false trap while still reporting "ok". A pattern that silently stops
matching is the one failure this whole check cannot survive.

## 2. What the script cannot see

Read each file and compare against the tree, not against memory.

- **`README.md`** — the Status table (every "Not started" row: does the file exist?), the
  project-layout tree (every entry under `tools/` and `src/`), and "Not yet built".
- **`HANDOFF.md`** — the numbered gaps. Any that were closed? A gap left reading as open sends the
  next session to re-solve it. If something is done but unverified, say which.
- **`FINDINGS.md`** — any section your recent work made untrue. Correct it in place; do not append
  a contradiction and leave both.
- **`CLAUDE.md`** — the command list runs? The traps still true? A trap that has been fixed should
  say so, or become a one-line "do not reintroduce" pointing at the guard.
- **Tool help text.** `--help` and `.SYNOPSIS` blocks rot like anything else. `make-hmi-map.py`
  cited an 8 KB ceiling for a day after chunking removed it.

## 3. Verify claims you cannot see from the code

A doc can be wrong in a way the source does not reveal. Check the live rig where cheap:
`.\rig.ps1 health`, `.\rig.ps1 tag <name>`. Register 7 reading 0.1% is what proved GPU collection
worked; the README had said it did not, in four places.

Before writing that a program is running, check `Get-Process`.

## Finish

**Report what you found before editing.** List each item as: what the doc says, what is actually
true, and how you established it. Then fix, and add any new checkable pattern to `CLAIMS` in
`tools/repo-check.py` with its `example` and `counter` so the next drift is caught automatically.
