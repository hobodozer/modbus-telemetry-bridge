#!/usr/bin/env python3
"""Consistency checks for this repository.

Every check here exists because the thing it catches already went wrong and cost real time. None
of them need the rig running, a PLC, or a network - it is all static, and it takes about a second.

    python tools/repo-check.py           # report, exit non-zero on a failure
    python tools/repo-check.py --quiet   # only failures
    python tools/repo-check.py --list    # what is checked, and why it is checked

Run it before pushing. `rig.ps1 check` and `build.ps1` both call it.
"""

import argparse
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

BINARY_SUFFIXES = (".png", ".ico", ".exe", ".dll", ".pdb", ".zip", ".pyc")
CODE_SUFFIXES = (".cs", ".py", ".ps1", ".csproj", ".xaml", ".sln")
SOURCE_DIRS = ("src", "tools", "tests", "shared", "plugin")

CR = 13
# Bytes that should never appear in a text file here. CR is handled separately, because CRLF is
# legitimate and only a *lone* CR is damage.
FORBIDDEN = set(range(0, 9)) | {11, 12} | set(range(14, 32))

# Projects that are outside ModbusBridge.sln on purpose. The reason has to be stated, so that
# "it is deliberate" stays a decision rather than something nobody got round to.
EXEMPT_PROJECTS = {
    "plugin/modbusbridge.simhubplugin/modbusbridge.simhubplugin.csproj":
        "targets net48 and references assemblies from a SimHub install; including it would break "
        "the build on a machine without SimHub",
}

# Documentation sometimes has to quote a stale claim in order to describe it - CLAUDE.md's
# documentation-maintenance rule lists the exact sentences that were once wrong. Wrap those in
# these markers so the claim checker reads them as history rather than as an assertion.
IGNORE_OPEN = "repo-check: ignore-block"
IGNORE_CLOSE = "repo-check: end-ignore"


class Report:
    def __init__(self, quiet):
        self.quiet = quiet
        self.failures = 0
        self.checks = 0

    def check(self, name):
        self.checks += 1
        if not self.quiet:
            print()
            print(name)

    def ok(self, detail):
        if not self.quiet:
            print("  ok    " + detail)

    def fail(self, detail):
        self.failures += 1
        print("  FAIL  " + detail)

    def note(self, detail):
        if not self.quiet:
            print("  ..    " + detail)


def git(*args):
    out = subprocess.run(["git", "-C", ROOT] + list(args), capture_output=True, text=True)
    if out.returncode != 0:
        return []
    return [line for line in out.stdout.splitlines() if line]


def tracked_text_files():
    for path in git("ls-files"):
        if path.lower().endswith(BINARY_SUFFIXES):
            continue
        full = os.path.join(ROOT, path)
        if os.path.isfile(full):
            yield path, full


def check_escape_damage(report):
    """A tool call writing a Windows path through a shell can have its backslash escapes
    interpreted. The result is non-printing, so it survives review and lands in the repository.
    Nine commands across three files were corrupted this way before anyone noticed, and two more
    hid in rig.ps1's own help text."""
    report.check("Escape damage in tracked text files")
    bad = 0
    for path, full in tracked_text_files():
        data = open(full, "rb").read()
        crlf = data.count(bytes([CR, 10]))
        lone_cr = data.count(bytes([CR])) - crlf
        control = sum(data.count(bytes([c])) for c in FORBIDDEN)
        if lone_cr or control:
            report.fail("{0}: {1} lone CR, {2} control byte(s) - an interpreted backslash escape"
                        .format(path, lone_cr, control))
            bad += 1
    if not bad:
        report.ok("no interpreted escapes anywhere in the tree")


def check_projects_in_solution(report):
    """tools/simhub-catalog compiled the shared protocol file but was in neither the solution nor
    build.ps1. It broke silently when a shared signature changed, and only a grep found it."""
    report.check("Every project is in the solution")
    sln = os.path.join(ROOT, "ModbusBridge.sln")
    text = open(sln, encoding="utf-8-sig").read() if os.path.exists(sln) else ""
    referenced = set()
    for match in re.findall(r'"([^"]+\.csproj)"', text):
        referenced.add(match.replace(chr(92), "/").lower())

    missing = []
    exempt = 0
    for path in git("ls-files", "*.csproj"):
        key = path.replace(chr(92), "/").lower()
        if key in referenced:
            continue
        if key in EXEMPT_PROJECTS:
            exempt += 1
            report.note("{0} is outside the solution on purpose: {1}"
                        .format(path, EXEMPT_PROJECTS[key]))
            continue
        missing.append(path)

    if missing:
        for path in missing:
            report.fail(path + " is not in ModbusBridge.sln - it will rot unnoticed")
    else:
        report.ok("all {0} project(s) referenced, {1} exempt".format(len(referenced), exempt))


def check_source_not_ignored(report):
    """A .gitignore rule once matched src/ModbusBridge.Core/Config/ and kept nine source files out
    of the repository. Every local build passed; a fresh clone failed with 54 errors."""
    report.check("No source file is excluded by .gitignore")
    candidates = []
    for base in SOURCE_DIRS:
        root = os.path.join(ROOT, base)
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in ("bin", "obj", ".vs")]
            for name in filenames:
                if name.endswith(CODE_SUFFIXES):
                    rel = os.path.relpath(os.path.join(dirpath, name), ROOT)
                    candidates.append(rel.replace(os.sep, "/"))

    if not candidates:
        report.note("no source files found to check")
        return

    tracked = set(git("ls-files"))
    untracked = [c for c in candidates if c not in tracked]

    ignored = set()
    if untracked:
        out = subprocess.run(["git", "-C", ROOT, "check-ignore", "--"] + untracked,
                             capture_output=True, text=True)
        ignored = set(line.replace(os.sep, "/") for line in out.stdout.splitlines() if line)

    for path in sorted(ignored):
        report.fail(path + " is source but .gitignore excludes it - a clean clone will not build")
    for path in untracked:
        if path not in ignored:
            report.fail(path + " is source but is not tracked by git")

    if not ignored and not untracked:
        report.ok("all {0} source file(s) tracked, none ignored".format(len(candidates)))


def check_tools_documented(report):
    """A tool nobody knows about gets rewritten from scratch. Both tools added on 2026-09-10 were
    missing from the project-layout tree until someone went looking."""
    report.check("Every tool appears in the README project layout")
    readme = os.path.join(ROOT, "README.md")
    if not os.path.exists(readme):
        report.fail("README.md is missing")
        return
    text = open(readme, encoding="utf-8-sig").read()

    entries = set()
    for path in git("ls-files", "tools/*"):
        parts = path.split("/")
        if len(parts) >= 2 and not parts[1].startswith("__"):
            entries.add("tools/" + parts[1])

    missing = [e for e in sorted(entries) if e not in text]
    for entry in missing:
        report.fail(entry + " is not mentioned in README.md")
    if not missing:
        report.ok("all {0} tool(s) documented".format(len(entries)))


def check_doc_paths_exist(report):
    """Documentation naming a file that no longer exists sends the next reader hunting for it."""
    report.check("Paths named in documentation exist")
    pattern = re.compile(r"`((?:src|tools|tests|shared|plugin)/[A-Za-z0-9_./-]+)`")
    seen = set()
    missing = []
    for path in git("ls-files", "*.md"):
        full = os.path.join(ROOT, path)
        with open(full, encoding="utf-8-sig") as handle:
            for line_no, line in enumerate(handle, 1):
                for match in pattern.findall(line):
                    target = match.rstrip("/")
                    if target in seen:
                        continue
                    seen.add(target)
                    if not os.path.exists(os.path.join(ROOT, target)):
                        missing.append("{0}:{1} refers to {2}, which does not exist"
                                       .format(path, line_no, target))
    for item in missing:
        report.fail(item)
    if not missing:
        report.ok("all {0} referenced path(s) exist".format(len(seen)))


def check_stale_claims(report):
    """README claimed five shipped features were "Not started", and said GPU load was not
    collected in four places while the collector had been running for a day. This cannot be fully
    automated, so each entry pairs a phrase with the file whose existence disproves it."""
    report.check("Documented gaps against what is actually built")
    claims = [
        ("GPU collection", "src/ModbusBridge.Core/Inputs/GpuCounter.cs",
         re.compile(r"GPU[^.\n]{0,60}(not collected|does not gather|is not gathered)", re.I)),
        ("keyboard output", "src/ModbusBridge.Core/Outputs/Keyboard/KeyboardFeeder.cs",
         re.compile(r"[Kk]eyboard[^.\n]{0,60}[Nn]ot started")),
        ("network scanner", "src/ModbusBridge.Core/Modbus/ModbusScanner.cs",
         re.compile(r"[Nn]etwork scanner[^.\n]{0,60}[Nn]ot started")),
        ("CSV record/replay", "src/ModbusBridge.Core/Inputs/TagReplayer.cs",
         re.compile(r"record/replay[^.\n]{0,60}[Nn]ot started")),
        ("derived tags", "src/ModbusBridge.Core/Data/Expression.cs",
         re.compile(r"derived[- ]tag[^.\n]{0,60}[Nn]ot started", re.I)),
        ("schema chunking", "shared/TelemetryProtocol.cs",
         re.compile(r"schema is NOT")),
        ("shift layers", "src/ModbusBridge.Core/Outputs/VJoy/VJoyFeeder.cs",
         re.compile(r"shift layers[^.\n]{0,60}[Nn]ot started")),
    ]

    live = {}
    for path in git("ls-files", "*.md"):
        full = os.path.join(ROOT, path)
        ignoring = False
        kept = []
        with open(full, encoding="utf-8-sig") as handle:
            for line_no, line in enumerate(handle, 1):
                if IGNORE_OPEN in line:
                    ignoring = True
                    continue
                if IGNORE_CLOSE in line:
                    ignoring = False
                    continue
                if not ignoring:
                    kept.append((line_no, line))
        live[path] = kept

    hits = 0
    quoted = 0
    for label, evidence, pattern in claims:
        if not os.path.exists(os.path.join(ROOT, evidence)):
            continue
        for path, lines in live.items():
            for line_no, line in lines:
                if pattern.search(line):
                    report.fail("{0}:{1} says {2} is unbuilt, but {3} exists"
                                .format(path, line_no, label, evidence))
                    hits += 1

    for path in live:
        total = sum(1 for _ in open(os.path.join(ROOT, path), encoding="utf-8-sig"))
        quoted += total - len(live[path])

    if not hits:
        report.ok("none of the {0} tracked claims contradict the tree".format(len(claims)))
        if quoted:
            report.note("{0} line(s) skipped as quoted history".format(quoted))


CHECKS = [
    check_escape_damage,
    check_projects_in_solution,
    check_source_not_ignored,
    check_tools_documented,
    check_doc_paths_exist,
    check_stale_claims,
]


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--quiet", action="store_true", help="only print failures")
    parser.add_argument("--list", action="store_true", help="describe the checks and exit")
    args = parser.parse_args()

    if args.list:
        for fn in CHECKS:
            print(fn.__name__)
            print("    " + " ".join((fn.__doc__ or "").split()))
            print()
        return 0

    report = Report(args.quiet)
    for fn in CHECKS:
        fn(report)

    print()
    if report.failures:
        print("{0} problem(s) across {1} check(s)".format(report.failures, report.checks))
        return 1
    print("repo clean - {0} check(s) passed".format(report.checks))
    return 0


if __name__ == "__main__":
    sys.exit(main())
