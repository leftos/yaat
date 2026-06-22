# Pilot Check-in & TTS Phraseology — AIM Audit

Systemic, area-by-area review of every pilot transmission YAAT emits, against the FAA AIM
(and 7110.65 where it governs pilot phraseology). Grounded by reading the actual strings each
`Build*` method emits in `src/Yaat.Sim/Pilot/PilotResponder.cs`, with helpers in
`PhraseologyVerbalizer.cs`, `PilotSayBuilder.cs`, and number/altitude spelling in
`src/Yaat.Sim/Speech/AtcNumberParser.cs`. Each builder returns a `PilotSpeechText(Terminal, Tts)`
— the terminal-display string and the spoken (TTS) string are independent.

> Status legend: **DONE** (fixed in the check-in pass), **TODO** (open), severity **HIGH / MED / LOW**.

## Conventions (house rules established by this audit)
- **Callsign in terminal:** initial check-ins include the callsign in the terminal text
  (`{facility}, {callsign}, …`). Readbacks remain callsign-column-only (callsign shown in the
  terminal's callsign column, not repeated in the message).
- **Punctuation:** terminal clauses are comma-separated; commas double as TTS pacing hints.
- **Altitudes:** stated to the nearest 100 ft; flight-level form at/above FL180.
- **Deliberate GA colloquialisms (NOT findings):** "short final", "inbound", "passing {fix}",
  "clear of runway X" (binary, no "fully"), paired airline flight numbers, "november…" for US GA
  tails, "with information Alpha" (accepted trainer ATIS-ack colloquialism).

## Findings

| # | Area | Finding | Current → AIM-correct | Cite | Sev | Status |
|---|------|---------|------------------------|------|-----|--------|
| A1-1 | Initial contact / check-ins | Airborne check-in said "level" unconditionally; no rounding/FL; terminal omitted callsign | vertical-state verb (level / leaving X climbing\|descending Y / descend-via / climb-via), round-to-100 + FL, callsign in terminal | 5-3-1.b.2.a, 5-4-1.b.2, 5-2-9.b.9 | HIGH | **DONE** |
| A8-1 | Callsign pronunciation | `IcaoToSpoken` never appends **"heavy"/"super"** — affects *every* heavy/super transmission | "American Twenty Two" → "American Twenty Two heavy" | 4-2-4.a.5 | HIGH | **DONE** |
| A1-2 | Initial contact | `"with information Alpha"` ATIS letter is **hardcoded in ~6 sites** | drive from the live scenario ATIS letter; suppress when no ATIS exists (keep the colloquial "with information" wording) | 4-2-3.a.3, 4-1-13 | HIGH | **DONE** |
| A5-1 | Traffic / visual | `BuildLostSightOfField` / `BuildLostSightOfTraffic` say **"negative contact"** (a radio-contact term, not loss of visual) | "lost sight of the field/traffic" / "no joy" | 5-5-8/10/11 | HIGH | **DONE** |
| A4-1 | Mandatory readbacks | TAXI readback completeness — hold-short / crossing only echoed if a matching rule variant exists | every (path, runway, hold-short, cross-runway) capture must be read back | 4-4-7.b.4, 4-3-18 | HIGH | **DONE**¹ |
| A4-2 | Mandatory readbacks | Runway **L/R/C** must never be dropped from assignment readbacks | preserve the suffix | 4-4-7.b.4 | HIGH/MED | **DONE**¹ |
| A4-6 | Mandatory readbacks | **"caution wake turbulence"** appended to the *pilot* readback (it's a controller advisory) | remove from pilot speech | 4-4-7 | MED-HIGH | **DONE** |
| A3-4 | Position / pattern reports | `BuildAtFixReport` uses the raw fix id ("passing VPCBT") | `SpellFix` (TTS) / `FixDisplayText` (terminal) → "passing Lake Chabot" | PCG REPORT- | MED | TODO |
| A1-3 | Initial contact | `BuildArrivalApproachRequest` "{N} miles to land runway X" is invented phraseology | use an AIM form | 4-2 | MED | TODO |
| A6-1 | Ground / taxi | `BuildHoldingShortTaxi` "{label} at {taxiway}" lacks the "holding short of" verb | add the verb | 4-3-18 | MED | TODO |
| A6-2 | Ground / taxi | `BuildReadyToTaxi` omits op-type / destination | "IFR to Chicago, ready to taxi" | 4-3-18.4.a | MED | TODO |
| A7-1 | Go-around / missed | `BuildGoingAround` speaks the internal parenthetical reason into TTS | reason terminal-only / real intent phrase | 5-4-21, 5-5-5 | MED | TODO |
| A5-2 | Output contract | Several builders return a raw bracketed `string`, bypassing the `PilotSpeechText` dual-output | route through `PilotSpeechText` | — | MED | TODO |
| A1-4 | Other altitude reporters | `BuildClosedTrafficRequest` renders raw feet (same class as A1-1) | round-to-100 + FL | 2-4-3.b | MED | TODO |
| A9-1 | Punctuation / pacing | callsign placement / terminal-include convention varies per builder | apply the house convention (above) repo-wide | 4-2-3.a | MED | TODO |
| A8-2 | Cleanup | three distance spellers (`MilesToWords` / `SpellMiles` / `SpellDistanceDigits`) | consolidate | — | LOW | TODO |

¹ **Verified — no gap found.** Enumerated the `PhraseologyRules.cs` TAXI rules: all eight
combinations of `{rwy?, crossrwy?, holdshort?}` × `{path}` exist (lines ~665–696), and
`PhraseologyVerbalizer.PickPreferredRule` selects the richest rule whose captures are all
satisfied — so a full taxi clearance reads back path + destination runway + cross-runway +
hold-short, none dropped. `SpellRunway`/`CompactRunway` retain the L/R/C suffix (R→"right",
L→"left", C→"center"). No production change was required; regression tests now lock in both
behaviours (`BuildReadback_TaxiWithRunwayCrossAndHoldShort_ReadsBackEveryCapture`,
`BuildReadback_TaxiPreservesCenterRunwaySuffix`).

## Suggested fix sequence (high → low)
1. A8-1 heavy/super suffix in spoken callsign
2. A1-2 ATIS letter data-driven (6 sites)
3. A5-1 "negative contact" → "lost sight of" / "no joy"
4. A4-1 / A4-2 TAXI readback completeness + L/R/C retention (after verifying `PhraseologyRules.cs`)
5. A4-6 drop "caution wake turbulence" from pilot readback
6. A3-4 fix-passage report uses spoken/display name
7. A1-3 / A6-1 / A6-2 / A7-1 / A1-4 phraseology cleanups
8. A5-2 / A9-1 dual-output + punctuation consistency
9. A8-2 distance-speller consolidation

Each fix lands area-by-area in its own commit with TDD tests. Promote this doc to a permanent
`docs/pilot-phraseology-audit.md` once the high-severity items are worked through.
