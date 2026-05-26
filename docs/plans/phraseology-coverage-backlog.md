# Phraseology coverage backlog

Produced by the systematic audit described in [`phraseology-coverage-audit.md`](./phraseology-coverage-audit.md). One section per audited FAA chapter; one entry per distinct controller or pilot phrasing.

## Entry format

Every entry uses these four fields in this order. No prose. Keep entries scannable.

```markdown
### §X-Y-Z (or AIM Y-Z-W) — Brief topic

- **Status:** Covered | MissingRule | MissingCanonical | OutOfScope
- **Phrasing:** "EXACT FAA WORDING WITH (variables) IN PARENS"
- **Canonical:** existing or proposed canonical command (`CTO`, `CM 5000`, `EF 28R`, etc.). Use `??` if MissingCanonical and no obvious shape yet.
- **Notes:** one line — for Covered, point at the rule line (e.g. `PhraseologyRules.cs:142`); for MissingRule, name the verb to extend; for MissingCanonical, sketch the new `ParsedCommand` shape; for OutOfScope, why.
```

### Conventions

- **Multiple variants of one phrasing** (e.g. "cleared for takeoff" vs "clear for takeoff") get ONE entry unless variants reveal a gap.
- **Word-order variants** (suffix vs prefix runway) only get a separate entry when one is covered and the other isn't.
- **Phrasings spanning multiple sections** get one entry under the primary section, with `Notes: also at §A-B-C`.
- **Compound clearances** (e.g. "cleared to land, hold short of") may produce one entry if a rule handles the compound, or two entries if each clause needs separate handling.

## Status definitions

| Status | Meaning | Effort to ship |
|---|---|---|
| **Covered** | Rule exists, matches this phrasing (with allowance for `{rwy}` / `{alt}` / etc. captures). | None — already done. |
| **MissingRule** | Canonical command exists in `CommandRegistry`; just no rule produces it from this phrasing. | One PR: rule + test + verbalizer regression check. |
| **MissingCanonical** | No existing canonical command supports this intent. New `ParsedCommand` record + dispatcher + verbalizer + rule needed. | Medium-large PR. Often defer. |
| **OutOfScope** | Section is about workflow, equipment, charting, etc. — not phraseology. | N/A. |

---

## 7110.65 — Controller-side

### Chapter 3 — Airport Traffic Control

<!-- empty — agent fills this in -->

### Chapter 4 — IFR (TRACON / approach control)

<!-- empty -->

### Chapter 5 — Radar

<!-- empty -->

### Chapter 7 — Visual

<!-- empty -->

### Chapter 2 — General Control

<!-- empty -->

### Chapter 6 — Nonradar

<!-- empty -->

### Chapter 9 — Special Flights

<!-- empty -->

---

## AIM — Pilot-side

### Chapter 4 — Air Traffic Control

<!-- empty -->

### Chapter 5 — Air Traffic Procedures

<!-- empty -->

### Chapter 10 — Helicopter Operations

<!-- empty -->

---

## Summary

Filled in by the final agent session once every chapter checkbox in [`phraseology-coverage-audit.md`](./phraseology-coverage-audit.md) is ticked.

| Bucket | Count |
|---|---|
| Covered | — |
| MissingRule | — |
| MissingCanonical | — |
| OutOfScope | — |
| **Total phrasings audited** | — |

Implementation sessions pull from MissingRule first (cheapest, deterministic); MissingCanonical entries are deferred to product / milestone planning.
