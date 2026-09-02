"""Aggregate CPU_TIME leaf intervals from a dotnet-trace speedscope file.

`dotnet-trace convert --format speedscope` emits one "evented" profile per OS
thread. Most of those threads are idle thread-pool workers whose wall span
covers the whole run, so speedscope's own topN view is dominated by waits.
This script separates CPU from wall: it walks each thread's open/close events,
keeps only the intervals whose leaf frame is the sample profiler's `CPU_TIME`
marker, and attributes each interval to the real frame directly beneath it.

Usage:
    python speedscope_cpu.py <trace.speedscope.json> [--top N] [--threads N]
                             [--thread SUBSTRING] [--marker CPU_TIME]

Output: threads ranked by CPU (not wall), then the busiest thread's hottest
self-CPU frames. Pick the thread with the most CPU, never the most wall time.
"""

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

MIN_STACK_FOR_LEAF = 2  # the marker frame plus the real frame beneath it


def parse_args(argv):
    p = argparse.ArgumentParser(description="Rank threads by CPU and show the busiest thread's hottest leaf frames.")
    p.add_argument("speedscope", help="path to a *.speedscope.json produced by dotnet-trace")
    p.add_argument("--top", type=int, default=20, help="hot frames to print for the selected thread (default 20)")
    p.add_argument("--threads", type=int, default=10, help="threads to print in the ranking (default 10)")
    p.add_argument("--thread", default=None, help="substring of a thread name; overrides 'busiest by CPU' selection")
    p.add_argument("--marker", default="CPU_TIME", help="sample-profiler leaf marker frame (default CPU_TIME)")
    return p.parse_args(argv)


def walk_profile(profile, frame_names, marker):
    """Return (cpu_total, wall_span, {frame_name: self_cpu}) for one evented profile."""
    per_frame = defaultdict(float)
    stack = []
    last_at = profile.get("startValue", 0.0)
    cpu_total = 0.0
    for ev in profile.get("events", []):
        at = ev["at"]
        delta = at - last_at
        if delta > 0 and len(stack) >= MIN_STACK_FOR_LEAF and frame_names[stack[-1]] == marker:
            per_frame[frame_names[stack[-2]]] += delta
            cpu_total += delta
        last_at = at
        if ev["type"] == "O":
            stack.append(ev["frame"])
        elif stack:
            stack.pop()
    wall = profile.get("endValue", last_at) - profile.get("startValue", 0.0)
    return cpu_total, wall, per_frame


def main(argv=None):
    args = parse_args(argv if argv is not None else sys.argv[1:])
    with Path(args.speedscope).open(encoding="utf-8") as fh:
        doc = json.load(fh)
    frame_names = [f["name"] for f in doc["shared"]["frames"]]
    unit = doc["profiles"][0].get("unit", "?") if doc["profiles"] else "?"

    rows = []
    for prof in doc["profiles"]:
        if prof.get("type") != "evented":
            continue
        cpu, wall, per_frame = walk_profile(prof, frame_names, args.marker)
        rows.append((prof.get("name", "?"), cpu, wall, per_frame))
    if not rows:
        print(f"no evented profiles in {args.speedscope}", file=sys.stderr)
        return 1
    rows.sort(key=lambda r: r[1], reverse=True)

    total_cpu = sum(r[1] for r in rows)
    print(f"{len(rows)} threads, {total_cpu:.1f} {unit} of '{args.marker}' across all of them\n")
    print(f"{'thread':<44}{'cpu':>12}{'wall':>12}  cpu/wall")
    for name, cpu, wall, _ in rows[: args.threads]:
        ratio = f"{cpu / wall:6.1%}" if wall else "     -"
        print(f"{name:<44}{cpu:12.1f}{wall:12.1f}  {ratio}")

    if args.thread:
        picked = [r for r in rows if args.thread in r[0]]
        if not picked:
            print(f"\nno thread name contains {args.thread!r}", file=sys.stderr)
            return 1
        chosen = picked[0]
    else:
        chosen = rows[0]

    print(f"\ntop {args.top} self-CPU frames on {chosen[0]} ({chosen[1]:.1f} {unit} CPU):")
    hot = sorted(chosen[3].items(), key=lambda kv: kv[1], reverse=True)[: args.top]
    for frame, cpu in hot:
        share = cpu / chosen[1] if chosen[1] else 0.0
        print(f"{cpu:10.1f}  {share:6.1%}  {frame}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
