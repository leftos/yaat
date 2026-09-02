"""Rebuild an always-loaded memory index from a machine-readable inventory.

The index file is injected into every session and has a hard load budget, past
which entries are silently dropped — a truncated index looks exactly like a
shorter one. So the thing that writes the index also measures it, and every
integrity property is asserted on every run:

  * dangling  — an index link with no file behind it
  * unindexed — a file in the store that no index line points at
  * over-line — a line longer than the per-line budget
  * over-file — the whole index past its line or character budget

Any non-zero count is a failure (exit 1), not a warning.

Inventory format: one pipe-delimited row per surviving file,
`<filename> | <type> | <description> | <section>`. Blank lines, `#` comments,
a `filename | ...` header row and `---|---` separators are skipped. Sections
appear in the index in the order they first appear in the inventory.

Usage:
    python rebuild_index.py --memory-dir DIR --inventory FILE [--index MEMORY.md]
                            [--header FILE] [--dry-run]
                            [--max-line 160] [--max-lines 200] [--max-chars 25000]
"""

import argparse
import re
import sys
from pathlib import Path

PACK_THRESHOLD = 8  # sections larger than this collapse to multi-entry lines
TITLE_MAX = 58
INVENTORY_COLUMNS = 4
TITLE_MIN = 12  # a clause boundary before this leaves no usable title
TAIL_MIN = 15  # drop the description tail rather than truncate it below this


def parse_args(argv):
    p = argparse.ArgumentParser(description="Rebuild a budgeted memory index from an inventory file.")
    p.add_argument("--memory-dir", required=True, type=Path, help="directory holding the memory .md files")
    p.add_argument("--inventory", required=True, type=Path, help="pipe-delimited inventory produced by the audit")
    p.add_argument("--index", default="MEMORY.md", help="index filename inside --memory-dir (default MEMORY.md)")
    p.add_argument("--header", type=Path, default=None, help="preamble file; default reuses the existing index's preamble")
    p.add_argument("--dry-run", action="store_true", help="print the measurements and write nothing")
    p.add_argument("--max-line", type=int, default=160, help="per-line character budget (default 160)")
    p.add_argument("--max-lines", type=int, default=200, help="whole-file line budget (default 200)")
    p.add_argument("--max-chars", type=int, default=25000, help="whole-file character budget (default 25000)")
    return p.parse_args(argv)


def parse_inventory(path):
    """Return [(filename, type, description, section)] and the section order."""
    rows, order = [], []
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip().strip("|").strip()
        if not line or line.startswith("#") or line.lower().startswith("filename") or set(line) <= set("-| :"):
            continue
        parts = [p.strip() for p in line.split("|")]
        if len(parts) < INVENTORY_COLUMNS:
            print(f"SKIP unparsable inventory row: {raw}", file=sys.stderr)
            continue
        fname = re.sub(r"[`*]", "", parts[0])
        if not fname.endswith(".md"):
            fname += ".md"
        rows.append((fname, parts[1], parts[2], parts[3]))
        if parts[3] not in order:
            order.append(parts[3])
    return rows, order


def split_desc(desc):
    """Return (title, rest): cut at the first clause boundary that leaves a usable title."""
    for sep in (" — ", ": ", "; ", " (", " - "):
        i = desc.find(sep)
        if TITLE_MIN <= i <= TITLE_MAX:
            rest = desc[i + len(sep) :].strip()
            if rest.count(")") > rest.count("("):
                rest = rest.replace(")", "", 1).strip(" ;,")
            return desc[:i].strip(), rest
    if len(desc) <= TITLE_MAX:
        return desc.rstrip("."), ""
    cut = desc[: TITLE_MAX - 1].rsplit(" ", 1)[0]
    return cut + "…", desc[len(cut) :].strip(" ,;:—-")


def full_line(fname, desc, max_line):
    title, rest = split_desc(desc)
    link = f"- [{title}]({fname})"
    if rest:
        budget = max_line - len(link) - 3
        if budget >= TAIL_MIN:
            if len(rest) > budget:
                rest = rest[: budget - 1].rsplit(" ", 1)[0] + "…"
            return f"{link} — {rest.rstrip('.')}"
    return link


def packed_lines(entries, max_line):
    lines, cur = [], ""
    for fname, desc in entries:
        title, _ = split_desc(desc)
        item = f"[{title}]({fname})"
        cand = item if not cur else f"{cur}; {item}"
        if cur and len("- " + cand) > max_line:
            lines.append("- " + cur)
            cur = item
        else:
            cur = cand
    if cur:
        lines.append("- " + cur)
    return lines


def read_header(args, index_path):
    if args.header:
        return [*args.header.read_text(encoding="utf-8").rstrip("\n").splitlines(), ""]
    if index_path.exists():
        preamble = []
        for line in index_path.read_text(encoding="utf-8").splitlines():
            if line.startswith("## "):
                break
            preamble.append(line)
        while preamble and not preamble[-1].strip():
            preamble.pop()
        if preamble:
            return [*preamble, ""]
    return [f"# {index_path.stem}", ""]


def build_index(rows, order, on_disk, header, max_line):
    sections = {}
    for fname, _type, desc, sec in rows:
        if fname not in on_disk:
            continue
        sections.setdefault(sec, []).append((fname, desc))
    out = list(header)
    for sec in order:
        entries = sorted(sections.get(sec, []), key=lambda r: r[0])
        if not entries:
            continue
        out.append(f"## {sec}")
        if len(entries) > PACK_THRESHOLD:
            out.extend(packed_lines(entries, max_line))
        else:
            out.extend(full_line(f, d, max_line) for f, d in entries)
        out.append("")
    return "\n".join(out).rstrip("\n") + "\n"


def collect_failures(args, m):
    """Every integrity property that must hold before the index may be written."""
    checks = [
        (m["ghosts"], f"{len(m['ghosts'])} inventory rows name a file that is not on disk"),
        (m["dangling"], f"{len(m['dangling'])} dangling links"),
        (m["unindexed"], f"{len(m['unindexed'])} files with no index entry"),
        (m["over_line"], f"{len(m['over_line'])} lines over the {args.max_line}-char budget"),
        (m["line_count"] > args.max_lines, f"{m['line_count']} lines over the {args.max_lines}-line budget"),
        (m["char_count"] > args.max_chars, f"{m['char_count']} chars over the {args.max_chars}-char budget"),
    ]
    return [message for failed, message in checks if failed]


def main(argv=None):
    args = parse_args(argv if argv is not None else sys.argv[1:])
    index_path = args.memory_dir / args.index
    rows, order = parse_inventory(args.inventory)
    on_disk = {p.name for p in args.memory_dir.glob("*.md")} - {args.index}

    listed = {r[0] for r in rows}
    ghosts = sorted(listed - on_disk)  # inventory rows with no file
    text = build_index(rows, order, on_disk, read_header(args, index_path), args.max_line)

    lines = text.splitlines()
    links = set(re.findall(r"\]\(([^)]+\.md)\)", text))
    m = {
        "ghosts": ghosts,
        "dangling": sorted(links - on_disk),
        "unindexed": sorted(on_disk - links),
        "over_line": [ln for ln in lines if len(ln) > args.max_line],
        "line_count": len(lines),
        "char_count": len(text),
    }

    print(f"{index_path}: {len(lines)} lines (budget {args.max_lines}), {len(text)} chars (budget {args.max_chars}), {len(links)} links")
    print(f"inventory rows naming a missing file: {len(ghosts)} {ghosts or ''}")
    print(f"dangling links: {len(m['dangling'])} {m['dangling'] or ''}")
    print(f"unindexed files: {len(m['unindexed'])} {m['unindexed'] or ''}")
    print(f"lines over {args.max_line} chars: {len(m['over_line'])}")
    for ln in m["over_line"]:
        print(f"  [{len(ln)}] {ln[:80]}…")

    failures = collect_failures(args, m)

    if failures:
        print("FAIL: " + "; ".join(failures), file=sys.stderr)
        print("(index not written)" if not args.dry_run else "(dry run)", file=sys.stderr)
        return 1

    if args.dry_run:
        print("dry run: index not written")
        return 0
    index_path.write_text(text, encoding="utf-8", newline="\n")
    print("OK: index written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
