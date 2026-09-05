"""Predict the ceiling on a per-test-parallel scheduler (TUnit) vs xunit's per-collection model.

xunit runs COLLECTIONS in parallel and the tests inside one collection sequentially; the default
collection is the class. So a class holding several slow tests serialises them. TUnit schedules
per test. The gap between the two makespans is the entire scheduling prize - if it is small, TUnit
cannot win on parallelism no matter how good its discovery is.

Both models are simulated with LPT (longest-processing-time-first) onto N workers, which is the
standard greedy schedule and within 4/3 of optimal.
"""

import argparse
import heapq
import os
import re
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
parser.add_argument("trx", help="TRX report with per-test durations (--report-xunit-trx / --report-trx)")
parser.add_argument("-w", "--workers", type=int, default=os.cpu_count() or 16, help="parallel workers to simulate")
parser.add_argument("-t", "--top", type=int, default=15, help="how many heaviest classes to list")
args = parser.parse_args()

TRX = args.trx
WORKERS = args.workers

DUR = re.compile(r"^(\d+):(\d+):(\d+(?:\.\d+)?)$")


def secs(text):
    m = DUR.match(text or "")
    if not m:
        return 0.0
    h, mi, s = m.groups()
    return int(h) * 3600 + int(mi) * 60 + float(s)


def class_of(name):
    # "Ns.Class.Method(args...)" -> "Ns.Class". Strip arguments first so a '.' inside a
    # parameter value cannot be mistaken for the method separator.
    base = name.split("(", 1)[0]
    return base.rsplit(".", 1)[0] if "." in base else base


tests = []
for _event, el in ET.iterparse(TRX, events=("end",)):
    if el.tag.endswith("UnitTestResult"):
        n = el.get("testName")
        if n:
            tests.append((n, secs(el.get("duration"))))
        el.clear()

total = sum(d for _, d in tests)
classes = {}
for n, d in tests:
    c = class_of(n)
    classes[c] = classes.get(c, 0.0) + d


def lpt(jobs, workers):
    """Greedy longest-first packing onto `workers` machines; returns makespan."""
    heap = [0.0] * workers
    heapq.heapify(heap)
    for j in sorted(jobs, reverse=True):
        t = heapq.heappop(heap)
        heapq.heappush(heap, t + j)
    return max(heap)


per_test = lpt([d for _, d in tests], WORKERS)
per_class = lpt(list(classes.values()), WORKERS)
max_test = max(d for _, d in tests)
max_class = max(classes.values())

print(f"tests                {len(tests)}")
print(f"classes              {len(classes)}")
print(f"total CPU-seconds    {total:8.1f}s")
print(f"workers              {WORKERS}")
print()
print(f"perfect divisibility {total / WORKERS:8.1f}s   (total / workers - unreachable lower bound)")
print(f"longest single test  {max_test:8.1f}s   (hard floor for ANY scheduler)")
print(f"longest single class {max_class:8.1f}s   (hard floor for a per-CLASS scheduler)")
print()
print(f"LPT per-class  (xunit model) {per_class:8.1f}s")
print(f"LPT per-test   (TUnit model) {per_test:8.1f}s")
gain = per_class - per_test
pct = (gain / per_class * 100) if per_class else 0.0
print(f"scheduling prize             {gain:8.1f}s  ({pct:.1f}%)")
print()
print("Heaviest classes by serial time (these are what a per-class scheduler cannot split):")
counts = {}
for n, _ in tests:
    c = class_of(n)
    counts[c] = counts.get(c, 0) + 1
for c, d in sorted(classes.items(), key=lambda kv: kv[1], reverse=True)[: args.top]:
    print(f"  {d:7.1f}s  {counts[c]:4d} tests  {c.split('.')[-1]}")
