# Pilot Phraseology

Reference for **what** the simulated pilots say on the radio and the **forms** each transmission
takes. Read this before touching the `PilotResponder.Build*` builders, `PhraseologyVerbalizer`,
`PilotSpeechText`, or any pilot wording — and before "fixing" a phrase that looks wrong (several
GA colloquialisms here are deliberate, see [Conventions](#conventions)).

This doc owns the **phraseology** (wording, AIM/7110.65 grounding, output forms). The delivery
**plumbing** — the per-frequency airtime queue, awaited-readback gating, request reminders, and
client TTS — lives in [`solo-training-pilot-speech.md`](solo-training-pilot-speech.md). The
controller-side STT (PTT → canonical command) is [`speech-recognition-pipeline.md`](speech-recognition-pipeline.md).

## Output forms: `PilotSpeechText`

Every pilot-speech builder returns a `PilotSpeechText` (or `PilotSpeechText?` where nullable),
with the forms built **independently** from the canonical command — terminal text is NEVER derived
by regex-stripping the spoken string.

```csharp
public sealed record PilotSpeechText(string Terminal, string Tts)
{
    public string? RpoTerminal { get; init; }
    public string TerminalForRpo => RpoTerminal ?? Terminal;
}
```

| Form | Audience | Style |
|------|----------|-------|
| `Terminal` | **solo student** (playing controller) and the default terminal/SAY transcript | compact: digit runway `8R`, digits for heading/altitude/speed, **no callsign in the message** (the SAY line's callsign column carries it) |
| `Tts` | spoken radio / Piper voice | phonetic: spelled runway "eight right", NATO callsign, ends with the spoken callsign |
| `RpoTerminal` | **RPO / instructor** terminal only | `Terminal` plus a diagnostic the solo student must not see — chiefly the lead/target callsign in traffic & follow calls. Defaults to `Terminal` (via `TerminalForRpo`) when there's no difference. |

The third form exists because a pilot — and therefore the solo student playing controller —
identifies traffic by the controller's position/type call, **not by callsign**. So in every
traffic-in-sight / follow transmission the spoken `Tts` and the solo `Terminal` say "the traffic"
("traffic in sight", "lost sight of the traffic", "unable to maintain separation, breaking off the
follow"), and the lead/target callsign survives only in `RpoTerminal` as an instructor aid.

`PhraseologyVerbalizer` produces the `Terminal`/`Tts` pair for **rule-backed readbacks** from one
switch: `Verbalize(cmd)` (spoken) and `VerbalizeTerminal(cmd)` (compact) share the `PhraseologyRule`
patterns + `PickPreferredRule`; only a per-capture formatter strategy differs. Pilot-only utterances
that have no controller-side rule (check-ins, "going around", in-sight/follow calls) are built
directly in `PilotResponder`.

## Routing the forms by mode

Three routers in `PilotResponder` send a `PilotSpeechText` to the right channel. The mode — not the
channel — decides whether the callsign diagnostic is shown.

| Router | Used by | Solo | RPO + `RpoShowPilotSpeech` | RPO default |
|--------|---------|------|-----------------------------|-------------|
| `RouteSoloOrRpoTransmission` | pattern reports, follow events, going-around, boundary holds (position-gated) | `PendingPilotTransmissions` (`Terminal`+`Tts`) if the student position is relevant, else warning `Terminal` | `PendingPilotSpeech` (`Tts`) | `PendingWarnings` (`TerminalForRpo`) |
| `RouteRpoSayReadback` | RTIS/RFIS in-sight readbacks | `QueueSoloPilotReadback` (`Terminal`+`Tts`) | `PendingPilotSpeech` (`Tts`) | `PendingPilotReadbacks` (`TerminalForRpo`) |
| `RouteRpoTransmission` (PilotSpeechText overload) | follow break-off, server maintain-contact loop | `PendingWarnings` (`Terminal`) | `PendingPilotSpeech` (`Tts`) | `PendingWarnings` (`TerminalForRpo`) |

Across every branch: the spoken (`Tts`) and solo (`Terminal`) forms never carry the lead/target
callsign; only the RPO-mode terminal branch resolves `TerminalForRpo`.

A string overload of `RouteRpoTransmission` also exists for callers that already hold a spoken
string plus a bespoke warning (`ContactCommandHandler`, `HoldingShortPhase`, `DownwindPhase` pass
`.Tts` + a custom warning). The well-known solo position lists are `SoloPositionsTower` (`["TWR"]`)
and `SoloPositionsTowerApproach` (`["TWR","APP"]`).

## Conventions

House rules every builder follows. **These are intentional — don't "correct" them.**

- **Callsign placement.** Initial check-ins put the callsign in the terminal text
  (`{facility}, {callsign}, …`). Readbacks and reports stay callsign-column-only (the SAY column
  shows it; the message doesn't repeat it).
- **Punctuation.** Terminal clauses are comma-separated; the commas double as TTS pacing hints.
- **Altitudes.** Stated to the nearest 100 ft; flight-level form at/above FL180. Use
  `AtcNumberParser.AltitudeToWords` (TTS) / `PhraseologyVerbalizer.CompactAltitude` (terminal).
- **Distances.** Cardinal group form for an "N-mile final" report (`AtcNumberParser.CardinalWord`
  → "three mile final", "ten mile final"). **Digit-by-digit** for position-relative distances in
  check-ins (`SpellDistanceDigits` → "one zero miles west"). These are different on purpose; see
  [Number & identifier spelling](#number--identifier-spelling).
- **Deliberate GA colloquialisms (NOT errors).** "short final", "inbound", "passing {fix}",
  "clear of runway X" (binary, no "fully"), paired airline flight numbers, "november …" for US GA
  tails, "with information Alpha" (accepted trainer ATIS-ack wording). Pilot transmissions are
  not held to controller phraseology — keep the colloquial wording.

## AIM-grounded rules

The decisions established by the phraseology audit, with the authority. Local copies:
`.claude/reference/faa/7110.65/` and `.claude/reference/faa/aim/` (read directly — do not web-search).

| Rule | Where | Cite |
|------|-------|------|
| Spoken own callsign appends **"heavy"/"super"** for those wake classes | `SpokenOwnCallsign` / `WakeClassSuffix` | AIM 4-2-4.a.5 |
| Airborne/arrival check-in states a rounded altitude + vertical-state verb (level / leaving X climbing\|descending Y / descend-via / climb-via), FL form ≥ FL180, callsign in terminal | `BuildAirborneCheckIn` / `BuildIfrAirborne` / `BuildVfrAirborne` | 5-3-1.b.2.a, 5-4-1.b.2, 5-2-9.b.9 |
| "with information {letter}" follows the field's ATIS; dropped when the primary field has no ATIS position | `ResolvePrimaryFieldAtisLetter` / `AtisInfoClause` (scenario `AtisLetter`, `ArtccConfigResolver.AirportHasAtis`) | AIM 4-2-3.a.3, 4-1-13 |
| Loss of an established visual is **"lost sight of …"**, never "negative contact" (that's traffic never acquired) | `BuildLostSightOfField` / `BuildLostSightOfTraffic` | 5-5-8/10/11 |
| A pilot **never speaks the lead/target callsign** in a traffic-in-sight or follow call (acquired by position, not callsign); callsign is an RPO diagnostic only | `BuildTrafficInSight` + all follow builders (`RpoTerminal`) | 5-5-10/11 |
| Pilots do **not** read back "caution wake turbulence" — it's a controller advisory | takeoff/landing-clearance readbacks | 4-4-7 |
| Fix-passage report says **"passing {fix}"** using the display name (terminal) / spoken name (TTS), not the raw fix id | `BuildAtFixReport` (`FixDisplayText`/`SpellFix`) | PCG REPORT- |
| Go-around: spoken call is just **"going around"**; the internal reason stays terminal-only | `BuildGoingAround` | 5-4-21, 5-5-5 |
| An arrival with no approach issued gives a brief **"request approach assignment, {callsign}"** — names neither runway nor approach type (ATC assigns both) | `BuildArrivalApproachRequest` | 4-1-8 |
| Mandatory readbacks keep the full taxi route (path + runway + cross-runway + hold-short) and the runway **L/R/C** suffix | `PhraseologyRules` TAXI rules + `SpellRunway`/`CompactRunway` | 4-4-7.b.4, 4-3-18 |

Note the arrival check-in vs approach-request distinction: an IFR arrival checks in with its
altitude and lateral state (and ATIS if held) — it does **not** request a runway (ATC assigns it).
A pure reminder when no approach has been issued is the callsign-only "request approach assignment".

## Builder catalog

Grouped by trigger. All return `PilotSpeechText`; follow/traffic builders set `RpoTerminal`.

- **Readbacks** — `BuildReadback(compound, aircraft)` (rule-driven, the bulk of readbacks);
  `BuildUnable` (rejected command, gated by `CommandDefinition.ProducesPilotUnable`).
- **Initial contact / check-in** — `BuildAirborneCheckIn` → `BuildIfrAirborne` / `BuildVfrAirborne`;
  `BuildReadyToTaxi`; `BuildClosedTrafficRequest`; `BuildArrivalApproachRequest`.
- **Position / pattern reports** — `BuildMidfieldDownwindReminder`, `BuildShortFinalReminder`,
  `BuildTurningLegReport`, `BuildMileFinalReport`, `BuildAtFixReport` (armed by `REPORT`).
- **Tower / ground** — `BuildHoldingShortTaxi`, `BuildHoldingShortCrossing`, `BuildClearOfRunwayText`,
  `BuildUnableToExit`, `BuildGoingAround`, `BuildApproachingMinimumsNoLandingClearance`.
- **Visual acquisition** — `BuildTrafficInSight`, `BuildFieldInSight`, `BuildLostSightOfTraffic`,
  `BuildLostSightOfField`.
- **Follow / sequencing** (all `RpoTerminal`) — `BuildTargetLanded`, `BuildUnableToCatchUp`,
  `BuildUnableToMaintainSeparation`, `BuildSequenceTightTurningBase`, `BuildSTurnsForSpacing`.

## Number & identifier spelling

| Helper | Output | Used for |
|--------|--------|----------|
| `AtcNumberParser.CardinalWord(n)` | cardinal: 5 → "five", 12 → "twelve" | "N-mile final" reports |
| `PilotResponder.SpellDistanceDigits(n)` | digit-by-digit: 10 → "one zero" | position-relative check-in distance ("one zero miles west") — pinned by `M102AirborneCheckInTests` |
| `PhraseologyVerbalizer.SpellRunway` | drops the padding leading zero: `08R` → "runway eight right" | runway designators (7110.65 §2-4-18.10) |
| `AtcNumberParser.AltitudeToWords` / `CompactAltitude` | "five thousand" / `5000` | altitudes |

Headings keep all three digits including leading zeros (`HeadingDigits`, §2-4-18.8) — a separate
path; don't route headings through the distance/cardinal helpers.

## Pitfalls

- **Don't put the lead/target callsign in `Terminal` or `Tts`** for traffic/follow calls — it goes
  in `RpoTerminal` only. The router resolves `TerminalForRpo` for the RPO branch.
- **Don't regex-strip the TTS form to make the terminal form.** Build both directly. If the RPO
  view needs a diagnostic the solo view shouldn't have, set `RpoTerminal`.
- **Don't merge `SpellDistanceDigits` into `CardinalWord`.** They're different phraseology
  (digit-by-digit position distance vs cardinal mile-final) and the airborne-check-in form is
  test-pinned.
- **Cross-repo:** `BuildTrafficInSight` / `BuildLostSightOfField` are also called from yaat-server's
  `TickProcessor` maintain-contact loop. A signature change there must land in both repos.
- **Aviation changes need review.** Anything touching what a pilot says is aviation logic — use
  `aviation-sim-expert` (or a direct steer from the domain owner) and cite the AIM/7110.65, don't
  guess. A narrow, explicit instruction from the domain owner doesn't need a separate second opinion.
