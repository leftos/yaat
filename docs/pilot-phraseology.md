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

`PhraseologyVerbalizer` produces the `Terminal`/`Tts` pair for **rule-backed readbacks** from one switch: `Verbalize(cmd)` (spoken) and `VerbalizeTerminal(cmd)` (compact) share the `PhraseologyRule` patterns + `PickPreferredRule`; only a per-capture formatter strategy differs.

Only the spoken form takes load-band variant rows (a seeded pick among the band's `PilotVariants`, each row held to the value floor `AviationFloor.HoldsTemplate` at test time) and the quiet flavour (not added to a readback with a clause spoken from a rule that has Quiet rows); speeds are separate digits everywhere (`SpeedWords`), and the group form (`SpeedGroupWords`, `AltitudeGroupWords`) only through a busy row's `{spd:group}` / `{alt:group}`.

`VerbalizeTerminal(cmd)` has no personality or band, so the Terminal and RPO text are always canonical.

The expedite, the altitude restriction (`maintain at or above|at or below`) and the cross-fix restriction are built directly in `PilotResponder` / `PhraseologyVerbalizer` but take their spoken variant rows from `BuilderVariantSet`s (`PhraseologyRules.BuilderVariantSets`) through `VerbalizeBuilderClause`, held to the same floor; a cross-fix restriction with a speed says it after the altitude in every form.

Six more sets cover the approach readbacks (`ApproachClearance`, `ApproachClearanceNamingNoType`, `StraightInApproachClearance`, `ApproachJoin`, `StraightInApproachJoin`, `ExpectedApproach`) three more the visual and follow readbacks (`VisualApproachClearance`, `AirportVisualApproachClearance`, `FollowTrafficAirborne`) and six the PTAC readback (`PtacTurn{Climb,Descend,Maintain}` with a turn direction, `PtacFlyHeading{Climb,Descend,Maintain}` without), 28 in all.

An approach clearance or join is built by `ApproachCommandHandler`, so its set and spoken captures ride on the `HandlerReadback` (`Variants`, `Captures`) and `PilotResponder.BuildClause` varies only the clause's final part, keeping a route or crossing before it canonical.

A readback of two or more commands (a CAPP with a route or crossing before it included) is chained: it never takes a chatty row or the quiet "alright," / ", thanks" flavour, a quiet frequency speaks the normal-band words and a busy one keeps its terse rows on every clause; a single command, conditioned or not, keeps its band.

A `CVA` naming its runway, an airborne `FOLLOW` and a `PTAC`/`PTACF` are built from the command alone (`VariedBuilderClause`); the airport-level `CVA` rides on its `HandlerReadback`, as does the runway form's first block (`ApproachCommandHandler.RunwayVisualReadback`) and the `PTAC` readback of `TryPtac`. A `PTAC` takes the set of its heading kind (turn direction or fly heading) and its vertical verb (climb, descend, maintain), chosen by `ApproachCommandHandler.PtacVariants`.

The visual's pattern side and follow tail are comma-led captures (`{side}`, `{follow}`: ", left traffic", ", follow the traffic", or empty) that the renderer attaches without a space and that keep the next comma when empty; a set's pattern token may carry a trailing comma (`{apt},`). Pilot-only utterances that have no controller-side rule (check-ins, "going around", in-sight/follow calls) are built directly in `PilotResponder`.

## Routing the forms by mode

Three routers in `PilotResponder` send a `PilotSpeechText` to the right channel, `RouteRpoTransmission` in two overloads. The mode — not the channel — decides whether the callsign diagnostic is shown.

| Router | Used by | Solo | RPO + `RpoShowPilotSpeech` | RPO default |
|--------|---------|------|-----------------------------|-------------|
| `RouteSoloOrRpoTransmission` | pattern reports, follow events, going-around, boundary holds (position-gated) | `PendingPilotTransmissions` (`Terminal`+`Tts`) if the student position is relevant, else warning `Terminal` | `PendingPilotSpeech` (whole `PilotSpeechText`, shown as `TerminalForRpo`) | `PendingWarnings` (`TerminalForRpo`) |
| `RouteRpoSayReadback` | RTIS/RFIS in-sight readbacks | `QueueSoloPilotReadback` (`Terminal`+`Tts`) | `PendingPilotSpeech` (whole `PilotSpeechText`, shown as `TerminalForRpo`) | `PendingPilotReadbacks` (`TerminalForRpo`) |
| `RouteRpoTransmission(aircraft, solo, rpoShow, text)` (4-arg) | follow break-off, server maintain-contact loop | `PendingWarnings` (`Terminal`) | `PendingPilotSpeech` (whole `PilotSpeechText`, shown as `TerminalForRpo`) | `PendingWarnings` (`TerminalForRpo`) |
| `RouteRpoTransmission(aircraft, solo, rpoShow, text, warningText)` (5-arg) | `ContactCommandHandler`, `HoldingShortPhase`, `DownwindPhase` (a bespoke warning) | `PendingWarnings` (`warningText`) | `PendingPilotSpeech` (whole `PilotSpeechText`, shown as `TerminalForRpo`) | `PendingWarnings` (`warningText`) |

`PendingPilotSpeech` is a `List<PilotSpeechText>`: it keeps both forms, the terminal, speech bubble and terminal history show the written `TerminalForRpo` line, and `Tts` rides along unused because RPO has no TTS consumer. Across every other branch: the spoken (`Tts`) and solo (`Terminal`) forms never carry the lead/target callsign; only the RPO-mode terminal branches resolve `TerminalForRpo`.

The well-known solo position lists are `SoloPositionsTower` (`["TWR"]`) and `SoloPositionsTowerApproach` (`["TWR","APP"]`).

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
- **Runway-segment taxi echo shows the single cleared end, not the reciprocal.** A taxi route with a
  runway segment (e.g. `TAXI 28R G D`) renders "on runway two eight right" / terminal "on 28R" — never
  the internal combined centerline id `28R/10L`. `TaxiRoute.ToSummary(turnHints, clearedRunways)` resolves
  the displayed designator via `seg.Edge.Edge.MatchesRunway(d)` against the cleared path
  (`GroundCommandHandler` passes `taxi.Path`); with no command context (snapshot / Aircraft List) it falls
  back to the de-padded combined id `28R/10L`.

### Readback of a rewritten clearance

`SimulationEngine.SendCommand` builds the solo readback with `PilotResponder.BuildReadbackAsApplied(compound, result.EffectiveCommand, …)`.

When a handler applied a rewritten command — `GroundCommandHandler.TryTaxi` dropping a cleared taxiway (the unreachable gate lead-out lane, issue #396, or the contradictory via before a `@parking` destination) — it returns `CommandResult.EffectiveCommand`, and the readback verbalizes *that* route with the omitted pavement named first: terminal `unable M4, taxi via M1 A A1 runway 1R`, spoken `unable mike four, taxi via mike one, alpha, …`.

A crew never reads back pavement it will not use, and the hearback check only works if the controller hears the substitution. A null `EffectiveCommand` reads back exactly as issued (`BuildReadback`).

## AIM-grounded rules

The decisions established by the phraseology audit, with the authority. Local copies:
`.claude/reference/faa/7110.65/` and `.claude/reference/faa/aim/` (read directly — do not web-search).

| Rule | Where | Cite |
|------|-------|------|
| Spoken own callsign appends **"heavy"/"super"** for those wake classes | `SpokenOwnCallsign` / `WakeClassSuffix` | AIM 4-2-4.a.5 |
| A readback may close with the **abbreviated N-number** ("november three alpha bravo") once the controller has acknowledged the contact and while the aircraft is on the student's frequency, never on the readback to the acknowledgement, to an airline/foreign callsign, or beside an aircraft with the same last two characters; the wake suffix still follows | `SpokenReadbackCallsign` / `SpokenFormDraws.CanShortenCallsign` / `CallsignParser.ShortenedNNumberSpoken` | AIM 4-2-4.a.1-a.3, 7110.65 2-4-9 |
| A spoken contact readback may **group the frequency** without the leading 1 ("twenty five thirty five"; one decimal keeps "point"), Busy only in practice; the Terminal keeps "125.35" | `PhraseologyVerbalizer.FrequencyToWords(double, bool)` / `SpokenFormDraws.DrawGroupedContactFrequency` | 7110.65 2-4-17.k.1 |
| Airborne/arrival check-in states a rounded altitude + vertical-state verb (level / leaving X climbing\|descending Y / descend-via / climb-via), FL form ≥ FL180, callsign in terminal | `BuildAirborneCheckIn` / `BuildIfrAirborne` / `BuildVfrAirborne` | 5-3-1.b.2.a, 5-4-1.b.2, 5-2-9.b.9 |
| "with information {letter}" follows the field's ATIS; dropped when the primary field has no ATIS position | `ResolvePrimaryFieldAtisLetter` / `AtisInfoClause` (scenario `AtisLetter`, `ArtccConfigResolver.AirportHasAtis`) | AIM 4-2-3.a.3, 4-1-13 |
| Loss of an established visual is **"lost sight of …"**, never "negative contact" (that's traffic never acquired) | `BuildLostSightOfField` / `BuildLostSightOfTraffic` | 5-5-8/10/11 |
| A pilot **never speaks the lead/target callsign** in a traffic-in-sight or follow call (acquired by position, not callsign); callsign is an RPO diagnostic only | `BuildTrafficInSight` + all follow builders (`RpoTerminal`) | 5-5-10/11 |
| Pilots do **not** read back "caution wake turbulence" — it's a controller advisory | takeoff/landing-clearance readbacks | 4-4-7 |
| A takeoff clearance given while **holding short of a taxiway** names where the departure starts: an intersection departure reads **"cleared for takeoff runway 28R at echo"** (terminal `… runway 28R at E`), and a clearance stored while the aircraft stays held (the taxiway does not reach the runway) reads **"cleared for takeoff runway 28R, holding short of echo"**. A route that reaches the runway at another entry reads the plain clearance. The "immediate" lead and the departure instruction carry through unchanged; no distance-remaining phrase is spoken | `CommandDispatcher.ApplyClearedForTakeoff` → `PilotResponder.BuildTakeoffClearanceAtTaxiwayHold` (outcome `TaxiwayHoldTakeoffOutcome`) | AIM 4-3-10 |
| Fix-passage report says **"passing {fix}"** using the display name (terminal) / spoken name (TTS), not the raw fix id | `BuildAtFixReport` (`FixDisplayText`/`SpellFix`) | PCG REPORT- |
| Go-around: spoken call is just **"going around"**; the internal reason stays terminal-only — EXCEPT a lost-visual go-around, whose reason is regulatorily required on the air ("include the reason" for a pilot-initiated missed) | `BuildGoingAround` / `BuildGoingAroundLostVisualReference` | 5-4-21, 5-5-5.a.2 |
| Rejected takeoff: with the roll underway the pilot says **"aborting takeoff"** (P/CG ABORT — "to terminate a preplanned aircraft maneuver; e.g., an aborted takeoff"; "reject"/RTO stays the code name) and SPEAKS the reason on the auto trigger ("traffic on the runway"); from a **standstill** there is no maneuver to abort, so the pilot **declines** the clearance instead ("unable, traffic on the runway" — §3-9-6.a is a "does not begin takeoff roll" rule). Once stopped, **"stopped on the runway, standing by"** cues the controller that the runway is blocked and the aircraft awaits an exit/taxi instruction (AIM 4-3-18.a). A CTOC-commanded abort doesn't restate a reason (issue #417 tracks carrying the §3-9-11 reason on CTOC itself) | `BuildRejectingTakeoffTrafficOnRunway` / `BuildUnable` / `BuildStoppedOnRunway` | P/CG ABORT, 3-9-6.a, 3-9-11, AIM 4-3-18.a |
| An intercept the aircraft cannot fly is **refused on frequency**, not logged as a grey notification, and the refusal states only what happened: crossing the final approach course beyond the category cut says **"unable, passing through the localizer"**, while the 180 s timeout — the aircraft flew parallel and never reached the course — says **"unable to intercept the localizer, request vectors"** (AIM §5-5-16.a.1 reason → unable → request), never claiming a position it never held. Both drop the clearance. The procedure id is an RPO-only diagnostic (`RpoTerminal`): a crew reports what it is doing, not the id it was cleared for | `InterceptCoursePhase.HandleBustThrough` / `HandleInterceptTimeout` → `BuildUnable` / `BuildUnableToInterceptRequestVectors` + `RouteSoloOrRpoTransmission` | 5-9-2, TBL 5-9-1, AIM 5-5-16.a.1 |
| A landed aircraft at an airport with **no ground map** rolls out to a stop *on* the runway and says **nothing** — not "clear of runway" (AIM 4-3-21.b: clear means every part of the aircraft past the runway edge, and there is no taxiway to name). Product decision: the aviation review asked for the RTO "stopped on the runway, standing by" cue here, but under every auto-delete mode except `Never` the aircraft is removed a second later, so the call would be noise on every layout-less arrival; the phase cannot see the mode, so it stays silent | `HoldingAfterExitPhase.OnStart` (no-layout branch) | AIM 4-3-21.b, 4-3-18.a |
| An aircraft stopped **on the runway with no exit ahead** reports once and asks for a way off: **"unable to exit, request back-taxi to {twy}."** naming the nearest free exit behind it, or **"unable to exit the runway."** when there is none. Turning around on the runway needs ATC approval, so the pilot asks rather than backtracks on its own (a CLANDF forced rollout is the exception and backtracks unasked). The instructor terminal adds "{CS} stopped on RWY {rwy} with no exit ahead, awaiting TAXI or EXIT". The wording is a convention of the simulator; no FAA text gives one | `RunwayExitPhase.ReportNoExitAhead` → `BuildUnableToExitRequestBackTaxi` + `RouteSoloOrRpoTransmission` | AIM 4-3-21.a |
| An exit instruction naming a taxiway is read back as given: **"exit W3"**, **"exit left W3"**, **"exit right W3"** (spoken "exit left whiskey three"), and a bare `EL`/`ER` as "exit left"/"exit right". The speech-input rules that accept "exit left when able at charlie" or "turn left at charlie" are `SttOnly`: their optional filler words are never spoken back, since the readback renderer speaks every word of the rule it picks | `PhraseologyRules` exit rules via `PhraseologyVerbalizer` | Owner convention: mirror the instruction (no FAA readback form) |
| An instructed exit (`EL`/`ER`/`EXIT <twy>`, with or without `EXP`) the crew cannot make — it would need more than the category's firm braking rate (max effort under `EXP`), it lies past a LAHSO hold-short point, or the aircraft is already inside the turn lead of a late re-target — gets **"unable {twy}."** (spoken "{callsign}, unable whiskey three.", the taxiway spelled). It answers the command in place of a readback, or comes on the tick the rollout finds the exit out of reach, or as the aircraft rolls past the branch; an `EL P`/`ER P` whose taxiway exists only on the other side gets it too. Once per instruction, and the taxiway is then not used for the rest of the landing. "Unable", not "negative": the crew cannot comply with an instruction, which is the P/CG's UNABLE; NEGATIVE is "no", "permission not granted" or "that is not correct" | `BuildUnableToExit` from `LandingPhase.RefuseNamedExit` / `ReportUnableToExit`, `RunwayExitPhase.EvaluateRetarget` / `ReportNamedExitOnlyOffSide`; a refused command carries it on `CommandResult.PilotUnable` | P/CG UNABLE, P/CG NEGATIVE; 7110.65 3-10-9.a (phraseology "IF ABLE, TURN LEFT/RIGHT (taxiway)") |
| An instructed exit with **no connection ahead** on this runway — rolled past, or a taxiway that never touches it — is refused with **"unable, no {twy} ahead."** (spoken "{callsign}, unable, no whiskey three ahead."). Only when no connection lies ahead: an exit that is ahead but out of reach says "unable {twy}." (above). "No {twy} ahead", not "{twy} is behind us": the search finds nothing in both cases and only the first would justify "behind" | `BuildUnableNoExitAhead` from `LandingPhase.RefuseNamedExit` / `RunwayExitPhase.EvaluateRetarget`, via `CommandResult.PilotUnable` | P/CG UNABLE; 7110.65 3-10-9.a |
| Losing the lead while still holding the field couples the loss with the held reference ("lost sight of the traffic, field in sight"); losing everything follows the reason → unable → request structure ("unable the visual, lost sight of …, request vectors" — the template is AIM 5-5-16.a.1's RNAV unable-call, borrowed as a structural analogy, not as visual-approach authority) | `BuildLostSightOfTrafficFieldInSight` / `BuildUnableVisualRequestVectors` | 5-5-11.a.3/.5 |
| Losing the field while following traffic is **silent** on frequency. Product decision to keep the frequency clean: §7-4-3.c.2 NOTE never required the field report on a FOLLOW clearance and the approach legally continues — but note AIM §5-5-11.a.5 literally lists "loses sight of the airport" among the advise-ATC items, so this is a deliberate deviation | `SimulationEngine.TickVisualDetection` field-loss branch (log only) | 7110.65 7-4-3.c.2 |
| An arrival with no approach issued gives a brief **"request approach assignment, {callsign}"** — names neither runway nor approach type (ATC assigns both) | `BuildArrivalApproachRequest` | 4-1-8 |
| Mandatory readbacks keep the runway **L/R/C** suffix and the taxi route up to the **first hold short that stops the aircraft**, and nothing past it. A `TAXI` readback is read off the resolved route in the order route, crossings, hold short: the runway assignment still leads ("runway three zero, taxi via bravo, hold short of taxiway whiskey"), every cleared runway bar on the way is a crossing, the implied crossing of the runway the aircraft is holding short of and an auto-crossed runway included ("taxi via bravo, cross runway two eight left, hold short of runway two eight right"; with auto-cross all, "taxi via bravo, charlie, cross runway two eight left and runway two eight right"), and the first uncleared bar ends the readback: its target is the last thing said, and the taxiways, issued taxiways past the route's end and destination beyond it are left out. The destination runway's own bar is no stop: the runway assignment says it | `PhraseologyVerbalizer.RenderRouteToFirstStop` / `RenderTaxiRouteReadback`, built by `GroundCommandHandler.RouteReadback` as `CommandResult.PilotReadback` | 4-4-7.b.4, 4-3-18, 3-7-2.b |
| Crossings in a `TAXI` readback join in one clause ("cross runway two eight left and runway two eight right"); a crossing an expedited `CROSS` named reads in its own clause with "without delay" ("cross runway two eight left, cross runway two eight right without delay"). An expedited `TAXI EXP` says **one** trailing "without delay", which covers every crossing it reads ("taxi via bravo, charlie, cross runway two eight left and runway two eight right, without delay"; with a hold short, before it: "taxi via bravo, cross runway two eight left, without delay, hold short of runway two eight right") | `PhraseologyVerbalizer.CrossingClauses` | 3-7-2.j |
| A `CROSS` or `RES` in the same transmission as the `TAXI` that names only runways whose bars the TAXI readback already reads as crossings adds **no clause of its own**: `TAXI B C, CROSS 28R` from the 28L bar reads "taxi via bravo, charlie, cross runway two eight left and runway two eight right" once, and `TAXI B C, CROSS 28L` reads 28L once. The handler rebuilds the TAXI readback after the block has run (so a bar the `CROSS` pre-cleared is read as cleared), with the runway ends the commands named ("cross 10L" reads the 28R/10L crossing as runway one zero left). A `CROSS` naming a taxiway, or a runway whose bar lies past the readback's first stop, keeps its own clause, and the TAXI readback then ends silently at that bar ("taxi via bravo, hold short of runway two eight left, cross runway two eight right") | `BlockTaxiReadback`, `GroundCommandHandler.IsCoveredByRouteReadback` → `HandlerReadback.Covers` → `PilotResponder.TakeCovered` | 3-7-2.b |
| A taxi clearance that includes a runway segment says **ON** the runway, never "via" — VIA is for taxiways | `PhraseologyVerbalizer.RenderTaxiAlongRunway` (bypasses the flat `via`-only rule template) / `CaptureFormatter.RunwaySegment` | 3-7-2.a |
| A taxi clearance to a **parking position or spot** reads the destination back — "taxi to parking bravo one two", "taxi via alpha, bravo to spot seven alpha" — for TAXI and TAXIAUTO (parking only), with or without a route. The destination clause sits between the route and any cross / hold-short clause ("taxi via alpha, bravo to parking bravo one two, cross runway two eight right, hold short of runway two five left"). Codes are spelled character by character (dash spoken as "dash"); a pronounceable word of four or more letters is said as the word ("CARGO1" → "cargo one", "HELI1" → "heli one"), and a leading "SPOT" that repeats the noun is dropped ("SPOT7" → "spot seven"). A helipad destination is still read back as "parking" — the verbalizer has no layout access to tell node types apart. STT accepts both orders ("to parking … via …" and "via … to parking …") plus the "gate" / "stand" / "helipad" synonyms, re-joins the spoken name, validates it against the loaded layout's parking / spot / helipad names (`MapContext.DestinationNames`; shape-checked when no layout is loaded), and reads a telephony-shaped tail ("bravo one two", "echo four") as part of the name unless that callsign is on frequency. A `RWY` takeoff-runway assignment cannot be combined with a parking / spot destination (the parser rejects it) | `PhraseologyRules` `taxi to? parking {parking...}` (+ spot / via / synonym forms), `PhraseologyRules.DestinationNouns` + `PhraseologyVerbalizer.RenderTaxiAlongRunway` / `RenderTaxiDestinationClause` / `SpellDestinationName`; `PhraseologyMapper.ResolveDestinationName` / `IsInsideDestinationName` | 3-7-2.a ("TAXI TO (location)", route then hold-short); AIM 4-3-18.b.4.c, 4-4-7.b.2 |
| A taxi clearance with **no route** — the bare `TAXI 1L` an aircraft already at its runway receives (issue #393), and `TAXIAUTO 1L` — reads back "taxi to runway one left" / terminal "taxi to runway 1L". `TaxiAutoCommand` is verbalized as the equivalent path-less `TaxiCommand`, so both share the one rule; STT "taxi to runway one left" maps to `TAXI 01L` | `PhraseologyRules` `taxi to? runway {rwy}` + `PhraseologyVerbalizer.TaxiArgs` (emits `rwy` without a `path`) | 3-7-2 (route normally specified; omitted only when the aircraft is at the bar) |
| A hold-short readback says **"runway"** only for a runway target — a taxiway target is "hold short of charlie", never "hold short of runway charlie" (asserting a runway hold the controller never issued). Any TAXI carrying hold-shorts is direct-rendered for this: the flat rule template's `runway?` literal is spoken unconditionally | `PhraseologyVerbalizer.RenderHoldShortTarget` + the `TaxiCommand` direct-render gate in `VerbalizeCore` | AIM 2-3-5.a.3, 3-7-2.b |
| `CROSS <taxiway>` reads back as **"continue taxi"** — the §3-7 release for a taxiway hold-short. "Cross taxiway bravo" is not codified phraseology (crossing clearances are a runway construct), so a taxiway target in a CROSS list voices the release with no identifier; runway targets in the same list keep their "cross runway …" clause ("cross runway 28L, continue taxi"; "continue taxi, hold short of runway 28R"). A bare `RES` that pre-clears a taxiway or spot hold short ahead of a rolling aircraft (solo) reads back the same "continue taxi", built by the handler as `CommandResult.PilotReadback` and swapped in for the compound's first `RES` clause | `PhraseologyVerbalizer.RenderCross` (taxiway partition); `GroundCommandHandler.TryPreClearNextTaxiwayHoldShort` → `PilotResponder.BuildReadback` | 3-7-2.a.3, 3-7-2.a |
| A `CROSS` or `RES` that releases a bar on a taxi route in progress, when the route goes on past the bar, reads the crossing and the queued route up to the next hold short that stops the aircraft, as a `TAXI` from there would: "cross runway two eight right, continue taxi via bravo, charlie" (the issued taxiways past the route's end follow when nothing stops it first; an expedited crossing adds ", without delay" after the crossing; a further stopping bar ends it with ", hold short of …"). Only the clearance's own taxiways and the route's implied lanes are said (`TaxiRoute.ClearanceTaxiways`, snapshotted; a route that does not know them reads every leg and no tail). Not covered, the readback comes from the command: a complete route, a follow, or a rolling aircraft with an uncleared bar ahead that stops it before the released one | `GroundCommandHandler.RouteContinuationReadback` / `PhraseologyVerbalizer.RenderRouteContinuationReadback`, returned as `CommandResult.PilotReadback` | 3-7-2.b |
| `HS <target>` when the bar is already **inside the stop the aircraft can fly at its firm braking rate** answers "unable to hold short of tango, stopping" (terminal `unable to hold short of T, stopping`) instead of reading the hold back: the crew cannot comply, and the sim brakes to the stop it can make. A direct render gated on `HoldShortPoint.Unable`, never a `PhraseologyRule` — STT must not match a pilot's "unable" as controller input | `PhraseologyVerbalizer.RenderUnableHoldShort` from `PilotResponder.VerbalizeForReadback` (`IsUnmakeableHoldShort`) | P/CG UNABLE; AIM 4-4-7.c (advise ATC when unable to comply); AIM 2-3-5.c |
| A `TAXI` with no destination whose end hold (the hold short of its last taxiway: `TAXI C B`, a bare `TAXI B` on C) a **rolling aircraft can no longer brake for** is answered like an `HS` inside the stopping distance: **"unable to hold short of bravo, stopping"** (terminal `unable to hold short of B, stopping`, callsign in its usual spoken position) in place of the taxi readback. The instructor sees `Unable to hold short of B — stopping`, followed by the route's usual notes in brackets, and the aircraft brakes at its firm rate. No route readback is built for it, so a `CROSS` or `RES` in the same transmission has no taxi readback to cover. A direct render gated on the hold being flagged `Unable` at dispatch, never a `PhraseologyRule` | `GroundCommandHandler.UnableToHoldShortReadback` → `PhraseologyVerbalizer.RenderUnableHoldShort` | P/CG UNABLE; AIM 4-4-7.c (advise ATC when unable to comply); AIM 2-3-5.c |
| At the end of a route that could not reach its destination as cleared (a `RouteIncomplete` hold, issue #461) the pilot reports the hold and asks for the rest of the way: **"holding short of charlie at echo, request further taxi"** (terminal `holding short of C at E, request further taxi.`). The hold that ends a clearance with no destination (`TAXI C B`, a bare `TAXI B` on C) is a `RouteIncomplete` hold and reports the same way. The crew knows the clearance does not take it to where it was sent, so it asks rather than waits; the wording is a convention of the simulator, and no FAA text gives one | `HoldingShortPhase.OnStart` → `BuildHoldingShortTaxi(…, requestFurtherTaxi: true)` | AIM 4-3-18.b (obtain clarification of a clearance not understood) |
| A **located** hold-short (`HS C@J`, issue #358) reads back with the **AT** locative — "hold short of charlie at juliett" — matching the intersection qualifier used across ground phraseology ("cross … at …", "runway … at … intersection departure"); "on" would collide with the route particle. STT accepts "at", "at taxiway", and colloquial "on" | `RenderHoldShortTarget` / `HoldShortCommandArgs` + the located `PhraseologyRules` HS/TAXI variants | 3-7-2.a, 3-7-2.a.10, 3-7-1.d |
| A ground spawn's **ready-to-taxi** call names where it calls from: `{facility}, {location}{, with information A}{, IFR\|VFR to {destination}}, ready to taxi.` — "at gate F8" / "at parking GA13" from a stand; **"on taxiway K"** from a coordinate spawn on a movement-area taxiway (keep the noun "taxiway"; never "at the ramp", which is not movement area, nor "holding on K"); "at the ramp" only when there is neither. When the filed destination is the field it is at, a taxiway call asks for parking instead: **"on taxiway K, with information Alpha, request taxi to parking."** (no intent clause, no "ready to taxi") | `BuildReadyToTaxi` (`ReadyToTaxiLocation.ForStandCall`, `RequestsTaxiToParking`) | AIM 4-2-3.a.1.c, 4-3-18.a.1, 4-3-18.d.1, 4-3-18.d.3 |
| A jet or turboprop on a `PushBack` stand asks for the push: **"Oakland Ground, at gate F8, with information Alpha, request pushback."** (spoken "…, november one two three alpha bravo at gate foxtrot eight, …, request pushback."), with no destination or flight rules, which the after-push ready-to-taxi call carries. Every other stand's ground spawn (a piston, a helicopter, an `Either` or `TaxiOut` stand) calls **"ready to taxi"** and leaves any push to the instructor's `PUSH`; the push itself is the ramp's business (a non-movement area), so the request is a courtesy to ground. An IFR departure with no clearance yet never opens with this call to the student: its clearance request comes first and the push request follows the clearance | `BuildPushbackRequest` / `BuildReadyToTaxi` (`ReadyToTaxiLocation.ForStandCall`), chosen by `InitialCallupCall.RequestsPushback` | 7110.65 3-7-2 NOTE 2 |
| After a scripted **push**, the crew reports ready to taxi from where the push left it: **"pushed back from gate F8, with information Alpha, IFR to Los Angeles Airport, ready to taxi."** (spoken "… pushed back from gate foxtrot eight …"), or **"at spot 5, …, ready to taxi."** when the tow ended on a named spot; the push end's taxiway is never added. The call comes after the crew's setup time (jet 90 s, turboprop 60 s, piston 30 s — a judgement, no published figure). The wording is a convention of the simulator; no FAA text gives one | `HoldingAfterPushbackPhase` → `BuildReadyToTaxi` (`ReadyToTaxiLocationKind.PushedBackFrom` / `Spot`) | AIM 4-3-18.a.1 |
| After a scripted **taxi** stops at a spot or a taxiway hold short, the pilot calls from there 10–20 s after stopping: **"at spot 9, …, ready to taxi."** / **"holding short of C at T41W, …, ready to taxi."** (spoken "holding short of charlie at tango four one whiskey", each name spelled as `BuildHoldingShortTaxi` spells it — the same AT locative as the hold-short report, which this call replaces at that bar). A ramp spot takes the noun **"spot"**, a leading "SPOT" in its name dropped ("SPOT7" → "at spot 7"); a taxi with no destination calls "on taxiway K, …" | `HoldingInPositionPhase` / `HoldingShortPhase` → `BuildReadyToTaxi` (`ReadyToTaxiLocationKind.Spot` / `HoldingShort` / `Taxiway`; `SpotNoun`, `DescribeReadyToTaxiLocation`) | AIM 4-3-18.a, 4-3-18.d.1; P/CG MOVEMENT AREA; pause AIM 4-2-3.e, 4-2-2.a |
| An IFR departure's **clearance request** goes to the lowest position the student works and names its location, ATIS and destination, with no aircraft type and no "request clearance": **"Oakland Clearance, at gate F8, with information Alpha, IFR to Los Angeles Airport."** (spoken "Oakland Clearance, southwest twenty ninety nine, at gate foxtrot eight, …"; "heavy" after the callsign per the wake-class row). It comes only from the stand call, so the location is where the aircraft spawned ("at gate F8", "at spot 9"); an aircraft a scenario preset or a controller already pushed back or taxied counts as cleared and makes its ground call instead. With delivery worked it is addressed to the student's own radio name ("Oakland Clearance"); with ground the lowest it is addressed to "Oakland Ground", since the pilot knows delivery is offline and ground relays the clearance, and the pushback or ready-to-taxi request follows once it is answered. It is never sent to an AI ground, which would not answer it | `BuildClearanceRequest` (IFR) from `InitialCallupCall` | AIM 5-2-6.h.2, 5-2-3.a, 4-2-3.a.1; 4-3-18.d.2 NOTE (ground normally relays the IFR clearance) |
| A VFR departure asks a delivery student for a **VFR departure** naming a direction of flight and its filed altitude, never its destination: **"Oakland Clearance, at gate F8, with information Alpha, VFR departure to the north, at 4500."** (spoken "… at four thousand five hundred"); the altitude clause is left out when none is filed, and a follow-up repeats the same direction. The wording is a convention of the simulator | `BuildClearanceRequest` (VFR; `VfrDepartureDirection`, `CompactAltitude` / `AltitudeWords`) | AIM 4-2-3.a.1 (call structure) |
| An IFR runway spawn lined up at an **untowered** field asks the radar controller for its **release**: **"NorCal Approach, runway 25 at {airport}, ready for departure, request release."** (spoken "NorCal Approach, november five one three sierra juliett, runway two five at {airport}, …"; the airport is its spoken name). The follow-up repeats the same line every 120 s until `REL` or `HFROFF`. It is not the pilot's initial contact: the airborne check-in still follows the takeoff. A VFR spawn there asks for nothing (a release gates only an IFR clearance) | `RunwaySpawnCall.TryRequestRelease` → `BuildReleaseRequest` | 7110.65 4-3-4; AIM 5-2-7.a |
| `EAPP VIS28R` reads back **"expect visual approach runway 28R"** (spoken "…two eight right", 7110.65 4-7-10.d), rendered directly before the rule path; any other `EAPP` reads back its type and variant too, **"expect localizer runway two eight right approach"** (terminal `expect LOC runway 28R approach`), rendered directly from `ApproachReadbackName.ForShorthand` because every `ExpectApproach` rule ties on one capture (`PhraseologyVerbalizer.RenderExpectApproach`; the `ExpectedApproach` set gives a quiet "we'll expect the localizer runway two eight right", a normal "expect the ILS runway …" and a busy "expect RNAV Zulu runway …"); `SEAPP` answers **"Expecting the visual approach runway 28R"**. An airport-level `CVA` at an untowered field reads back the pilot's own pick: **"cleared visual approach to O69, landing runway 29"** (spoken "cleared visual approach to Petaluma, landing runway two nine", the field's name without "Airport"), plus ", left/right traffic" when given. `CVA 28R` reads back **"cleared visual approach runway 28R"** plus ", left/right traffic" and ", follow the traffic" (the RPO terminal names the callsign: ", follow AAL123"); a `CVA {rwy}` in a second or later queued block reads back the same, built from the command by `PilotResponder.VariedBuilderClause`; the `VisualApproachClearance` set gives a quiet "cleared visual approach runway two eight right, {cs}, thanks" and a busy "cleared visual runway two eight right" or "cleared visual two eight right", and `AirportVisualApproachClearance` a busy "cleared visual to Petaluma, landing runway two nine", each keeping the side and follow tail (the Terminal and RPO texts stay canonical); a bare `CAPP` that clears an expected visual reads back the same | `PhraseologyVerbalizer`, `PilotSayBuilder.BuildExpectedApproach`, `ApproachCommandHandler.AirportLevelVisualCleared`, `RunwayVisualCleared` | 4-7-10.d, 7-4-3.c.4 |
| An **approach clearance** reads back the approach as 7110.65 4-8-1 names it, type and variant before the runway: `CAPP`/`CAPPF` **"cleared ILS runway two eight right approach"** (terminal `cleared ILS runway 28R approach`), a variant ahead of the runway ("cleared RNAV Zulu runway two eight right approach", terminal `RNAV Z runway 28R`), and the other types by name: localizer (terminal `LOC`), localizer back course (`LOC BC`), VOR DME, NDB DME, GPS, LDA, TACAN, SDF. A bare `CAPP` that resolves an approach reads it in full. A `CAPP` with `DCT` and a crossing restriction says the route first, "cleared direct sunol, cross sunol at or above three thousand four hundred, cleared ILS …"; with only an AT fix, "at sunol, cleared ILS …"; with an AT fix and a crossing there, "cross sunol at or above …, cleared ILS …" (the crossing names the fix, so the "at" lead drops, 4-8-1.a.4). `CAPPSI` reads **"cleared straight in ILS …"** (terminal `straight-in`); `JAPP`/`JAPPF` **"join the ILS runway …"**; `JAPPSI` **"join the straight in ILS …"**. A circling approach has no runway ("VOR Alpha") and a `CAPP` for one is refused by the sim. A later-block command reads the same from the typed id, else the expected approach, else "cleared approach". In a varied session the spoken final clause takes band rows: busy "cleared ILS two eight right", "cleared straight in ILS runway two eight right", "join the ILS runway two eight right"; a lone quiet CAPP may add ", {cs}, thanks" after "cleared ILS runway two eight right approach" (a chained one never does) | `ApproachCommandHandler.ClearedApproachReadback` / `JoinApproachReadback` (handler `PilotReadback`) + `ApproachReadbackName`; later blocks `PilotResponder.VerbalizeForReadback` → `BuildApproachClause`; crossing words `PhraseologyVerbalizer.CrossFixReadback` | 7110.65 4-8-1, AIM 4-4-7.b |
| `PTAC`/`PTACF` read back the vector, the altitude and the clearance: **"fly heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach"**; with a turn direction (`PTAC L240 …`) **"turn left heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach"**, and a later block is prefixed "then ". The altitude is climb, descend or "maintain" against the present altitude (within 10 ft is "maintain"; a `PA` altitude is the one the aircraft keeps, always "maintain"); `PH` says **"fly present heading"** and takes no direction; without an `L`/`R` the readback names no turn direction (the shorter turn). Spoken variants by band (shown with a turn direction; without one the same shapes say "fly heading" / "heading"): Quiet speaks the canonical or the canonical plus ", {cs}, thanks"; Normal the canonical or "left heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach"; Busy "left heading …, maintain …, cleared ILS runway two eight right", the same with "cleared ILS two eight right", or "left heading …, descending …, cleared ILS two eight right" (a climb says "climb and maintain" / "climbing"; the maintain shape has no progressive row). Only Busy drops "runway" and "approach", as for `CAPP` (`PTAC`/`PTACF` are in `AviationFloor.RunwayClearanceTypes`); the heading, a given direction and the altitude always stay, a non-flight-level altitude never takes "up to" / "down to", and there is no bare altitude or "alright, ". `PH`, a later-block `PTAC` with `PA` and an approach id naming no type or runway stay canonical in every band apart from the Quiet flavour | `ApproachCommandHandler.PtacReadback` / `PtacAltitude` / `PtacVariants`; `PilotResponder.VariedBuilderClause`; later blocks `PilotResponder.BuildApproachClause` | 7110.65 5-9-4.c.2, 5-6-2.a.1; AIM 4-4-7.b.2; P/CG "fly heading" |
| `RPS` (resume published speed) reads back **"resuming published speed"** (terminal "Resume published speed"), a bare "wilco" on a busy frequency like `RNS` ("resuming normal speed"), because it carries no number, and the "wilco" is a candidate only when `RPS` is the whole readback; an `SPD` block's "slowing to" row is withheld when a conditioned `RPS` follows, as for `RNS` | `PhraseologyRules` (`RPS` rule); `PilotResponder.SpeedChangeIsNow`; `AviationFloor.WilcoTypes` | 7110.65 2-4-3 NOTE 1.a, 5-7-1; AIM 4-4-12.f |
| An on-final check-in names the approach with the same type and variant before the runway: **"RNAV Yankee two eight right"** (terminal `RNAV Y 28R`), and the H/P/X/S/Q/T/U types are named, not spelled | `PilotResponder.SpokenApproachName` / `CompactApproachName` → `ApproachReadbackName.TypeAndRunway` | 7110.65 4-8-1 |
| A **pre-issued** clearance (`CLAND`/`TG`/`SG`/`LA`/`COPT` against a still-queued pattern entry) gets the same full readback **including the runway** as a live one — readback verifies the numbers, not the aircraft's position, so being outside the pattern changes nothing | `PilotResponder.ResolveRunwayId` falls back to `Pattern.PendingLandingClearance.RunwayId` | 4-4-7.b.4 |
| An option clearance carrying a pattern runway (`COPT MLT 28L`, `TG 28R MRT 28R 15`) reads back the clearance runway **and** the pattern clause with its runway — terminal "cleared for the option runway 28R, make left traffic runway 28L", spoken "…runway two eight right, make left traffic runway two eight left"; a bare `MLT`/`MRT` keeps the runway-less clause. The modifier assigns the runway the next circuit is flown to, so it is a runway assignment and is read back | `PilotResponder.AppendTrafficPatternClause` (`CompactRunway`/`SpellRunway`) | 4-4-7.b.4 |
| A pre-armed **instruction** with no altitude, vector or runway (`EXT` / `SA` / `MNA` against a queued entry) needs only an acknowledgement, not a clearance-style readback | `PatternCommandHandler.PendingModifierMessage` | 4-4-7.b |

Note the arrival check-in vs approach-request distinction: an IFR arrival checks in with its
altitude and lateral state (and ATIS if held) — it does **not** request a runway (ATC assigns it).
A pure reminder when no approach has been issued is the callsign-only "request approach assignment".

## Authoring a rejection reason

Most `CommandResult(false, …)` messages are **spoken**, not just printed: `CommandRegistry.DefaultProducesPilotUnable`
opts in every command in the `Heading`, `Altitude / Speed`, `Ground`, `Tower`, `Pattern`, `Navigation`, `Hold`,
`Helicopter` and `Approach` categories, and `SimulationEngine.QueueSoloUnableIfNeeded` feeds the message to
`BuildUnable`. That builder
strips the leading "unable" token via `CleanUnableReason` and renders **"unable, {rest}."** — so the reason has
to stand alone as a clause:

| Authored | Spoken | |
|---|---|---|
| `"Unable, no G ahead"` | *unable, no G ahead.* | ✅ |
| `"Unable to pull forward, no room"` | *unable, to pull forward, no room.* | ❌ the verb phrase is left dangling |

`CleanUnableReason` strips en/em dashes alongside the ASCII hyphen, so `"Unable — reason"` is also safe; it did
not always, which is how *"unable, — already turning off at G"* reached the frequency. `PilotResponderTests.BuildUnable_StripsTheLeadingTokenAndAnyDashAfterIt` pins it.

A handler that has the pilot's own words for a refusal sets `CommandResult.PilotUnable`, and `SimulationEngine` speaks that instead of `BuildUnable(message)`. The runway-exit refusals do (`ExitInstructionVerdict.PilotUnable`: `BuildUnableToExit`, `BuildUnableNoExitAhead`), so the spoken form spells the taxiway (AIM 4-2-7) while the terminal line matches what `BuildUnable` would have made of the message.

So does the refusal of a TAXI that would turn an aircraft about on its taxiway when the type's gear does not fit (`TurnAboutFit`, [ground/pathfinder.md](ground/pathfinder.md#where-it-sits--entry-points)): the message is `Unable, no room to turn around on B, request a route ahead` (`GroundCommandHandler.NoRoomToTurnAroundReason`).

`BuildUnableNoRoomToTurnAround` gives the terminal line *unable, no room to turn around on B, request a route ahead.* and speaks *…, unable, no room to turn around on bravo, request a route ahead.*, the taxiway spelled by `PhraseologyVerbalizer.SpellTaxiway`. Without a taxiway (an aircraft standing on none whose stop overruns a hold line) the "on" clause is left out: *unable, no room to turn around, request a route ahead.*

The same phrase is said, unprompted and once, by an aircraft that has no room to turn about on its taxiway: one still rolling when a TAXI turns it about there, with no room short of a runway hold line even at the firm brake rate, an overrun of the node ahead with no straight edge beyond it that has room, a turn about at the node ahead that would leave its pavement with no straight edge beyond it that has room, or no route node a turning diameter from any turn about it could fly.

It stops, holds and says it on the stop's first tick (`GroundNavigator.HoldUnableToTurnAbout`, [ground/navigator.md](ground/navigator.md)).

An aircraft at rest, or creeping at up to its pivot speed, says it too when its turn about would carry any part of it (nose, wingtips, tail; AIM 2-3-5.a.1) past a runway's hold line (`GroundNavigator.HoldUnableFromRest`).

When a runway's hold line is what governs the stop, the refusal names the runway instead of asking for a route: `BuildUnableNoRoomToTurnAroundAtRunway` gives the terminal line *unable, no room to turn around on B, holding short of runway 28R.* and speaks *…, unable, no room to turn around on bravo, holding short of runway two eight right.*

(taxiway spelled by `SpellTaxiway`, runway by `SpellRunway`). An aircraft standing on no taxiway drops the "on B" clause. The runway named is the end the aircraft is nearest.

A TAXI naming two taxiways that meet nowhere the aircraft may join them is refused by `BuildUnableTaxiwaysDoNotConnect`: the terminal line *Unable, C3 and S2 do not connect, request taxi instructions* and the spoken *…, unable, {A} and {B} don't connect, request taxi*, each taxiway spelled by `SpellTaxiway`. The aircraft stays put ([ground/pathfinder.md](ground/pathfinder.md#mandatory-connector-insertion--notification)).

A TAXI naming two taxiways that do meet, but nowhere the route can reach ahead of the aircraft (every junction behind it, or beyond where its walk along the first taxiway dead-ends), is refused by `BuildUnableNoRouteAhead`: the terminal line *Unable, no route from E onto W ahead, request taxi instructions*, with *(intersection only directly behind, would require turn-about)* appended when every junction lies directly behind.

The spoken form is *…, unable, no route from {a} onto {b} ahead, request taxi*, which with the junction behind reads *…, unable, no route from {a} onto {b} ahead, the intersection is behind us, we'd have to turn around, request taxi*.

The aircraft stays put.

A declared crossing the aircraft's type cannot turn out of (the turn from the runway onto the second taxiway exceeds the type's turn limit: KCOS A2 onto C2 for a B738) is refused by `BuildUnableCannotMakeTurn`: the terminal line *Unable, can't make the turn from A2 onto C2, request taxi instructions* and the spoken *{callsign}, unable the turn onto charlie two, request taxi instructions*, only the taxiway turned onto spelled. The aircraft stays put.

A bare TAXI across a runway onto a taxiway with no room straight ahead for the whole aircraft to clear the runway's holding position (a T, a runway centreline or another runway's bar comes first) is refused by `BuildUnableNoRoomToClearRunway`: the terminal line *Unable, no room on C2 to clear 17R/35L, request taxi instructions* and the spoken *{callsign}, unable, no room on charlie two to clear the runway, request taxi instructions* (AIM 4-3-18.d.3, taxiway spelled by `SpellTaxiway`).

The aircraft stays put.

A bare TAXI naming only the taxiway the aircraft is already on (`TAXI B` on B, at most `EXP`/`NODEL`) is refused with `TAXI B rejected: already on B; give a destination or a further taxiway`, an operator-guidance message that is never spoken: the pilot says nothing and the aircraft stays put. A directed TAXI from holding short of a runway keeps the implied crossing and reads it.

A stop that is an overrun, a firm stop that still ends with a part past a hold line, names no runway and says the plain *request a route ahead* form; only an aircraft on no taxiway still names the runway, so the unable has something to name.

That call goes through `RouteSoloOrRpoTransmission` like any unsolicited pilot call: queued for a GND or TWR student in solo training, otherwise a terminal warning, or pilot speech when the RPO shows it.

The grounding is AIM 4-3-18.b: taxi clearances and instructions are predicated on known physical airport conditions, and the pilot in command is the final authority on the aircraft's operation, so a crew that cannot turn its aircraft about on its taxiway declines and asks for a route ahead.

That strip covers the **leading** token and the string's **edges** only. An *interior* dash survives into TTS, so
`"Already at 1,400 — nothing to expedite"` is spoken with the dash intact. Write two clauses joined by a comma
instead. For the same reason, format altitudes in a spoken reason as bare integers (`1400`) — a `:N0` thousands
separator hands a comma to the synthesiser mid-number.

## Builder catalog

Grouped by trigger. All return `PilotSpeechText`; follow/traffic builders set `RpoTerminal`.

- **Readbacks** — `BuildReadback(compound, aircraft)` (rule-driven, the bulk of readbacks).

  `BuildReadbackAsApplied` swaps in what the handler applied — the effective `TAXI`, and a `PUSH`/`PUSHM`'s own sentence or a pre-clearing `RES`'s "continue taxi" from `CommandResult.PilotReadback` (a `HandlerReadback(Command, Speech)` that replaces only the first clause of its own command type; the push text is the RSP text less its RPO notes, and a queued push is verbalized from its parse in the same wording; see [command-handlers.md](command-handlers.md#handler-built-readbacks)).

  `BuildUnable` (rejected command, gated by `CommandDefinition.ProducesPilotUnable` — see the authoring rule below). `BuildUnableAirspaceAltitude` (an assigned altitude that would enter un-cleared Class B/C — AIM §5-5-6.a.3 makes advising ATC the pilot's obligation, and the line names the altitude they *can* hold).

  `BuildUnableSpeedLimit(aircraft, SpeedLimitKind, figure)` (a controller speed past the type's Vmo/Mmo or below its final approach speed, set on `CommandResult.PilotUnable` by `FlightCommandHandler.RefuseUnflyableSpeed` — AIM 4-4-12.h and 7110.65 §5-7-3.a NOTE 2: a pilot unable to comply with a speed adjustment advises ATC, and the line names the speed they *can* fly).

  Three forms: *unable, 290 knots is the fastest we can do.* (spoken *…, unable, two niner zero knots is the fastest we can do.*), *unable, Mach .82 is the fastest we can do.* when Mmo governs above the IAS/Mach crossover (spoken *…, unable, Mach point eight two is the fastest we can do.*), and *unable, 140 knots is the slowest we can do.* The figure is floored so it is always one the aircraft can fly, and the aircraft keeps its speed.

  `BuildMaximumForwardSpeed` (`SMAX`, 7110.65 §5-7-2 "MAINTAIN MAXIMUM FORWARD SPEED": the pilot reads back the instruction, which has no number: *maximum forward speed.* in both the terminal and spoken forms).

  `BuildUnableGoAroundRequestVectors` (a bare `GA` outside the published final approach fix on an instrument approach: *unable go-around, we're in the procedure turn, request vectors.*, or *…we're in the hold…*, or *…we're outside the final approach fix…*).

  The published missed approach does not start from where the aircraft is, so the pilot refuses and asks for a vector in the AIM §5-5-16.a.1 unable, reason, request structure. `CommandDispatcher.RefuseBareGoAroundOutsideFaf` sets it as `CommandResult.PilotUnable`.

  `BuildArmedFollowClause` (a `FOLLOWG` armed at a runway bar reads back `follow the traffic, hold short of runway 1R`: the follow form never names the leader, and the hold-short is read back per AIM 4-3-18a.9(c)).

  `FOLLOWG` reads back `follow the traffic` and `GIVEWAY` `behind the traffic` (7110.65 §3-7-2.a FOLLOW / BEHIND (traffic)), built directly in `VerbalizeForReadback` (`BuildFollowGroundClause`, `BuildGiveWayClause`); their `RpoTerminal` names the traffic (`follow N2468K`, `behind UAL456`), never by type and with no position.

  An airborne `FOLLOW` reads back `follow the traffic` too (7110.65 §7-6-7.a "FOLLOW (description)"; also tower sequencing, §3-8-1), built directly as `BuildFollowAirborneClause`; its `RpoTerminal` is `follow N2468K`, or plain `follow the traffic` for a bare `FOLLOW` that names no traffic.

  An airborne FOLLOW is accepted only after the pilot has reported the traffic in sight, so the readback does not restate it. The spoken form takes the `FollowTrafficAirborne` rows: a quiet "we'll follow the traffic" and a busy "following the traffic, {cs}", while `FOLLOWG` and `GIVEWAY` stay canonical, so a busy airborne follow and a ground one word it differently.

  As a condition prefix (`GIVEWAY UAL456 TAXI …`) GIVEWAY leads the readback: `behind the traffic, taxi to runway 1R via A A1` (`FormatCondition` / `FormatConditionTerminal` / `FormatConditionForRpo`).

  `BuildReadback` assembles a third body beside Terminal and Tts from each clause's `TerminalForRpo`, with the same condition leads and joins, and sets `RpoTerminal` only when it differs from Terminal. `WithBravoClearance` (solo: appends `, cleared into/through the bravo` to a readback that implies a Class B clearance — see airspace-database.md).
- **Airspace requests** — `BuildBravoClearanceRequest` (`request clearance into/through the bravo.`; into when the destination sits in that Class B at the surface — the controller's CLEARED THROUGH / TO ENTER split, 7110.65 §7-9-2.a; AIM §3-2-3.d.2).
- **Initial contact / check-in** — `BuildAirborneCheckIn` → `BuildIfrAirborne` / `BuildVfrAirborne`; `BuildReadyToTaxi` (from a `ReadyToTaxiLocation`: stand, spot, taxiway, "pushed back from", hold short, or the ramp); `BuildPushbackRequest` (a jet or turboprop's stand call from a push-back stand); `BuildClearanceRequest` (to the lowest position the student works: IFR, or a VFR departure with direction and altitude to a delivery student); `BuildReleaseRequest` (an untowered runway spawn's release request to the radar controller); `BuildClosedTrafficRequest`; `BuildArrivalApproachRequest`.
- **Position / pattern reports** — `BuildMidfieldDownwindReminder`, `BuildShortFinalReminder`,
  `BuildTurningLegReport`, `BuildMidfieldDownwindReport` ("midfield left downwind runway 28R", or "left downwind runway 28R, past midfield" / "…, abeam the numbers" by position), `BuildMileFinalReport`, `BuildAtFixReport` (armed by `REPORT`).
- **Tower / ground** — `BuildHoldingShortTaxi`, `BuildHoldingShortCrossing`, `BuildClearOfRunwayText`, `BuildTaxiInRequest` (the arrival's call to ground after the exit — "clear of runway 28R at W, taxi to gate 29", AIM 4-3-21.c).

  It and `BuildReadyToTaxi`'s "at gate F8" name a stand the same way (`StandNoun`): no noun for a name that reads as a word ("at kilo ramp", "taxi to signature"), "gate" for a gate name (`ArrivalParkingPicker.IsGateName`: F8, A13R, 29), "parking" otherwise, the terminal keeping the name as written and the TTS spelling it with `SpellDestinationName` — "gate foxtrot eight".

  A ramp spot in `BuildReadyToTaxi` takes "spot" instead, its leading "SPOT" dropped.

  The group also holds `BuildUnableToExit` ("unable W3.": an instructed exit the crew cannot make), `BuildUnableNoExitAhead` ("unable, no W3 ahead.": no connection of it ahead), `BuildUnableToExitRequestBackTaxi` (stopped on the runway with no exit ahead), `BuildUnableNoRoomToTurnAround` ("unable, no room to turn around on B, request a route ahead.": a type whose gear does not fit refusing a TAXI that would turn it about), `BuildUnableNoRoomToTurnAroundAtRunway` ("unable, no room to turn around on B, holding short of runway 28R.": a turn about that would carry a part past a runway hold line), `BuildGoingAround`, `BuildApproachingMinimumsNoLandingClearance`.
- **Visual acquisition** — `BuildTrafficInSight`, `BuildFieldInSight`, `BuildLostSightOfTraffic`,
  `BuildLostSightOfField`, `BuildLostSightOfTrafficFieldInSight` (traffic lost, field held — the §7-4-3.c.3
  separation handback), `BuildUnableVisualRequestVectors` (visual ended away from the runway — level-off +
  request), `BuildGoingAroundLostVisualReference` (visual ended on short final — reason spoken per §5-5-5.a.2).
- **Military routes / refueling** — `BuildClearedIntoMilitaryRouteClause`,
  `BuildMaintainRouteAltitudesClause`, `BuildClearedOutOfMilitaryRouteClause`,
  `BuildClearedToConductRefuelingClause`, plus `PilotSayBuilder.BuildExitFixEstimate` answering
  `SAYEXIT`. These are builders rather than rules on purpose — their `PhraseologyRule`s exist but
  are `SttOnly`. Rule selection happens per canonical type without seeing which arguments are
  present, so `CMTR`'s three forms collapse onto the longest pattern and an assigned altitude is
  silently read back as "maintain route altitudes"; and `RenderPattern` joins tokens with spaces,
  so a pattern cannot carry the comma before an altitude clause. `MTRA` and `XMTR` have no choice
  either way: neither carries its designator, which lives in aircraft state.
- **Follow / sequencing** (all `RpoTerminal`) — `BuildUnableToMaintainSeparation`, `BuildSequenceTightTurningBase`, `BuildSTurnsForSpacing` (also said, at most once a minute, when a free-pursuit follower starts an S-turn excursion for spacing: "S-turning for spacing behind the traffic", AIM §4-3-5).

  `BuildUnableNoRouteToFollow` is a taxi follower's (`FOLLOWG`) one call when its plan at runtime finds no taxi route onto its traffic's path and it holds: Terminal "Unable to follow traffic, no taxi route to its path, request taxi instructions", Tts "{callsign}, unable, no route to follow traffic, request taxi.", RpoTerminal "Unable to follow {lead}, no taxi route to its path, request taxi instructions". Said once per follow, to a solo student on ground or tower (`SoloPositionsGround`).

  The same call is made, once, when a running follow's re-plan after the lead's re-route finds the follower ahead of its lead, and when the follower holds standing on the lead's route ahead of it (waiting for it to pass); a first plan that finds the follower ahead holds silently.

  `BuildUnableRunwayNotOnRoute` is the refusal of a `CROSS` or `RES CROSS` that names a runway off a `FOLLOWG` follower's route (its follow route or its lead's path ahead): Terminal "Unable, runway 28L is not on our route, request taxi instructions", Tts "{callsign}, unable, runway two eight left isn't on our route, request taxi.", RpoTerminal "unable, runway {rwy} is not on the route following {lead}". The follower stays held and keeps its follow.

  `RES` is refused through `BuildUnable(aircraft, "request taxi instructions")` (`GroundCommandHandler.RefusesResumeTaxi`, from `CommandDispatcher` and `TryResumeTaxi`) only when the hold in position is the one a `HOLD`-broken-off follow ends in (`HoldingInPositionPhase.EndsBrokenOffFollow`) with nothing after it, or at a non-departure hold short whose only successor is that hold: the pilot says "unable, request taxi instructions."

  A departure-runway bar keeps the hold short's own refusal ("issue CTO or LUAW"), and a lead-gone stop or the end of a finished `TAXI` is no break-off.

  `BuildLostTrafficHolding` is a taxi follower's one call when its lead is lost with no legal way back onto its assigned route and nothing left to drive, as it holds in position: Terminal "Lost sight of traffic, holding position, request taxi instructions", Tts "{callsign}, lost the traffic, holding, request taxi.", RpoTerminal "Lost sight of {lead}, holding position, request taxi instructions". Said once, to a solo student on ground or tower (`SoloPositionsGround`).

  A follower unjoinable for a reason other than the unable call — a route lost to a backward start, or a first plan ahead of its lead — has said nothing, so it still says this once its lead is gone. A follower with no assigned route left to lose, and one that has said `BuildUnableNoRouteToFollow` — it cannot follow at all — says this one nothing; one holding after saying `BuildHoldingShortNotClear` does not either.

  `BuildHoldingShortNotClear` is a taxi follower's one call when its clearing route ran out before its tail was past the hold line, so it holds short of the runway its way on enters while not clear of the runway it was clearing: Terminal "Holding short of runway {next}, not clear of runway {cleared}", Tts "{callsign}, holding short of runway {next}, not clear of {cleared}.".

  With no runway ahead of it — no edge out of the route's end enters another runway — `BuildHoldingPositionNotClear` says instead "Holding position, not clear of runway {cleared}" (Tts "{callsign}, holding position, not clear of {cleared}.").

  `BuildHoldingShortNotClearOfRunwayText` is the same report with the taxiway the aircraft stands on: Terminal "holding short of runway {other} at {taxiway}, not clear of runway {crossed}.", Tts "{callsign}, holding short of runway {other} at {taxiway}, not clear of runway {crossed}.".

  It is said by an exit that stops at another runway's bar with its tail inside the vacated runway (`HoldingAfterExitPhase`, to Tower), by a `CLRWY` cut back at the other runway's bar (`HoldingInPositionPhase`, to Tower), and by the hold a `HOLD` leaves a crossing or a broken-off `FOLLOWG` follow in (`HoldingInPositionPhase.AfterFollowBreakOff`, to a Ground or Tower solo student, `SoloPositionsGround`), when its nose rests short of the other runway's bar.

  `BuildHoldingPositionNotClear` is likewise said by a `CLRWY` or a break-off hold whose nose is past the other runway's bar, or with no other runway (to the same positions). The break-off hold makes its own not-clear call, which a `HOLD` does not silence as it does the traffic-ahead call below. A crossing's tail still inside the crossed runway reports it, and one clear of it is silent.

  A nose counts as past the bar by `ClearRunwayPhase.IsNosePastBar` with `NosePastBarSlackFt` (3.82 ft), the check `CLRWY` uses. Runway ends are named by the nearest end.

  Each runway is named by the end nearest the aircraft (`RunwayCrossingEnd.Nearest`), as the hold-short texts name one — the crew reports the runway it cannot clear (AIM 4-3-21.b). Either goes to a solo student on ground or tower (`SoloPositionsGround`), once per hold, and stands in for `BuildLostTrafficHolding` in that hold: one line, the runway one.

  `BuildHoldingShortTrafficAhead` is the one call of an aircraft cleared across a runway that holds at the near line because the far side lacks room (`RunwayCrossingFarSideGate`): Terminal "Holding short of runway {X}, traffic ahead", Tts "{callsign}, holding short of runway {X}, traffic ahead.". The runway is the end nearest the aircraft (`RunwayCrossingEnd.Nearest`), and no form names the traffic.

  Said once per bar, to a solo student on ground or tower (`SoloPositionsGround`) or as an RPO's pilot speech.

  `BuildHoldingShortForFollowTraffic` is the one call of an aircraft cleared across a runway with a `FOLLOWG` armed behind the crossing that holds at the bar for its lead, whose route merges with its own beyond it, to pass: Terminal "Holding short of runway {X}, waiting for the traffic to pass", Tts "{callsign}, holding short of runway {X}, waiting for the traffic to pass.", RpoTerminal "Holding short of runway {X}, waiting for {lead} to pass".

  The runway is the end nearest the aircraft, as the hold-short texts name it.

  It is made only for a real wait (the merge ahead of the lead, or the lead moving below 1 kt), judged again each second while the wait lasts and said at most once per wait; the short wait for a lead moving away to clear its tail past the merge is silent. Routed as a solo or RPO transmission, with the warning "{callsign} cleared across RWY {X}, holding for the follow traffic".

  `BuildHoldingPositionNotClearTrafficAhead` is the not-clear call of an aircraft brought to rest on the runway by traffic with part of it inside the far marking: Terminal "Holding position, not clear of runway {X}, traffic ahead", Tts "{callsign}, holding position, not clear of {X}, traffic ahead.".

  Said once per crossing, about three seconds after it stops; a GIVEWAY does not silence it, a HOLD does (a HOLD that ends the crossing at rest makes the break-off hold's own not-clear call instead, above).

  A HOLD readback mid-crossing says "holding position on runway {X}" (`BuildHoldingPositionOnRunwayClause`) instead of the bare hold, when any part of the aircraft is past the near marking or the HOLD cannot stop it short of it, also in the taxi stretch just before the crossing phase starts.

  `BuildUnableToFollowExtendingDownwind` ("unable to follow the traffic, extending downwind, request base turn": a follower that could not build spacing 2 nm past the lead's base turn, or nearing the final, ends the follow and holds an extended downwind — AIM §5-5-12.a.2, it cannot keep its own separation).

  `BuildTurningDownwindForSpacing` is the one call of a follower level with or ahead of its lead on base or final, or stalled alongside it, or breaking off its own base for spacing, as it turns out to the downwind heading: Terminal "turning downwind for spacing behind the traffic, request base turn.", Tts "{callsign}, turning downwind for spacing behind the traffic, request base turn.", RpoTerminal "turning downwind for spacing behind {lead}, request base turn."

  The maneuver comes first, then the request, AIM §4-3-5 — a pilot maneuvering for spacing advises the controller — and §4-3-4, no cutting in on final; "behind the traffic" because the lead may still be level or behind when the turn starts.

  A widened base and a base break-off say nothing of their own. A follower never reports "unable to catch up": a lead that outpaces it is increasing separation. Its self-generated break-offs are loss of visual contact (`BuildLostSightOfTraffic`, AIM §5-5-12.a.2 / §4-4-14 NOTE), the extension limit above and the turn-out.

  FOLLOW's refusals (`CommandDispatcher.DepartingLeadRefusal`, `FollowFromLegRefusal`, `TryRouteRunwaylessLead`, `LeadBoundElsewhereRefusal`, `FollowSequenceRefusal`) follow the reason-then-request rule: "Unable, {T} is departing, request vectors"; "Unable, on {base|final} for runway {rwy}, request vectors to follow {T}".

  The others are "Unable, on {upwind|crosswind|downwind|base|final|approach} for runway {rwy}, {T} is not ahead of us, request vectors"; "Unable, {T} is on the ground"; "Unable, {T} is inbound to {APT}, request vectors". Grounding is in `docs/approach-and-pattern-geometry.md`.

## Number & identifier spelling

| Helper | Output | Used for |
|--------|--------|----------|
| `AtcNumberParser.CardinalWord(n)` | cardinal: 5 → "five", 12 → "twelve" | "N-mile final" reports |
| `PilotResponder.SpellDistanceDigits(n)` | digit-by-digit: 10 → "one zero" | position-relative check-in distance ("one zero miles west") — pinned by `M102AirborneCheckInTests` |
| `PhraseologyVerbalizer.SpellRunway` | drops the padding leading zero: `08R` → "runway eight right" | runway designators (7110.65 §2-4-18.10) |
| `AtcNumberParser.AltitudeToWords` / `CompactAltitude` | "five thousand" / `5000` | altitudes |
| `PhraseologyVerbalizer.SpellMilitaryRoute` | letters then group form: `IR531` → "i-r five thirty one" | military **training** route designators (§2-5-1.f) |
| `PhraseologyVerbalizer.SpellRefuelingTrack` | number only, no letters: `AR312` → "three twelve" | aerial **refueling** tracks (§9-2-13 says "REFUELING ALONG (number) TRACK") |
| `PhraseologyVerbalizer.SpellRouteString` | `V495 SEA` → "victor four ninety five, Seattle VORTAC" | a whole route of flight (§2-5-1.a); airways phonetic + group form, fixes keep their own pronunciation |

Headings keep all three digits including leading zeros (`HeadingDigits`, §2-4-18.8) — a separate
path; don't route headings through the distance/cardinal helpers.

## Pitfalls

- **Don't put the lead/target callsign in `Terminal` or `Tts`** for traffic/follow calls — it goes
  in `RpoTerminal` only. The router resolves `TerminalForRpo` for the RPO branch.
- **Don't regex-strip the TTS form to make the terminal form.** Build both directly. If the RPO
  view needs a diagnostic the solo view shouldn't have, set `RpoTerminal`.
- **TAXI has two independent readback outputs — don't conflate them.** The **RSP** (controller-facing
  `CommandResult.Message`) is built in `GroundCommandHandler.BuildTaxiReadback` — `"Taxi via " + route.ToSummary(turnHints, clearedRunways, ReadbackTaxiwayFilter(…))` (or `"Already at runway 1L — holding short (runway 1L assigned)"` for the segment-less bare-`TAXI <rwy>` route, issue #393)
  — from the *resolved* route, fed turn hints via `BuildTurnHintMap(taxi)`, but naming only the clearance's taxiways and the occupied one, so it says the clearance as issued as the pilot does (added ramp taxilanes and the implied runway-entry connector are silent; an uncleared movement-area taxiway is warned `taxiing via X — not in the route issued`). The **TTS/Terminal** pilot
  readback is a separate call: for a `TAXI` (not `TAXIAUTO`) with a route the handler builds it from the *resolved* route, cut at the first stopping hold short (`GroundCommandHandler.RouteReadback` → `PhraseologyVerbalizer.RenderTaxiRouteReadback`, returned as `CommandResult.PilotReadback`); otherwise (a path-less or node-only clearance, `TAXIAUTO`) `PilotResponder.BuildReadback` → `PhraseologyVerbalizer` builds it from the *parsed*
  `TaxiCommand`. Either way a turn hint renders as "right on J" / "left on J" (`TaxiTurn`), never "right turn"/"onto".
  Changing one does not change the other.
- **Don't merge `SpellDistanceDigits` into `CardinalWord`.** They're different phraseology
  (digit-by-digit position distance vs cardinal mile-final) and the airborne-check-in form is
  test-pinned.
- **Cross-repo:** `BuildTrafficInSight` / `BuildLostSightOfField` are also called from yaat-server's
  `TickProcessor` maintain-contact loop. A signature change there must land in both repos.
- **Aviation changes need review.** Anything touching what a pilot says is aviation logic — use
  `aviation-sim-expert` (or a direct steer from the domain owner) and cite the AIM/7110.65, don't
  guess. A narrow, explicit instruction from the domain owner doesn't need a separate second opinion.
