#!/usr/bin/env python3
"""Read-only queries against a bridge config, so answering "what is at register 200?" does not
mean writing a throwaway parser again.

The config is JSON with comments and trailing commas, which `json.loads` refuses, so every ad-hoc
inspection started by re-solving that. This does it once.

    python tools/config-query.py rig/config/bridge.json summary
    python tools/config-query.py rig/config/bridge.json points --area holdingRegister
    python tools/config-query.py rig/config/bridge.json find 200          # by address
    python tools/config-query.py rig/config/bridge.json find fuel         # by tag substring
    python tools/config-query.py rig/config/bridge.json blocks            # incl. access mix
    python tools/config-query.py rig/config/bridge.json gaps              # unmapped addresses
    python tools/config-query.py rig/config/bridge.json subs
    python tools/config-query.py rig/config/bridge.json derived
    python tools/config-query.py rig/config/bridge.json tags              # every tag name it uses

Nothing here writes. Use tools/make-hmi-map.py to regenerate a map.
"""

import argparse
import json
import os
import re
import sys

SIZE_BY_TYPE = {
    "bool": 1, "int16": 1, "uint16": 1,
    "int32": 2, "uint32": 2, "float32": 2,
    "int64": 4, "uint64": 4, "float64": 4,
}


def load(path):
    """JSON with // comments and trailing commas, matching ConfigService.CreateOptions()."""
    with open(path, encoding="utf-8-sig") as handle:
        text = handle.read()
    # Strip // comments, but not inside a string literal.
    out = []
    in_string = False
    escape = False
    i = 0
    while i < len(text):
        ch = text[i]
        if in_string:
            out.append(ch)
            if escape:
                escape = False
            elif ch == chr(92):
                escape = True
            elif ch == '"':
                in_string = False
            i += 1
            continue
        if ch == '"':
            in_string = True
            out.append(ch)
            i += 1
            continue
        if ch == "/" and i + 1 < len(text) and text[i + 1] == "/":
            while i < len(text) and text[i] != chr(10):
                i += 1
            continue
        out.append(ch)
        i += 1
    stripped = "".join(out)
    stripped = re.sub(r",(\s*[}\]])", r"\1", stripped)   # trailing commas
    return json.loads(stripped)


def point_size(point):
    if point.get("dataType", "uint16").lower() == "string":
        return max(1, int(point.get("length", 1)))
    return SIZE_BY_TYPE.get(point.get("dataType", "uint16").lower(), 1)


def walk_points(config):
    """Yields (server, map, block, point) for every enabled point in every server block."""
    for server in config.get("servers", []):
        for mapping in server.get("maps", []):
            for block in mapping.get("blocks", []):
                for point in block.get("points", []):
                    yield server, mapping, block, point


def cmd_summary(config, args):
    print("clients")
    for client in config.get("clients", []):
        reads = client.get("readGroups", []) or []
        writes = client.get("writeGroups", []) or []
        read_points = sum(len(g.get("points", [])) for g in reads)
        write_points = sum(len(g.get("points", [])) for g in writes)
        print("  {0:<22} {1}:{2}  {3} read group(s)/{4} point(s), {5} write group(s)/{6} point(s)"
              .format(client.get("name", "?"), client.get("host", "?"), client.get("port", 502),
                      len(reads), read_points, len(writes), write_points))

    print()
    print("servers")
    for server in config.get("servers", []):
        blocks = sum(len(m.get("blocks", [])) for m in server.get("maps", []))
        points = sum(len(b.get("points", []))
                     for m in server.get("maps", []) for b in m.get("blocks", []))
        print("  {0:<22} {1}:{2}  {3} map(s), {4} block(s), {5} point(s)"
              .format(server.get("name", "?"), server.get("bindAddress", "?"),
                      server.get("port", 502), len(server.get("maps", [])), blocks, points))

    telemetry = config.get("telemetry") or {}
    print()
    print("telemetry   {0} subscription(s), {1} feedback, port {2}"
          .format(len(telemetry.get("subscriptions", []) or []),
                  len(telemetry.get("feedback", []) or []),
                  telemetry.get("listenPort", "-")))

    for name in ("derived", "recording", "replay", "pcStats", "keyboard", "vJoy", "simulation"):
        section = config.get(name)
        if section is None:
            print("{0:<12}absent".format(name))
        elif isinstance(section, list):
            print("{0:<12}{1} entrie(s)".format(name, len(section)))
        else:
            state = section.get("enabled")
            extra = ""
            if name == "vJoy":
                extra = "  {0} device(s)".format(len(section.get("devices", []) or []))
            print("{0:<12}enabled={1}{2}".format(name, state, extra))


def cmd_points(config, args):
    rows = []
    for server, mapping, block, point in walk_points(config):
        if args.area and block.get("area", "").lower() != args.area.lower():
            continue
        if args.access and point.get("access", "read").lower() != args.access.lower():
            continue
        base = int(block.get("startAddress", 0))
        rows.append((base + int(point.get("offset", 0)),
                     point.get("tag", ""), point.get("dataType", "uint16"),
                     point.get("access", "read"), block.get("area", ""), block.get("name", "")))
    for address, tag, kind, access, area, block_name in sorted(rows):
        print("  {0:>6}  {1:<34} {2:<9} {3:<10} {4} / {5}"
              .format(address, tag, kind, access, area, block_name))
    print()
    print("{0} point(s)".format(len(rows)))


def cmd_find(config, args):
    needle = args.needle
    numeric = needle.isdigit()
    target = int(needle) if numeric else None
    hits = 0

    for server, mapping, block, point in walk_points(config):
        base = int(block.get("startAddress", 0))
        address = base + int(point.get("offset", 0))
        size = point_size(point)
        tag = point.get("tag", "")

        matched = (target is not None and address <= target < address + size) or \
                  (target is None and needle.lower() in tag.lower())
        if not matched:
            continue
        hits += 1
        span = "{0}".format(address) if size == 1 else "{0}..{1}".format(address, address + size - 1)
        print("  {0:<12} {1:<34} {2:<9} {3:<10} bit={4}  {5} / {6}"
              .format(span, tag, point.get("dataType", "uint16"), point.get("access", "read"),
                      point.get("bitIndex", -1), block.get("area", ""), block.get("name", "")))
        scale = point.get("scale")
        if scale:
            print("               scale gain={0} offset={1} min={2} max={3} deadband={4}"
                  .format(scale.get("gain", 1), scale.get("offset", 0),
                          scale.get("min"), scale.get("max"), scale.get("deadband", 0)))
        if point.get("description"):
            print("               " + point["description"])

    # A tag can also come from somewhere other than a server point.
    if target is None:
        for entry in config.get("derived", []) or []:
            if needle.lower() in entry.get("tag", "").lower():
                hits += 1
                print("  derived      {0:<34} = {1}".format(entry.get("tag"), entry.get("expression")))
        telemetry = config.get("telemetry") or {}
        for sub in telemetry.get("subscriptions", []) or []:
            if needle.lower() in sub.get("tag", "").lower():
                hits += 1
                print("  simhub       {0:<34} <- {1}".format(sub.get("tag"), sub.get("property")))

    print()
    print("{0} match(es)".format(hits))


def cmd_blocks(config, args):
    for server in config.get("servers", []):
        for mapping in server.get("maps", []):
            for block in mapping.get("blocks", []):
                access = {}
                for point in block.get("points", []):
                    access.setdefault(point.get("access", "read"), 0)
                    access[point.get("access", "read")] += 1
                mix = ", ".join("{0}={1}".format(k, v) for k, v in sorted(access.items()))
                flag = "  MIXED ACCESS" if len(access) > 1 else ""
                print("  {0:<20} {1:<16} start={2:<6} size={3:<6} stale={4}/{5}ms  [{6}]{7}"
                      .format(block.get("name", "?"), block.get("area", "?"),
                              block.get("startAddress", 0), block.get("size", 0),
                              block.get("staleBehavior", "holdLastValue"),
                              block.get("staleTimeoutMs", 0), mix, flag))


def cmd_gaps(config, args):
    """Addresses reserved by a block but not covered by any point. They read as zero, which looks
    exactly like a dead data source - this is how "register 7 reads 0" gets diagnosed."""
    for server in config.get("servers", []):
        for mapping in server.get("maps", []):
            for block in mapping.get("blocks", []):
                size = int(block.get("size", 0))
                covered = set()
                for point in block.get("points", []):
                    start = int(point.get("offset", 0))
                    for i in range(start, start + point_size(point)):
                        covered.add(i)
                gaps = [i for i in range(size) if i not in covered]
                if not gaps:
                    print("  {0}: fully mapped ({1} address(es))".format(block.get("name"), size))
                    continue

                runs = []
                run_start = gaps[0]
                previous = gaps[0]
                for value in gaps[1:]:
                    if value != previous + 1:
                        runs.append((run_start, previous))
                        run_start = value
                    previous = value
                runs.append((run_start, previous))

                base = int(block.get("startAddress", 0))
                print("  {0}: {1} of {2} address(es) unmapped"
                      .format(block.get("name"), len(gaps), size))
                for low, high in runs[:args.limit]:
                    label = str(base + low) if low == high else "{0}..{1}".format(base + low, base + high)
                    print("      " + label)
                if len(runs) > args.limit:
                    print("      ... {0} more run(s), raise --limit".format(len(runs) - args.limit))


def cmd_subs(config, args):
    telemetry = config.get("telemetry") or {}
    subs = telemetry.get("subscriptions", []) or []
    for sub in subs:
        scale = sub.get("scale")
        suffix = ""
        if scale:
            suffix = "  gain={0} offset={1}".format(scale.get("gain", 1), scale.get("offset", 0))
        print("  {0:<34} <- {1}{2}".format(sub.get("tag", ""), sub.get("property", ""), suffix))
    print()
    print("{0} subscription(s)".format(len(subs)))


def cmd_derived(config, args):
    entries = config.get("derived", []) or []
    for entry in entries:
        state = "" if entry.get("enabled", True) else "  (disabled)"
        print("  {0:<34} = {1}{2}".format(entry.get("tag", ""), entry.get("expression", ""), state))
        if not entry.get("requireGoodInputs", True):
            print("      publishes even when an input is stale or bad")
    print()
    print("{0} derived tag(s)".format(len(entries)))


def cmd_tags(config, args):
    names = set()
    for server, mapping, block, point in walk_points(config):
        names.add((mapping.get("tagPrefix", "") or "") + point.get("tag", ""))
    for client in config.get("clients", []):
        for group in (client.get("readGroups", []) or []) + (client.get("writeGroups", []) or []):
            for point in group.get("points", []):
                names.add((client.get("tagPrefix", "") or "") + point.get("tag", ""))
    telemetry = config.get("telemetry") or {}
    for sub in telemetry.get("subscriptions", []) or []:
        names.add((telemetry.get("tagPrefix", "") or "") + sub.get("tag", ""))
    for entry in config.get("derived", []) or []:
        names.add(entry.get("tag", ""))

    for name in sorted(n for n in names if n):
        print("  " + name)
    print()
    print("{0} distinct tag name(s)".format(len(names)))


AREA_SHORT = {
    "holdingregister": "holding", "inputregister": "input",
    "coil": "coil", "discreteinput": "discrete",
}


def cmd_resolve(config, args):
    """Everything another script needs to read one tag off the wire, as key=value lines.

    This exists so tools/read-tag.ps1 does not have to parse human-readable output. Prints the
    single best match: an exact tag name if there is one, otherwise the first substring hit.
    """
    needle = args.needle
    candidates = []
    for server, mapping, block, point in walk_points(config):
        tag = (mapping.get("tagPrefix", "") or "") + point.get("tag", "")
        if needle.lower() == tag.lower():
            candidates.insert(0, (server, mapping, block, point, tag))
        elif needle.lower() in tag.lower():
            candidates.append((server, mapping, block, point, tag))

    if not candidates:
        print("found=0")
        return

    server, mapping, block, point, tag = candidates[0]
    scale = point.get("scale") or {}
    area = block.get("area", "holdingRegister").lower()

    print("found=1")
    print("matches=" + str(len(candidates)))
    print("tag=" + tag)
    print("address=" + str(int(block.get("startAddress", 0)) + int(point.get("offset", 0))))
    print("area=" + AREA_SHORT.get(area, "holding"))
    print("dataType=" + point.get("dataType", "uint16"))
    print("size=" + str(point_size(point)))
    print("bitIndex=" + str(point.get("bitIndex", -1)))
    print("gain=" + str(scale.get("gain", 1.0)))
    print("offset=" + str(scale.get("offset", 0.0)))
    print("access=" + point.get("access", "read"))
    print("port=" + str(server.get("port", 502)))
    print("unit=" + str(mapping.get("unitId", 1)))
    print("units=" + (point.get("units") or ""))
    print("description=" + (point.get("description") or ""))


COMMANDS = {
    "summary": cmd_summary, "points": cmd_points, "find": cmd_find, "blocks": cmd_blocks,
    "gaps": cmd_gaps, "subs": cmd_subs, "derived": cmd_derived, "tags": cmd_tags,
    "resolve": cmd_resolve,
}


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("config", help="path to bridge.json")
    parser.add_argument("command", choices=sorted(COMMANDS), help="what to report")
    parser.add_argument("needle", nargs="?", default="", help="for 'find': an address or tag substring")
    parser.add_argument("--area", help="for 'points': coil, discreteInput, holdingRegister, inputRegister")
    parser.add_argument("--access", help="for 'points': read, write, readWrite")
    parser.add_argument("--limit", type=int, default=12, help="for 'gaps': runs to print per block")
    args = parser.parse_args()

    if not os.path.exists(args.config):
        print("no such config: " + args.config, file=sys.stderr)
        return 2
    if args.command in ("find", "resolve") and not args.needle:
        print("'" + args.command + "' needs an address or a tag substring", file=sys.stderr)
        return 2

    COMMANDS[args.command](load(args.config), args)
    return 0


if __name__ == "__main__":
    sys.exit(main())
