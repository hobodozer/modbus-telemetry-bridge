#!/usr/bin/env python3
"""Extract the Modbus address map from a compiled Weintek/Maple EasyBuilder .exob.

The .exob has no published format, but the part we need is regular. See FINDINGS.md section 16
for how the address field was located; this is the tool that does it.

Layout, as far as this needs to care:

    "WINDOW"  u32 payload_length  u16 window_count      <- section header, 12 bytes
    then window_count windows, each:
        "WI"  u16 id  u16 width  u16 height  u16 record_count  10 reserved bytes
        then record_count records, each:
            u8[2] type   e.g. "NE" (numeric entry), "AE" (ASCII entry), "BL" (bit lamp)
            u16    size  total record length is size + 2
            payload

The object's device address is a little-endian u16 at byte +31 of the record, the same for every
record type that carries one. Objects at address >= 8000 are bound to the HMI's own local LW
registers rather than to Modbus, and are excluded unless --local is given.

    python tools/exob-map.py project.exob
    python tools/exob-map.py project.exob --csv hmi-map.csv
    python tools/exob-map.py project.exob --types NE,AE --local
"""

import argparse
import csv
import struct
import sys
from collections import Counter

SECTION_MAGIC = b"WINDOW"
WINDOW_MAGIC = b"WI"
WINDOW_HEADER = 20
ADDRESS_OFFSET = 31          # little-endian u16, relative to the start of the record
LOCAL_REGISTER_BASE = 8000   # at or above this the object is bound to the HMI's own LW space

# Only the types whose meaning has been confirmed against the live panel are named. An unnamed
# type is still reported - it just carries no description.
TYPE_NAMES = {
    "AE": "ASCII entry",
    "BL": "bit lamp",
    "DW": "direct window",
    "FK": "function key",
    "GP": "graphic",
    "MD": "meter display",
    "MS": "multi-state",
    "MV": "moving shape",
    "NE": "numeric entry",
    "OL": "overlap window",
    "SW": "set word",
    "TS": "toggle switch",
    "TX": "text",
    "WA": "window attributes",
}


def find_section(blob):
    """Offset of the window section payload, or None."""
    at = blob.find(SECTION_MAGIC)
    if at < 0:
        return None
    payload_length, window_count = struct.unpack_from("<IH", blob, at + len(SECTION_MAGIC))
    return at + 12, payload_length, window_count


def read_records(blob, verbose=False):
    """Walks every window and yields one dict per object record."""
    found = find_section(blob)
    if found is None:
        raise SystemExit("no WINDOW section: this does not look like an .exob")

    offset, payload_length, window_count = found
    end = min(offset + payload_length, len(blob))
    if verbose:
        print(f"window section at {offset}, {payload_length} bytes, {window_count} window(s)",
              file=sys.stderr)

    windows = 0
    while offset < end and windows < window_count:
        if blob[offset:offset + 2] != WINDOW_MAGIC:
            # The section is a flat chain; a bad header means the walk has lost sync, and
            # guessing past it would invent addresses. Stop and report what is certain.
            if verbose:
                print(f"lost sync at {offset} after {windows} window(s)", file=sys.stderr)
            break

        window_id, width, height, record_count = struct.unpack_from("<HHHH", blob, offset + 2)
        offset += WINDOW_HEADER

        for index in range(record_count):
            if offset + 4 > end:
                return
            kind = blob[offset:offset + 2].decode("latin-1")
            size = struct.unpack_from("<H", blob, offset + 2)[0]
            total = size + 2

            address = None
            if total > ADDRESS_OFFSET + 2:
                address = struct.unpack_from("<H", blob, offset + ADDRESS_OFFSET)[0]

            yield {
                "window": window_id,
                "index": index,
                "type": kind,
                "description": TYPE_NAMES.get(kind, ""),
                "offset": offset,
                "length": total,
                "address": address,
            }

            offset += total

        windows += 1


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("exob", help="path to the compiled .exob")
    parser.add_argument("--csv", help="write the map here instead of printing a summary")
    parser.add_argument("--types", help="comma-separated record types to keep, e.g. NE,AE")
    parser.add_argument("--local", action="store_true",
                        help=f"also report objects at address >= {LOCAL_REGISTER_BASE} "
                             "(the HMI's own LW registers, not Modbus)")
    parser.add_argument("--quiet", action="store_true", help="suppress progress on stderr")
    args = parser.parse_args()

    with open(args.exob, "rb") as handle:
        blob = handle.read()

    wanted = None
    if args.types:
        wanted = {t.strip().upper() for t in args.types.split(",") if t.strip()}

    rows, counts, skipped_local = [], Counter(), 0
    for record in read_records(blob, verbose=not args.quiet):
        counts[record["type"]] += 1
        if wanted is not None and record["type"] not in wanted:
            continue
        if record["address"] is None:
            continue
        if record["address"] >= LOCAL_REGISTER_BASE and not args.local:
            skipped_local += 1
            continue
        rows.append(record)

    if args.csv:
        with open(args.csv, "w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(
                handle, fieldnames=["address", "type", "description", "window", "index",
                                    "offset", "length"])
            writer.writeheader()
            for row in sorted(rows, key=lambda r: (r["address"], r["window"], r["index"])):
                writer.writerow({k: row[k] for k in writer.fieldnames})
        print(f"{len(rows)} object(s) -> {args.csv}")
    else:
        for row in sorted(rows, key=lambda r: (r["address"], r["window"], r["index"])):
            label = row["description"] or row["type"]
            print(f"  {row['address']:>6}  {row['type']}  {label:<20} window {row['window']}")

    total = sum(counts.values())
    print(f"\n{total} record(s) across {len(counts)} type(s); "
          f"{len(rows)} reported", file=sys.stderr)
    if skipped_local:
        print(f"{skipped_local} object(s) at >= {LOCAL_REGISTER_BASE} are HMI-local "
              f"(pass --local to include them)", file=sys.stderr)
    print("  " + "  ".join(f"{k}={v}" for k, v in sorted(counts.items())), file=sys.stderr)


if __name__ == "__main__":
    main()
