#!/usr/bin/env python3
"""Outline a C# file: its types and members, with line numbers.

Reading a 500-line file end to end to find one method is the most repeated waste in this project.
This prints the shape of a file in about 20 lines so the next read can be a range.

    python tools/code-map.py ServerDataStore          # resolves the name under src/
    python tools/code-map.py src/.../DeviceRunner.cs
    python tools/code-map.py DeviceRunner --private   # include private members
    python tools/code-map.py --all                    # every type in the solution, one line each
    python tools/code-map.py --grep Refresh           # where is this member declared?

It is a regex outliner, not a parser. It is deliberately shallow: it reports what is declared and
where, and nothing about what the code does.
"""

import argparse
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SEARCH_DIRS = ("src", "tests", "tools", "shared", "plugin")

TYPE_RE = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*"
    # Each modifier has to consume its own trailing space, or "internal sealed class" matches
    # only "internal" and then fails on the space before "sealed".
    r"(?P<mods>(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|file)\s+)*)"
    r"(?P<kind>class|struct|interface|enum|record)\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)")

MEMBER_RE = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*"
    r"(?P<mods>(?:public|internal|private|protected|static|virtual|override|sealed|abstract|async|extern|unsafe|new|required|partial)\s+)+"
    r"(?P<rest>[^;{]*?)"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*"
    r"(?P<paren>\()")

PROPERTY_RE = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*"
    r"(?P<mods>(?:public|internal|private|protected|static|virtual|override|required|sealed|abstract|new)\s+)+"
    r"(?P<type>[A-Za-z_][A-Za-z0-9_<>,\[\]\?\. ]*?)\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*"
    r"(?:\{\s*(?:get|set|init)|=>)")

SUMMARY_RE = re.compile(r"///\s*<summary>\s*(.*)")
DOC_RE = re.compile(r"///\s*(.*)")

CREF_RE = re.compile(r"<see\s+cref=\"[A-Za-z0-9_.]*?([A-Za-z0-9_]+)\"\s*/?>")
TAG_RE = re.compile(r"</?[a-z]+[^>]*>")


def clean_doc(text):
    """Doc comments are XML. Rendered as-is they are mostly angle brackets."""
    text = CREF_RE.sub(r"\1", text)
    text = TAG_RE.sub("", text)
    return " ".join(text.split())


def find_file(name):
    """Accepts a path, a file name, or a bare type name."""
    if os.path.isfile(name):
        return name
    candidate = os.path.join(ROOT, name)
    if os.path.isfile(candidate):
        return candidate

    stem = name[:-3] if name.endswith(".cs") else name
    matches = []
    for base in SEARCH_DIRS:
        root = os.path.join(ROOT, base)
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in ("bin", "obj", ".vs")]
            for filename in filenames:
                if filename.lower() == (stem + ".cs").lower():
                    matches.append(os.path.join(dirpath, filename))
    if len(matches) == 1:
        return matches[0]
    if not matches:
        return None
    print("ambiguous - " + str(len(matches)) + " matches:", file=sys.stderr)
    for match in matches:
        print("  " + os.path.relpath(match, ROOT), file=sys.stderr)
    return None


def first_doc_line(lines, index):
    """The <summary> line above a declaration, if there is one."""
    look = index - 1
    while look >= 0:
        stripped = lines[look].strip()
        if not stripped:
            look -= 1
            continue
        if not stripped.startswith("///"):
            return ""
        found = SUMMARY_RE.search(stripped)
        if found and clean_doc(found.group(1)):
            return clean_doc(found.group(1))
        if found:
            # <summary> on its own line: the text is on the next one.
            nxt = DOC_RE.search(lines[look + 1].strip()) if look + 1 < len(lines) else None
            return clean_doc(nxt.group(1)) if nxt else ""
        look -= 1
    return ""


def outline(path, include_private=False):
    with open(path, encoding="utf-8-sig") as handle:
        lines = handle.readlines()

    entries = []
    for index, raw in enumerate(lines):
        line = raw.rstrip("\n")
        stripped = line.strip()
        if stripped.startswith("//") or stripped.startswith("*"):
            continue

        found = TYPE_RE.match(line)
        if found:
            entries.append(("type", index + 1, found.group("kind"), found.group("name"),
                            (found.group("mods") or "").strip(), first_doc_line(lines, index)))
            continue

        found = MEMBER_RE.match(line)
        if found and found.group("name") not in ("if", "for", "foreach", "while", "switch",
                                                 "catch", "using", "lock", "return", "yield"):
            mods = found.group("mods").strip()
            if not include_private and mods.startswith("private"):
                continue
            entries.append(("method", index + 1, "", found.group("name"), mods,
                            first_doc_line(lines, index)))
            continue

        found = PROPERTY_RE.match(line)
        if found:
            mods = found.group("mods").strip()
            if not include_private and mods.startswith("private"):
                continue
            entries.append(("property", index + 1, found.group("type").strip(),
                            found.group("name"), mods, first_doc_line(lines, index)))

    return entries, len(lines)


def render(path, entries, total, show_doc=True):
    print(os.path.relpath(path, ROOT) + "  (" + str(total) + " lines)")
    for kind, line_no, extra, name, mods, doc in entries:
        if kind == "type":
            print()
            label = (extra + " " + name).strip()
            print("  {0:>5}  {1}".format(line_no, label))
            if doc and show_doc:
                print("         " + doc[:96])
        else:
            marker = "()" if kind == "method" else ""
            visibility = mods.split()[0] if mods else ""
            print("  {0:>5}    {1}{2}{3}".format(
                line_no, name, marker,
                "  [" + visibility + "]" if visibility not in ("public", "") else ""))
            if doc and show_doc:
                print("         " + " " * 4 + doc[:92])


def cmd_all():
    rows = []
    for base in SEARCH_DIRS:
        root = os.path.join(ROOT, base)
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in ("bin", "obj", ".vs")]
            for filename in sorted(filenames):
                if not filename.endswith(".cs"):
                    continue
                path = os.path.join(dirpath, filename)
                entries, total = outline(path)
                types = [e for e in entries if e[0] == "type"]
                members = len(entries) - len(types)
                rows.append((os.path.relpath(path, ROOT).replace(os.sep, "/"),
                             total, ", ".join(e[3] for e in types), members))
    for path, total, types, members in sorted(rows, key=lambda r: -r[1]):
        print("  {0:>5}  {1:<58} {2}".format(total, path, types))
    print()
    print("{0} file(s)".format(len(rows)))


def cmd_grep(needle):
    hits = 0
    for base in SEARCH_DIRS:
        root = os.path.join(ROOT, base)
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in ("bin", "obj", ".vs")]
            for filename in sorted(filenames):
                if not filename.endswith(".cs"):
                    continue
                path = os.path.join(dirpath, filename)
                entries, _ = outline(path, include_private=True)
                for kind, line_no, extra, name, mods, doc in entries:
                    if needle.lower() in name.lower():
                        hits += 1
                        print("  {0}:{1}  {2} {3}".format(
                            os.path.relpath(path, ROOT).replace(os.sep, "/"), line_no, kind, name))
    print()
    print("{0} declaration(s)".format(hits))


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("target", nargs="?", help="file path, file name, or type name")
    parser.add_argument("--private", action="store_true", help="include private members")
    parser.add_argument("--no-doc", action="store_true", help="omit summary lines")
    parser.add_argument("--all", action="store_true", help="one line per C# file in the tree")
    parser.add_argument("--grep", help="find where a member or type is declared")
    args = parser.parse_args()

    if args.all:
        cmd_all()
        return 0
    if args.grep:
        cmd_grep(args.grep)
        return 0
    if not args.target:
        parser.print_help()
        return 2

    path = find_file(args.target)
    if not path:
        print("not found: " + args.target, file=sys.stderr)
        return 1

    entries, total = outline(path, args.private)
    render(path, entries, total, show_doc=not args.no_doc)
    return 0


if __name__ == "__main__":
    sys.exit(main())
