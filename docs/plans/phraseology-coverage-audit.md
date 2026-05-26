# Phraseology coverage audit — handoff plan

## Context

YAAT's speech recognition pipeline (rule mapper at `src/Yaat.Sim/Speech/PhraseologyMapper.cs`, verbalizer at `src/Yaat.Sim/Pilot/PhraseologyVerbalizer.cs`) currently covers ~360 hand-authored phraseology rules in `src/Yaat.Sim/Speech/PhraseologyRules.cs`. Recent work has been **reactive** — gaps surface from real `*.yaat-speech-sample.zip` bundles and we patch them one at a time (e.g. runway-prefix word order for tower clearances, make-straight-in for VFR pattern entries).

We want to flip this to a **systematic** pass: walk every phraseology-bearing chapter of FAA 7110.65 and AIM, enumerate every distinct controller and pilot phrasing, compare against existing rules, and produce a triage backlog. The backlog feeds future implementation sessions (one or many) — this audit itself ships **no code changes**.

The decision to keep this report-only is deliberate: a fresh agent reading and triaging 10 chapters in one pass would run out of context if they also tried to write tests + rules + cross-repo verifications. Triage and implementation are separate jobs.

## Goal

Produce `docs/plans/phraseology-coverage-backlog.md` — one section per audited chapter, every distinct phrasing classified as **Covered / MissingRule / MissingCanonical / OutOfScope**, with the FAA citation, the closest existing canonical (if any), and a one-line proposal.

That document then drives subsequent implementation sessions — each session picks a cluster from MissingRule (cheapest), validates the FAA citation via `aviation-realism-review`, writes failing tests, adds rules, ships.

## Scope

**In scope** (per the user's product decision):
- Controller-side phraseology — what controllers say (drives the rule mapper)
- Pilot-side phraseology — what pilots say (drives the verbalizer + `PilotResponder`)
- Edge / less-common procedures — helo, military, special-VFR, formation (flag and defer, don't try to ship)

**Out of scope** (skip entirely):
- Emergency / IRROPS phraseology (mayday, lost-comm, NORDO, runway incursion alerts) — bigger surface, ties into sim behavior beyond speech, separate effort
- Non-phraseology content: controller workflow rules, weather minima, equipment standards, charting conventions, medical, etc.
- Implementation of any fix this pass surfaces — strictly triage

## Method (assess coverage programmatically)

For each chapter under audit, follow this sequence. Don't deviate — the deterministic procedure is what makes results comparable across chapters.

### Step 1 — read the chapter's section index

```
.claude/reference/faa/7110.65/INDEX.md     # find the chapter's section files
.claude/reference/faa/aim/INDEX.md
```

Open every section file in the chapter (e.g. `chap03_sec01.md` through `chap03_sec11.md`) and skim for phraseology blocks. FAA documents render canonical phraseology in `BOLD UPPERCASE` (7110.65) or `BOLD CAPS` (AIM). PHRASEOLOGY/EXAMPLE annotations call out the exact wording.

### Step 2 — build the existing-rule index

Read `src/Yaat.Sim/Speech/PhraseologyRules.cs` once at the start of each chapter audit. For each rule, note:
- Literal tokens (everything that isn't `{capture}` or `literal?`)
- Canonical output template
- Handler enum (which `CanonicalCommandType`)

A scratch table (not committed) helps:

```
canonical | tokens                              | line
CTO       | cleared for takeoff                 | 142
CTO       | clear for takeoff                   | 143
CTO       | cleared for takeoff runway {rwy}    | 140
CTO       | runway {rwy} cleared for takeoff    | 138
...
```

For the verbalizer side, also read `CommandRegistry` (in `src/Yaat.Sim/Commands/CommandRegistry.cs`) to know which canonical commands exist at all — a phrasing without a backing canonical command falls into MissingCanonical, not MissingRule.

### Step 3 — for each FAA phrasing, classify

Walk the chapter's phrasings. For each, decide:

| Bucket | Definition |
|---|---|
| **Covered** | A rule's literal tokens already match this phrasing (allowing for runway/altitude/etc captures). Note the rule line for the backlog. |
| **MissingRule** | The canonical command exists in `CommandRegistry`, but no rule produces it from this phrasing. Cheapest to fix — one or two new entries in `PhraseologyRules.cs`. |
| **MissingCanonical** | No existing canonical command supports this phrasing's intent. Requires adding a `ParsedCommand` record + dispatcher path + verbalizer entry + rule. Medium-to-large effort; flag and defer. |
| **OutOfScope** | The section talks about controller workflow, equipment, etc. — not phraseology. Don't enumerate. |

When in doubt between MissingRule and MissingCanonical, **MissingRule** is the bet — adding a rule with `??` placeholder canonical is reversible if it turns out the canonical doesn't exist.

### Step 4 — write the backlog section

Append to `docs/plans/phraseology-coverage-backlog.md` using the template in that file's header. One entry per phrasing. Keep entries terse: `Status:` → `FAA: §X-Y-Z` → `Phrasing: "..."` → `Canonical: ...` → `Notes: ...` (1 line each).

For Covered entries that already have multiple variants, ONE backlog entry mentioning the variants is enough — don't enumerate every form unless variants reveal a gap (e.g. "covered as suffix only, prefix form missing").

### Step 5 — mark the chapter done

In this plan file, tick the chapter's checkbox in the **Per-chapter task list** below. Run a quick sanity check: scan your new backlog section, ensure every entry has the four fields, no entry has a placeholder "TODO".

## Chunking and stop-conditions

- **One chapter per session** is the budget. 7110.65 chapter 3 alone has ~70 phrasings; expect ~400 phrasings across all in-scope chapters. Trying to ship multiple chapters in one session loses fidelity.
- **Stop after 90 minutes** of session time even if a chapter is unfinished — write what you have, leave a `<!-- INCOMPLETE: stopped at §X-Y-Z -->` marker, tick the chapter only when complete.
- **No deep dives** — if a phrasing is ambiguous (rare military formation phrasing, etc.), classify as MissingCanonical with `Notes: needs deeper review`, don't research it.

## Per-chapter task list

Order: 7110.65 first (controller-side is the bigger gap surface), AIM after. Within 7110.65, sequence by likely yield — Ch 3 (tower) and Ch 4 (TRACON) are the highest-traffic chapters.

### 7110.65 (controller-side)
- [ ] Chapter 3 — Airport Traffic Control (tower)
- [ ] Chapter 4 — IFR (TRACON / approach control)
- [ ] Chapter 5 — Radar
- [ ] Chapter 7 — Visual (visual approaches, pattern, VFR ops)
- [ ] Chapter 2 — General Control (safety alerts, equipment status, common verbs)
- [ ] Chapter 6 — Nonradar
- [ ] Chapter 9 — Special Flights (helo, military, formation — edge cases; flag-and-defer expected)

### AIM (pilot-side)
- [ ] Chapter 4 — Air Traffic Control (pilot communications, position reports)
- [ ] Chapter 5 — Air Traffic Procedures (departure/arrival pilot phrasings)
- [ ] Chapter 10 — Helicopter Operations (helo pilot phrasings — edge cases)

## Recent work to NOT re-flag

These were shipped in the current `## Unreleased` cycle. Skim `CHANGELOG.md` and `git log --oneline -10` before starting a chapter — if a backlog entry would duplicate work that just landed, skip it.

- CTO / CLAND / LUAW / TG / LAHSO / MLT / MRT / ELD / ERD / ELB / ERB — runway-prefix word order accepted (FAA §3-7-1, §3-9-9, §3-9-7, §3-10-3, §3-10-4, §3-11-2).
- EF (Enter Final) — "make/enter straight-in [approach] runway X" + bare forms (AIM §4-3-3, FAA §3-10-4).
- N-number GA tail-number callsigns — Whisper near-miss suffix letters (gulf → golf etc) recovered via `NatoNearMissResolver.TryResolveSingle`.

## Tools / skills the agent will need

| Tool | When |
|---|---|
| Read / Grep over `.claude/reference/faa/{7110.65,aim}/` | Step 1 — walking the chapter |
| Read of `src/Yaat.Sim/Speech/PhraseologyRules.cs` | Step 2 — building the rule index |
| Read of `src/Yaat.Sim/Commands/CommandRegistry.cs` | Step 2 — knowing what canonicals exist |
| Read of `src/Yaat.Sim/Commands/ParsedCommand.cs` | When deciding MissingRule vs MissingCanonical |
| `aviation-realism-review` skill | Optional — for ambiguous FAA citations. Don't invoke for every entry; only when the phrasing seems off |
| `stt-pipeline-debugging` skill | NOT for this pass — that skill is for implementation. This audit is triage-only |

## Output format — see template at top of backlog file

The backlog file (`docs/plans/phraseology-coverage-backlog.md`) has the template and current state. Do not invent a new format here — the audit is comparable across chapters only if the format is identical.

## Done definition

**Per chapter:** every in-scope section walked, every phrasing classified, backlog entry written, checkbox ticked above.

**Overall (all 10 chapters):** backlog file populated end to end, no INCOMPLETE markers, a final summary block at the bottom of the backlog tallying counts (Covered / MissingRule / MissingCanonical / OutOfScope) for the whole audit.

After the overall audit completes, archive this plan to `docs/plans/archive/phraseology-coverage-audit.md` and surface the backlog summary to the user. Implementation sessions then pick MissingRule clusters from the backlog one at a time.
