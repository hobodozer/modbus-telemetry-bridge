---
description: Hunt for bugs in classes the last pass structurally could not see, proving each finding before claiming it
---

Hunt for bugs in this codebase. $ARGUMENTS

The first review pass here found three bugs and signed off. A second pass found six more, including
one that let any client on the network corrupt what the HMI displayed. That happened because the
first pass looked for *wrong logic* and the bugs were something else. So do not re-read code
looking for mistakes — go after the classes a reading pass cannot see.

## Classes worth targeting

These are the ones that actually got through here. `FINDINGS.md` section 17 has the detail.

- **Mutation before validation.** A handler that writes to shared state before its guard clause.
  `ApplyWrite` overlaid a client's bytes onto the register image and only then checked whether the
  points were writable, so a *refused* write still corrupted the image.
- **Right work, wrong loop.** A per-collection operation sitting inside a per-element loop. The
  tell is cost that tracks the size of the data structure rather than the size of the request.
- **The only configuration that hides it is the one you run.** Benchmark and test the *shipped
  defaults*, not `rig/config/bridge.json`. The O(n²) read cost was invisible under the live
  config's `holdLastValue`/0 and present under `DefaultConfig`'s `Failsafe`/2000.
- **Cleanup on the path that cannot accumulate.** A bounds check inside the success branch.
- **A guard on one branch of two.** One arm of an if/else checks a precondition and the other
  does not.
- **Trusting a declared length over the bytes that arrived.**
- **A field that means something adjacent to what you want.** `bridge.gameCode` answers "which game
  is selected", not "is a game running". A name that sounds like the question is not an answer.

## How to work

1. **Prove it before you call it a bug.** Write a probe that fails against current code — the
   existing ones are `tools/store-probe` and `tools/repo-check.py --self-test`. A finding you
   only reasoned about is a hypothesis, and two "obvious" ones were wrong here.
2. **Measure, do not estimate.** "1.895 ms → 0.045 ms" is a finding; "this looks slow" is not.
3. **Add the regression to `tests/ModbusBridge.SmokeTest/Program.cs` before fixing**, so it is
   red first.
4. **Check the fix does not break the legitimate case.** The write-masking fix needed proving
   against packed bits sharing a register, partially-covered multi-register points and unmapped
   gaps — all of which still had to work.
5. **Ask whether the obvious fix is the right one.** `onChangeOnly` looked like a bug in
   `TagEntry.Set`; fixing it there would have let `SweepStale` demote a live input sitting at zero.

## Finish

`.\rig.ps1 check`, then `.\build.ps1`. Report what you proved, with the measurement. Say plainly
which findings are confirmed and which are still hypotheses — do not present the second as the
first. Update `FINDINGS.md` in the same commit that fixes the bug.
