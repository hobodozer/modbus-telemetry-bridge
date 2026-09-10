---
description: Check, build, commit, push and verify — in the order that actually proves the tree is good
---

Ship the current work. $ARGUMENTS

The order matters and is easy to get wrong. `verify-clone` clones **`origin`**, so running it
before the push validates the *previous* remote and reports success — that happened once in this
project and the green result was believed.

## Sequence

1. **`.\rig.ps1 check`** — static, about a second, no build. Catches a source file `.gitignore`
   excludes, a project missing from the solution, escape-corrupted commands, and documentation
   claiming a shipped feature is unbuilt. Fix anything it reports before going further.

2. **Documentation, in this commit.** Not afterwards — the pass afterwards is the one that never
   happens. Check `HANDOFF.md` (status, tools list, numbered gaps), `README.md` (Status table,
   project-layout tree, "Not yet built"), `FINDINGS.md` (only for things that cost real time; correct
   what your change made untrue rather than appending a contradiction) and `CLAUDE.md` (commands
   and traps). A new file under `tools/` belongs in the README tree and in `ModbusBridge.sln` if it
   is a project.

3. **`.\build.ps1`** — repo-check, build, SimHub plugin, smoke test, store probe, UI self-test. It
   stops a running `ModbusBridge` itself and does not restart it.

4. **Commit.** Say what changed and *why it was wrong before*. If a fix has a non-obvious reason
   for being where it is, that reason belongs in the message — the `onChangeOnly` fix went in the
   recorder rather than in `TagEntry.Set`, and the message is the only place that survives.
   Be explicit about anything not verified.

5. **Push.**

6. **`.\rig.ps1 verify-clone`** — now, and only now. It refuses to run while HEAD is ahead of
   origin.

7. **`.\rig.ps1 health`** if the rig should be up — `build.ps1` stopped it. Report the actual state
   rather than assuming it came back.

## Do not

- Push without being asked, if the user has not already said to.
- Report success for a step you skipped. If the smoke test failed, say so and show the output.
- Claim `verify-clone` passed if it was run before the push.
